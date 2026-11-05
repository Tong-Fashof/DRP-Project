using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.Cleansing
{

    // Cleansing Pipeline (Step 2-6): Orchestrator ของ PBI-03 ถึง PBI-08
    // รวมขั้นตอน "โหลดเกณฑ์จาก M_LOV -> เรียก Engine -> แสดงสรุป" ของแต่ละ Step ให้ Program.cs เรียกทีละบรรทัด
    // (รูปแบบเดียวกับ NetDemandPreprocessor.Run: รับ connection ที่เปิดอยู่แล้วจากข้างนอก)
    // ไม่มี Business Logic (สูตรอยู่ใน Engine) และไม่ยุ่ง SQL (DB I/O อยู่ใน OutlierCleansingRepository.cs)
    // ไม่ catch Exception: ปล่อยให้ Main ใน Program.cs ดักและ log ที่เดียว
    public static class CleansingPipeline
    {
        // ======================================================
        // STEP 2 (PBI-03, PBI-04): โหลดเกณฑ์ ADI_THRESHOLD / PROTECT_TREND_SEASON จาก M_LOV
        // -> โหลดยอดขาย 24 เดือนต่อ SKU -> Gate 1/2 คัดกรอง KEEP / PENDING -> แสดงสรุป
        // คืน candidates ทั้งหมด (KEEP + PENDING): Normality ใช้เฉพาะ PENDING | Finalize ใช้เฉพาะ KEEP
        // ======================================================
        public static List<SkuCleansingCandidate> RunGates(SqlConnection connection, string? targetWarehouseCode)
        {
            // เกณฑ์จาก M_LOV (ไม่พบหรือแปลงไม่ได้ -> ใช้ค่า Default และมี Warning ใน log)
            decimal adiThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "ADI_THRESHOLD", 1.320000m);
            decimal protectTrendSeason = OutlierCleansingRepository.LoadLovDecimal(connection, "PROTECT_TREND_SEASON", 1.000000m);
            // โหลดยอดขาย 24 เดือนต่อ SKU (เก็บใน Demand24M[month_no - 1])
            List<SkuCleansingCandidate> candidates = OutlierCleansingRepository.LoadCandidates(connection, targetWarehouseCode);
            // Gate 1 ทุก SKU ก่อน | เฉพาะที่ยัง PENDING จึงเข้า Gate 2 (SKU ที่ KEEP จาก Gate 1 ถูกล็อก)
            CleansingEngine.CleansingCenterAll(candidates, adiThreshold, protectTrendSeason);
            // แสดงสรุปผลหน้าจอ (Console)
            PrintCleansingSummary(candidates, protectTrendSeason);
            return candidates;
        }


        // ======================================================
        // สรุปผล Gate 1 & 2: จำนวน KEEP / PENDING แยกตามเหตุผลและตามคลัง
        // ======================================================
        private static void PrintCleansingSummary(List<SkuCleansingCandidate> candidates, decimal protectTrendSeason)
        {
            Console.WriteLine($"Total   : {candidates.Count}");
            Console.WriteLine($"KEEP    : {candidates.Count(c => c.CleansingStatus == "KEEP")}");
            Console.WriteLine($"PENDING : {candidates.Count(c => c.CleansingStatus == "PENDING")}");
            Console.WriteLine($"PROTECT_TREND_SEASON = {protectTrendSeason}");

            Console.WriteLine();
            Console.WriteLine("--- Gate 1 & 2 : แยกตามเหตุผล ---");
            // CleansingReason เป็น null = ยังเป็น PENDING | เรียงจากจำนวนมากไปน้อย
            foreach (var g in candidates.GroupBy(c => c.CleansingReason ?? "PENDING").OrderByDescending(g => g.Count()))
                Console.WriteLine($"{g.Key,-40} {g.Count(),8:N0}");

            Console.WriteLine();
            Console.WriteLine("--- Gate 1 & 2 : แยกตามคลัง ---");
            // แยกตามคลัง เรียงรหัสคลังแบบ Ordinal (ไม่ขึ้นกับภาษาของเครื่อง)
            foreach (var g in candidates.GroupBy(c => c.warehouse_code).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int k = g.Count(c => c.CleansingStatus == "KEEP");
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | KEEP {k,7:N0} | PENDING {g.Count() - k,7:N0}");
            }
        }


        // ======================================================
        // PBI-05: โหลดเกณฑ์ Normality 3 ตัวจาก -> ทดสอบเฉพาะ SKU ที่ PENDING
        // -> แยกสาย Normal (PBI-06 Sigma) / Non-normal (PBI-07 IQR) แล้วแสดงสรุป
        // คืน (normal, nonNormal): normal ส่งต่อ PBI-06 แล้ว | nonNormal ต้องส่งต่อ PBI-07
        // ======================================================
        public static (List<SkuNormalityResult> Normal, List<SkuNormalityResult> NonNormal) RunNormality(SqlConnection connection, List<SkuCleansingCandidate> candidates)
        {
            decimal pValueThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "JB_PVALUE_THRESHOLD", NormalityEngine.DefaultPValueThreshold);
            decimal skewLimit = OutlierCleansingRepository.LoadLovDecimal(connection, "SKEW_NORMAL_LIMIT", NormalityEngine.DefaultSkewLimit);
            decimal kurtLimit = OutlierCleansingRepository.LoadLovDecimal(connection, "KURT_NORMAL_LIMIT", NormalityEngine.DefaultKurtLimit);

            // ทดสอบ Normality เฉพาะ SKU ที่ยัง PENDING หลัง Gate 1/2 | สาย KEEP ไม่ผ่านขั้นนี้ ไปรวมที่ Finalize โดยตรง
            var pending = candidates.Where(c => c.CleansingStatus == "PENDING").ToList();
            List<SkuNormalityResult> results = NormalityEngine.EvaluateAll(pending, pValueThreshold, skewLimit, kurtLimit);
            var (normal, nonNormal) = NormalityEngine.Partition(results);   // normal -> PBI-06, nonNormal -> PBI-07

            Console.WriteLine();
            Console.WriteLine("--- Normality Testing :  ความเบ้, ความโด่ง, คะแนนการทดสอบทางสถิติ ---");
            /* ข้อความความ PBI -05: กฎเหล็ก Triple Gate(ต้องผ่านพร้อมกันทั้ง 3 ข้อ 100 %):
                jb_p_value ≥ 0.0500(อ้างอิง Rules!B7) และ
                | skewness | ≤ 0.5000(อ้างอิง Rules!B8) และ
                | kurtosis | ≤ 1.0000(อ้างอิง Rules!B9)
                ผ่านครบทั้ง 3 ข้อ: กำหนด IsNormal = true → ส่งต่อเข้าสู่ Sigma Capping(PBI - 06)
                ตกเกณฑ์ข้อใดข้อหนึ่ง: กำหนด IsNormal = false → ส่งต่อเข้าสู่ IQR Capping(PBI - 07)
            */
            Console.WriteLine($"เกณฑ์: p >= {pValueThreshold}, |skew| <= {skewLimit}, |kurt| <= {kurtLimit}");
            Console.WriteLine($"Input (PENDING)         : {pending.Count}");
            Console.WriteLine($"Normal     -> PBI-06 Sigma : {normal.Count}");
            Console.WriteLine($"Non-normal -> PBI-07 IQR   : {nonNormal.Count}");

            Console.WriteLine();
            Console.WriteLine("--- Normality : แยกตามคลัง ---");
            foreach (var g in results.GroupBy(r => r.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int n = g.Count(r => r.IsNormal);
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | Normal {n,7:N0} | Non-normal {g.Count() - n,7:N0}");
            }

            return (normal, nonNormal);
        }


        // ======================================================
        // PBI-06: โหลดเกณฑ์ CV_THRESHOLD / SIGMA_CAP_MULTIPLIER จาก M_LOV (ไม่พบ -> Default 0.5 / 3.0)
        // -> ตัดยอดส่วนเกินสาย Normal ด้วย 2SIGMA (CV <= threshold) หรือ 3SIGMA (CV > threshold)
        // ผลลัพธ์ List<CleansedDemandSeries> ต้องส่งต่อให้ PBI-08 (Cleansed Dataset Finalize Engine)
        // ======================================================
        public static List<CleansedDemandSeries> RunSigmaCapping(SqlConnection connection, List<SkuNormalityResult> normal)
        {
            decimal cvThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "CV_THRESHOLD", SigmaCappingEngine.DefaultCvThreshold);
            decimal sigmaCapMultiplier = OutlierCleansingRepository.LoadLovDecimal(connection, "SIGMA_CAP_MULTIPLIER", SigmaCappingEngine.DefaultSigmaCapMultiplier);

            List<CleansedDemandSeries> cleansed = SigmaCappingEngine.CapAll(normal, cvThreshold, sigmaCapMultiplier);

            // สถิติสรุป: จำนวน SKU ที่มีอย่างน้อย 1 เดือนถูกตัด และยอดรวมที่ถูกตัดทอนทั้งหมด
            int cappedSkus = cleansed.Count(c => c.QtyReduced24M.Any(q => q > 0m));
            decimal totalReduced = cleansed.Sum(c => c.QtyReduced24M.Sum());

            Console.WriteLine();
            Console.WriteLine("--- Sigma Capping : ตัดยอดส่วนเกินสาย Normal ---");
            Console.WriteLine($"เกณฑ์: CV_THRESHOLD = {cvThreshold}, SIGMA_CAP_MULTIPLIER = {sigmaCapMultiplier}");
            Console.WriteLine($"Input (Normal)          : {normal.Count}");
            Console.WriteLine($"2SIGMA                  : {cleansed.Count(c => c.CleansingMethod == SigmaCappingEngine.Method2Sigma)}");
            Console.WriteLine($"3SIGMA                  : {cleansed.Count(c => c.CleansingMethod == SigmaCappingEngine.Method3Sigma)}");
            Console.WriteLine($"SKU ที่มียอดถูกตัด        : {cappedSkus}");
            Console.WriteLine($"ยอดรวมที่ถูกตัดทอน        : {totalReduced:N4}");

            Console.WriteLine();
            Console.WriteLine("--- Sigma Capping : แยกตามคลัง ---");
            foreach (var g in cleansed.GroupBy(c => c.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int capped = g.Count(c => c.QtyReduced24M.Any(q => q > 0m));
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | 2SIGMA {g.Count(c => c.CleansingMethod == SigmaCappingEngine.Method2Sigma),7:N0} | 3SIGMA {g.Count(c => c.CleansingMethod == SigmaCappingEngine.Method3Sigma),7:N0} | ถูกตัด {capped,7:N0}");
            }

            return cleansed;
        }

        // ======================================================
        // PBI-07: โหลดเกณฑ์ SKEW_MODERATE_LIMIT / KURT_MODERATE_LIMIT จาก M_LOV (ไม่พบ -> Default 1.0 / 3.0)
        // -> ตัดยอดส่วนเกินสาย Non-normal ด้วย 1.5IQR (เหวี่ยงปานกลาง) / 3.0IQR (เหวี่ยงรุนแรง) / KEEP (IQR = 0)
        // ผลลัพธ์ List<CleansedDemandSeries> ต้องส่งต่อให้ PBI-08 (Cleansed Dataset Finalize Engine)
        // ======================================================
        public static List<CleansedDemandSeries> RunIqrCapping(SqlConnection connection, List<SkuNormalityResult> nonNormal)
        {
            decimal skewModerateLimit = OutlierCleansingRepository.LoadLovDecimal(connection, "SKEW_MODERATE_LIMIT", IqrCappingEngine.DefaultSkewModerateLimit);
            decimal kurtModerateLimit = OutlierCleansingRepository.LoadLovDecimal(connection, "KURT_MODERATE_LIMIT", IqrCappingEngine.DefaultKurtModerateLimit);

            List<CleansedDemandSeries> cleansed = IqrCappingEngine.CapAll(nonNormal, skewModerateLimit, kurtModerateLimit);

            // สถิติสรุป: จำนวน SKU ที่มีอย่างน้อย 1 เดือนถูกตัด และยอดรวมที่ถูกตัดทอนทั้งหมด
            int cappedSkus = cleansed.Count(c => c.QtyReduced24M.Any(q => q > 0m));
            decimal totalReduced = cleansed.Sum(c => c.QtyReduced24M.Sum());

            Console.WriteLine();
            Console.WriteLine("--- IQR Capping : ตัดยอดส่วนเกินสาย Non-normal ---");
            Console.WriteLine($"เกณฑ์: SKEW_MODERATE_LIMIT = {skewModerateLimit}, KURT_MODERATE_LIMIT = {kurtModerateLimit}");
            Console.WriteLine($"Input (Non-normal)      : {nonNormal.Count}");
            Console.WriteLine($"1.5IQR                  : {cleansed.Count(c => c.CleansingMethod == IqrCappingEngine.Method15Iqr)}");
            Console.WriteLine($"3.0IQR                  : {cleansed.Count(c => c.CleansingMethod == IqrCappingEngine.Method30Iqr)}");
            Console.WriteLine($"KEEP (Zero IQR)         : {cleansed.Count(c => c.CleansingMethod == IqrCappingEngine.MethodKeep)}");
            Console.WriteLine($"SKU ที่มียอดถูกตัด        : {cappedSkus}");
            Console.WriteLine($"ยอดรวมที่ถูกตัดทอน        : {totalReduced:N4}");

            Console.WriteLine();
            Console.WriteLine("--- IQR Capping : แยกตามคลัง ---");
            foreach (var g in cleansed.GroupBy(c => c.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int capped = g.Count(c => c.QtyReduced24M.Any(q => q > 0m));
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | 1.5IQR {g.Count(c => c.CleansingMethod == IqrCappingEngine.Method15Iqr),7:N0} | 3.0IQR {g.Count(c => c.CleansingMethod == IqrCappingEngine.Method30Iqr),7:N0} | KEEP {g.Count(c => c.CleansingMethod == IqrCappingEngine.MethodKeep),7:N0} | ถูกตัด {capped,7:N0}");
            }

            return cleansed;
        }

        // ======================================================
        // PBI-08: รวม KEEP (Gate 1/2) + Sigma (PBI-06) + IQR (PBI-07) -> คำนวณสถิติสะอาดใหม่ 7 ค่า
        // -> บันทึกลง DRP_FCT_SKU_OUTLIER_CLEANSING แล้วคืน List<CleansedSkuDemandDto> ส่งต่อ Stage 1.2 (PBI-09 ถึง PBI-12)
        // ======================================================
        public static List<CleansedSkuDemandDto> RunFinalize(SqlConnection connection, string? targetWarehouseCode, List<SkuCleansingCandidate> candidates, List<CleansedDemandSeries> cleansed, List<CleansedDemandSeries> cleansedIqr)
        {
            // month_no (1..24) -> period_year_month (YYYYMM) ใช้เติมคอลัมน์งวดในแถว Fact
            Dictionary<int, string> periodMap = OutlierCleansingRepository.LoadPeriodMap(connection, targetWarehouseCode);
            // รวม 3 สาย (KEEP + Sigma + IQR) -> DTO สถิติสะอาด 1 รายการ/SKU และแถว Fact 24 แถว/SKU
            var (dtos, rows) = CleansedDatasetFinalizeEngine.FinalizeAll(candidates, cleansed, cleansedIqr, periodMap);

            // Guard: Save เป็นแบบลบของเดิมแล้วเขียนใหม่ (ทุกคลัง = TRUNCATE) ถ้าไม่มีแถวเลยจะล้างตารางปลายทางจนว่าง
            if (rows.Count == 0)
                throw new InvalidOperationException("ไม่มีแถวข้อมูลสำหรับบันทึก (ไม่พบ SKU) ยกเลิกการบันทึกเพื่อป้องกันการล้างตารางปลายทาง");

            // บันทึกลงตารางปลายทาง: ลบของเดิม -> Bulk Insert -> Commit ใน Transaction เดียว (ล้มเหลว = ย้อนกลับทั้งหมด)
            OutlierCleansingRepository.Save(connection, rows, targetWarehouseCode);
            Log.Information("บันทึก {Rows} แถว ลง DRP_FCT_SKU_OUTLIER_CLEANSING สำเร็จ (SKU {Skus})", rows.Count, dtos.Count);

            Console.WriteLine();
            Console.WriteLine("--- Cleansed Dataset Finalize (PBI-08) ---");
            Console.WriteLine($"SKU ทั้งหมด (KEEP+Sigma+IQR) : {dtos.Count}");
            Console.WriteLine($"แถว Fact บันทึก              : {rows.Count}");
            Console.WriteLine($"HIGH_OUTLIER (เดือนที่ถูกตัด) : {rows.Count(r => r.OutlierStatus == CleansedDatasetFinalizeEngine.StatusHighOutlier)}");
            foreach (var g in dtos.GroupBy(d => d.OutlierMethod).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"{g.Key,-10} {g.Count(),8:N0}");
            }

            return dtos;
        }
    }

}