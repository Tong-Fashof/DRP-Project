using System.Globalization;
using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class FinalizeTests
    {
        // ชื่อวิธีคลีนซิ่ง
        private const string Keep = "KEEP";
        private const string Sigma2 = "2SIGMA";
        private const string Sigma3 = "3SIGMA";
        private const string Iqr15 = "1.5IQR";

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-08-01 ถึง TC-08-10, ยกเว้น TC-08-09 ที่ต้องทดสอบกับ DB จริง)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Original24M,          // ตำแหน่งที่ 3  <- Input (ยอดขายเดิม 24 เดือน)
            string Method,                  // ตำแหน่งที่ 4  <- Input (CleansingMethod)
            decimal CapLimit,               // ตำแหน่งที่ 5  <- Input (KEEP = 0)
            int HistoryMonths,              // ตำแหน่งที่ 6  <- Input
            decimal? Skewness,              // ตำแหน่งที่ 7  <- Input (null = KEEP จาก Gate 1/2)
            decimal? Kurtosis,              // ตำแหน่งที่ 8  <- Input
            decimal? JbPValue,              // ตำแหน่งที่ 9  <- Input
            decimal ExpectedTotalReduced,   // ตำแหน่งที่ 10 <- ผลที่ควรได้ (ยอดตัดทอนรวม 24 งวด)
            decimal ExpectedTotalCleansed,  // ตำแหน่งที่ 11 <- ผลที่ควรได้ (ยอดสะอาดรวม 24 งวด)
            decimal ExpectedMean,           // ตำแหน่งที่ 12 <- ผลที่ควรได้ (FM-06)
            decimal ExpectedSd,             // ตำแหน่งที่ 13 <- ผลที่ควรได้ (FM-07)
            decimal ExpectedCv,             // ตำแหน่งที่ 14 <- ผลที่ควรได้ (FM-13)
            int ExpectedActiveMonths,       // ตำแหน่งที่ 15 <- ผลที่ควรได้ (FM-04)
            decimal ExpectedAdi,            // ตำแหน่งที่ 16 <- ผลที่ควรได้ (FM-05)
            decimal ExpectedTrendR,         // ตำแหน่งที่ 17 <- ผลที่ควรได้ (FM-08)
            decimal ExpectedSeasonalR       // ตำแหน่งที่ 18 <- ผลที่ควรได้ (FM-09)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-08-01 ถึง TC-08-08 และ TC-08-10) ----------
            List<TestCase> cases = new()
            {
                new("TC-08-01", "Standard 2SIGMA Capped Series (FM-16 Ex.1)",
                    new decimal[] { 85, 115, 90, 110, 145, 75, 95, 105, 85, 115, 90, 110,
                                    75, 125, 85, 115, 90, 110, 55, 125, 85, 115, 90, 110 },
                    Sigma2, 140m, 24, 0.1524m, 0.4210m, 0.6540m,
                    5m, 2395m,
                    99.7917m, 19.5314m, 0.1957m, 24, 1.0000m, -0.0354m, 0.3332m),

                new("TC-08-02", "Standard 3SIGMA Capped Series",
                    new decimal[] { 60, 75, 60, 110, 65, 60, 75, 280, 45, 60, 75, 135,
                                    40, 60, 75, 110, 65, 60, 55, 135, 45, 60, 75, 40 },
                    Sigma3, 230m, 24, 0.8500m, 1.2000m, 0.0820m,
                    50m, 1870m,
                    77.9167m, 41.6485m, 0.5345m, 24, 1.0000m, -0.1358m, 0.7485m),

                new("TC-08-03", "1.5IQR Capped Series (Non-Normal Distribution)",
                    new decimal[] { 38, 39, 42, 44, 42, 38, 45, 42, 38, 42, 68, 39,
                                    42, 44, 45, 38, 42, 42, 45, 38, 42, 42, 45, 42 },
                    Iqr15, 51.5m, 24, 0.6500m, 1.8000m, 0.4210m,
                    16.5m, 1007.5m,
                    41.9792m, 3.1776m, 0.0757m, 24, 1.0000m, 0.1717m, 0.2536m),

                new("TC-08-04", "Gate 1 KEEP Series (Short History 15M & Active Window Trend)",
                    Pad(9, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100, 105, 110, 115, 120),
                    Keep, 0m, 15, null, null, null,
                    0m, 1275m,
                    53.1250m, 45.5119m, 0.8567m, 15, 1.6000m, 1.0000m, 0.0000m),

                new("TC-08-05", "Gate 2 KEEP Series (Full 24M Seasonal & Trend Protected)",
                    new decimal[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
                                    15, 25, 35, 45, 55, 65, 75, 85, 95, 105, 115, 125 },
                    Keep, 0m, 24, null, null, null,
                    0m, 1620m,
                    67.5000m, 35.3553m, 0.5238m, 24, 1.0000m, 0.5600m, 0.0000m),

                new("TC-08-06", "PBI-07 Zero-IQR KEEP Series (Normality Metadata Preserved)",
                    new decimal[] { 10, 10, 50, 50, 50, 50, 50, 50, 50, 50, 50, 200,
                                    50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 200 },
                    Keep, 0m, 24, 3.2000m, 12.0000m, 0.0000m,
                    0m, 1420m,
                    59.1667m, 44.8104m, 0.7574m, 24, 1.0000m, 0.3472m, 0.9501m),

                new("TC-08-07", "Zero Demand Entire 24M Safety Guard",
                    Flat(0m, 24),
                    Keep, 0m, 0, null, null, null,
                    0m, 0m,
                    0.0000m, 0.0000m, 0.0000m, 0, 0.0000m, 0.0000m, 0.0000m),

                new("TC-08-08", "Flat Non-Zero Demand (CleanSd == 0.0000 Guard)",
                    Flat(50m, 24),
                    Keep, 0m, 24, null, null, null,
                    0m, 1200m,
                    50.0000m, 0.0000m, 0.0000m, 24, 1.0000m, 0.0000m, 0.0000m),

                new("TC-08-10", "High-Volume Scale & 4-Decimal Precision Conservation",
                    Flat(95000.5m, 23).Append(160000.75m).ToArray(),
                    Sigma2, 124245.0848m, 24, 0.1358m, 0.5000m, 0.7500m,
                    35755.6652m, 2309256.5848m,
                    96219.0244m, 5969.5259m, 0.0620m, 24, 1.0000m, 0.3464m, 0.0000m),
            };

            Dictionary<int, string> periodMap = BuildPeriodMap();

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: CleansedDatasetFinalizeEngine Clean Statistics (FM-16) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง series จำลอง แล้วเรียก Engine ของ PBI-08 ทั้งฝั่งสถิติ (DTO) และฝั่งแถว Fact รายเดือน
                CleansedDemandSeries series = BuildSeries(tc);
                CleansedSkuDemandDto dto = CleansedDatasetFinalizeEngine.BuildDto(series);
                List<SkuOutlierCleansingRow> rows = CleansedDatasetFinalizeEngine.BuildRows(series, periodMap, DateTime.UtcNow);

                decimal totalOriginal = tc.Original24M.Sum();
                decimal totalCleansed = rows.Sum(r => r.CleansedDemandQty);
                decimal totalReduced = rows.Sum(r => r.QtyReduced);

                // ตรวจแถวรายงวด: 24 แถว, month_no/period ถูกต้อง, ยอดสะอาด/ยอดตัดทอนถูกต้อง, ไม่ติดลบ, status ถูกต้อง (AC 8.2, 8.3)
                bool rowsOk = rows.Count == 24;
                for (int t = 0; t < Math.Min(rows.Count, 24); t++)
                {
                    SkuOutlierCleansingRow row = rows[t];
                    decimal expectedCleansed = tc.Method == Keep ? tc.Original24M[t] : Math.Min(tc.Original24M[t], tc.CapLimit);
                    string expectedStatus = tc.Original24M[t] > expectedCleansed ? "HIGH_OUTLIER" : "NORMAL";

                    if (row.MonthNo != t + 1 ||
                        row.PeriodYearMonth != periodMap[t + 1] ||
                        row.OriginalDemandQty != tc.Original24M[t] ||
                        row.CleansedDemandQty != expectedCleansed ||
                        row.QtyReduced != tc.Original24M[t] - expectedCleansed ||
                        row.QtyReduced < 0m ||
                        row.OutlierStatus != expectedStatus)
                    {
                        rowsOk = false;
                    }
                }

                // ตรวจสมดุล: SUM(สะอาด) + SUM(ตัดทอน) = SUM(เดิม) (AC 8.2)
                bool conserved = totalCleansed + totalReduced == totalOriginal;

                // ตรวจ DTO 15 พร็อพเพอร์ตี้ และ Metadata ที่ส่งต่อ (KEEP จาก Gate 1/2 ต้องเป็น null) (AC 8.4)
                bool metadataOk = typeof(CleansedSkuDemandDto).GetProperties().Length == 15 &&
                    dto.CleansedDemand24M.Length == 24 &&
                    dto.OutlierMethod == tc.Method &&
                    dto.HistoryMonths == tc.HistoryMonths &&
                    dto.Skewness == tc.Skewness &&
                    dto.Kurtosis == tc.Kurtosis &&
                    dto.JbPValue == tc.JbPValue;

                // เทียบผลลัพธ์ทีละฟิลด์ (สถิติปัด 4 ตำแหน่งมาจาก Engine แล้ว) (AC 8.1)
                bool isPass = dto.CleanMean == tc.ExpectedMean &&
                    dto.CleanSd == tc.ExpectedSd &&
                    dto.CleanCv == tc.ExpectedCv &&
                    dto.CleanActiveMonths == tc.ExpectedActiveMonths &&
                    dto.CleanAdi == tc.ExpectedAdi &&
                    dto.CleanTrendR == tc.ExpectedTrendR &&
                    dto.CleanSeasonalR == tc.ExpectedSeasonalR &&
                    totalReduced == tc.ExpectedTotalReduced &&
                    totalCleansed == tc.ExpectedTotalCleansed &&
                    rowsOk && conserved && metadataOk;

                if (isPass)
                {
                    passCount++;
                }
                else
                {
                    failCount++;
                }

                // แสดงผลลัพธ์เทียบ expected vs actual
                Console.WriteLine($"[{(isPass ? "PASS" : "FAIL")}] {tc.Id} - {tc.Description}");
                Console.WriteLine($"   Input    : original24M=[{string.Join(", ", tc.Original24M)}], method={tc.Method}, capLimit={tc.CapLimit}, history={tc.HistoryMonths}, skew={tc.Skewness?.ToString() ?? "null"}, kurt={tc.Kurtosis?.ToString() ?? "null"}, jbP={tc.JbPValue?.ToString() ?? "null"}");
                Console.WriteLine($"   Expected : totalReduced={tc.ExpectedTotalReduced:F4}, totalCleansed={tc.ExpectedTotalCleansed:F4}, mean={tc.ExpectedMean:F4}, sd={tc.ExpectedSd:F4}, cv={tc.ExpectedCv:F4}, active={tc.ExpectedActiveMonths}, adi={tc.ExpectedAdi:F4}, trendR={tc.ExpectedTrendR:F4}, seasonalR={tc.ExpectedSeasonalR:F4}");
                Console.WriteLine($"   Actual   : totalReduced={totalReduced:F4}, totalCleansed={totalCleansed:F4}, mean={dto.CleanMean:F4}, sd={dto.CleanSd:F4}, cv={dto.CleanCv:F4}, active={dto.CleanActiveMonths}, adi={dto.CleanAdi:F4}, trendR={dto.CleanTrendR:F4}, seasonalR={dto.CleanSeasonalR:F4}");
                Console.WriteLine($"   Checks   : rowsOk={rowsOk}, conserved={conserved}, metadataOk={metadataOk}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("หมายเหตุ: TC-08-09 (Scoped Idempotency & Rollback) ต้องทดสอบกับฐานข้อมูลจริง ไม่อยู่ใน Unit Test นี้");
            Console.WriteLine("=========================================");
        }

        // สร้าง CleansedDemandSeries ที่ PBI-08 จะได้รับ
        // - KEEP จาก Gate 1/2 (ไม่มี Normality metadata): ผ่าน Adapter จริง ToKeepSeries (Skewness/Kurtosis/JbPValue = null)
        // - สายอื่น (Sigma/IQR/Zero-IQR KEEP): จำลองผลที่ PBI-06/07 ส่งมา คือ ยอดเกินเพดานถูกตัดเหลือเท่าเพดาน
        private static CleansedDemandSeries BuildSeries(TestCase tc)
        {
            if (tc.Method == Keep && tc.Skewness == null)
            {
                var c = new SkuCleansingCandidate
                {
                    warehouse_code = "WH",
                    item_id = tc.Id,
                    Demand24M = tc.Original24M,
                    HistoryMonths = tc.HistoryMonths,
                    CleansingStatus = "KEEP",
                    CleansingMethod = "KEEP"
                };
                c.Sd = CleansingEngine.CalculateSd(c.Demand24M);
                return CleansedDatasetFinalizeEngine.ToKeepSeries(c);
            }

            var original = (decimal[])tc.Original24M.Clone();
            var cleansed = new decimal[24];
            var reduced = new decimal[24];
            for (int t = 0; t < 24; t++)
            {
                cleansed[t] = tc.Method == Keep ? original[t] : Math.Min(original[t], tc.CapLimit);
                reduced[t] = original[t] - cleansed[t];
            }

            return new CleansedDemandSeries
            {
                WarehouseCode = "WH",
                ItemId = tc.Id,
                OriginalDemand24M = original,
                CleansedDemand24M = cleansed,
                QtyReduced24M = reduced,
                CleansingMethod = tc.Method,
                CapLimit = tc.CapLimit,
                HistoryMonths = tc.HistoryMonths,
                AvgDemand = CleansingEngine.CalculateAvgDemand(original),
                Sd = CleansingEngine.CalculateSd(original),
                Skewness = tc.Skewness,
                Kurtosis = tc.Kurtosis,
                JbPValue = tc.JbPValue
            };
        }

        // ตัวช่วยสร้างรหัสงวดจำลอง: month_no 1 = 202410 ... month_no 24 = 202609 (ใช้ InvariantCulture กันปี พ.ศ.)
        private static Dictionary<int, string> BuildPeriodMap()
        {
            var map = new Dictionary<int, string>();
            for (int i = 1; i <= 24; i++)
                map[i] = new DateTime(2024, 9, 1).AddMonths(i).ToString("yyyyMM", CultureInfo.InvariantCulture);
            return map;
        }

        // ตัวช่วยสร้างอาเรย์ 24 งวด: เติม 0 ไว้หน้า zeroMonths งวด แล้วตามด้วยยอดขายที่ระบุ (รวมต้องครบ 24)
        private static decimal[] Pad(int zeroMonths, params decimal[] values)
        {
            return new decimal[zeroMonths].Concat(values).ToArray();
        }

        // ตัวช่วยสร้างยอดขายคงที่ qty จำนวน n งวด
        private static decimal[] Flat(decimal qty, int n)
        {
            return Enumerable.Repeat(qty, n).ToArray();
        }
    }
}