using DRP_Project.src.Cleansing;
using DRP_Project.src.NetDemand;
using DRP_Project.Tests;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Xml.Linq;

namespace DRP_Project
{
    class Program
    {
        private static string? targetWarehouseCode = null;   // กรองเฉพาะคลังนี้ (null = ทุกคลัง)
        private static string? targetPeriod = null;          // งวดที่ต้องการประมวลผล รูปแบบ yyyyMM (null = ค่าเริ่มต้นของ NetDemand)

        static void Main(string[] args)
        {
            // โหมด Unit Test: ตรวจก่อนต่อฐานข้อมูล แล้วจบโปรแกรมทันที
            // รัน Unit Test (ไม่ต่อฐานข้อมูล): --test-netdemand | --test-gate1 | --test-gate2 | --test-normality | --test-sigma
            if (args.Contains("--test-netdemand")) { NetDemandTests.RunUnitTests(); return; }
            if (args.Contains("--test-gate1")) { Gate1.RunUnitTests(); return; }
            if (args.Contains("--test-gate2")) { Gate2.RunUnitTests(); return; }
            if (args.Contains("--test-normality")) { NormalityTests.RunUnitTests(); return; }
            if (args.Contains("--test-sigma")) { SigmaCappingTests.RunUnitTests(); return; }

            if (!TryParseArgs(args)) return;   // Argument ผิดรูปแบบ -> จบโปรแกรม

            try
            {
                using SqlConnection connection = ConnectData();

                // ===== STEP 1: NetDemand Preprocessing =====
                // เตรียมยอดความต้องการสุทธิรายเดือนลงตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP
                NetDemandPreprocessor.Run(connection, targetPeriod, targetWarehouseCode);

                // ===== STEP 2: Cleansing Gate 1/2 (PBI-03, PBI-04) =====
                // โหลดเกณฑ์จาก M_LOV และยอดขาย 24 เดือนต่อ SKU แล้วคัดกรอง KEEP / PENDING
                decimal adiThreshold = LoadLovDecimal(connection, "ADI_THRESHOLD", 1.320000m);
                decimal protectTrendSeason = LoadLovDecimal(connection, "PROTECT_TREND_SEASON", 1.000000m);
                List<SkuCleansingCandidate> candidates = LoadCandidates(connection);
                CleansingEngine.CleansingCenterAll(candidates, adiThreshold, protectTrendSeason);
                PrintCleansingSummary(candidates, protectTrendSeason);

                // ===== STEP 3: Normality Testing (PBI-05) =====
                // เฉพาะ SKU ที่ยัง PENDING: แยกสาย Normal -> PBI-06 Sigma / Non-normal -> PBI-07 IQR
                var (normal, nonNormal) = RunNormality(connection, candidates);   // nonNormal -> PBI-07 (ยังไม่พัฒนา)

                // ===== STEP 4: Sigma Capping (PBI-06) =====
                // เฉพาะสาย Normal: ตัดยอดส่วนเกินด้วย 2SIGMA / 3SIGMA แล้วส่งต่อ PBI-08
                List<CleansedDemandSeries> cleansed = RunSigmaCapping(connection, normal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"เกิดข้อผิดพลาด: {ex.Message}");
                throw;
            }
        }


        // ======================================================
        // อ่าน Argument: --warehouse <รหัสคลัง>, --period <yyyyMM>
        // คืน false ถ้า --period รูปแบบไม่ถูกต้อง (ตรวจโดยลองแปลง yyyyMM + "01" เป็นวันที่)
        // ======================================================
        private static bool TryParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warehouse" && i + 1 < args.Length)
                {
                    targetWarehouseCode = args[++i];
                }
                else if (args[i] == "--period" && i + 1 < args.Length)
                {
                    targetPeriod = args[++i];
                    if (!DateTime.TryParseExact(targetPeriod + "01", "yyyyMMdd", null, DateTimeStyles.None, out _))
                    {
                        Console.WriteLine("--period ต้องเป็นรูปแบบ yyyyMM เช่น 202609");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"ไม่รู้จัก Argument: {args[i]}");
                    Console.WriteLine($"Usage: NetDemandPreprocessingEngine.exe --warehouse CMPMH --period 202609");
                    return false;
                }
            }
            return true;
        }


        // ======================================================
        // ฟังก์ชันสำหรับเชื่อมต่อฐานข้อมูลและดึงข้อมูล - config.xml
        // ======================================================
        static SqlConnection ConnectData()
        {
            string xmlPath = Path.Combine(AppContext.BaseDirectory, "config.xml");
            if (!File.Exists(xmlPath))
            {
                throw new FileNotFoundException($"ไม่พบไฟล์ config.xml");
            }

            XDocument doc = XDocument.Load(xmlPath);
            string server = doc.Root!.Element("Server")!.Value;
            string database = doc.Root!.Element("Database")!.Value;
            string userId = doc.Root!.Element("UserId")!.Value;
            string password = doc.Root!.Element("Password")!.Value;

            string connectionString = $"Server={server};Database={database};User ID={userId};Password={password};TrustServerCertificate=True;";
            SqlConnection connection = new SqlConnection(connectionString);

            try
            {
                connection.Open();
                Console.WriteLine("Connection successful!");
                return connection;
            }
            catch (Exception)
            {
                connection.Dispose();
                throw;
            }
        }


        // ======================================================
        // สรุปผล Gate 1 & 2: จำนวน KEEP / PENDING แยกตามเหตุผลและตามคลัง
        // ======================================================
        static void PrintCleansingSummary(List<SkuCleansingCandidate> candidates, decimal protectTrendSeason)
        {
            Console.WriteLine($"Total   : {candidates.Count}");
            Console.WriteLine($"KEEP    : {candidates.Count(c => c.CleansingStatus == "KEEP")}");
            Console.WriteLine($"PENDING : {candidates.Count(c => c.CleansingStatus == "PENDING")}");
            Console.WriteLine($"PROTECT_TREND_SEASON = {protectTrendSeason}");

            Console.WriteLine();
            Console.WriteLine("--- Gate 1 & 2 : แยกตามเหตุผล ---");
            foreach (var g in candidates.GroupBy(c => c.CleansingReason ?? "PENDING").OrderByDescending(g => g.Count()))
                Console.WriteLine($"{g.Key,-40} {g.Count(),8:N0}");

            Console.WriteLine();
            Console.WriteLine("--- Gate 1 & 2 : แยกตามคลัง ---");
            foreach (var g in candidates.GroupBy(c => c.warehouse_code).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int k = g.Count(c => c.CleansingStatus == "KEEP");
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | KEEP {k,7:N0} | PENDING {g.Count() - k,7:N0}");
            }
        }


        // ======================================================
        // อ่านค่าตัวเลขจาก M_LOV (LOV_CODE_1 = 'DRP_CONFIG', LOV_CODE_2 = lovCode2, REC_STATUS = 'A' หรือ NULL)
        // ไม่พบแถวหรือแปลงค่าไม่ได้ -> แสดง [WARN] แล้วใช้ defaultValue
        // ======================================================
        static decimal LoadLovDecimal(SqlConnection connection, string lovCode2, decimal defaultValue)
        {
            string sql = @"
                SELECT TOP (1) LOV_VALUE
                FROM AxWebReport_UAT.dbo.M_LOV
                WHERE LOV_CODE_1 = 'DRP_CONFIG'
                  AND LOV_CODE_2 = @LovCode2
                  AND (REC_STATUS = 'A' OR REC_STATUS IS NULL);";

            using SqlCommand cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@LovCode2", lovCode2);
            object? result = cmd.ExecuteScalar();

            if (result == null || result == DBNull.Value)
            {
                Console.WriteLine($"[WARN] ไม่พบ {lovCode2} ใน M_LOV ใช้ค่า Default = {defaultValue}");
                return defaultValue;
            }

            // แปลงด้วย InvariantCulture เพื่อให้ "0.05" อ่านถูกต้องไม่ขึ้นกับภาษาของเครื่อง
            string text = Convert.ToString(result, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            {
                Console.WriteLine($"[WARN] แปลงค่า {lovCode2} = '{text}' ไม่ได้ ใช้ค่า Default = {defaultValue}");
                return defaultValue;
            }

            return value;
        }


        // ======================================================
        // ฟังก์ชันสำหรับโหลดข้อมูล SKU และปริมาณความต้องการ 24 เดือน
        // ======================================================
        static List<SkuCleansingCandidate> LoadCandidates(SqlConnection connection)
        {
            string sql = @"
            SELECT 
                warehouse_code,
                item_id,
                month_no,
                net_demand_qty
            FROM DRP_FCT_SKU_MONTHLY_DEMAND_PREP
            WHERE (@TargetWarehouseCode IS NULL OR warehouse_code = @TargetWarehouseCode)";

            // สร้าง Dictionary เพื่อเก็บข้อมูล SKU และปริมาณความต้องการ 24 เดือน จาก Query
            var map = new Dictionary<string, SkuCleansingCandidate>();

            using SqlCommand cmd = new SqlCommand(sql, connection);

            // กำหนดค่า Parameter สำหรับ TargetWarehouseCode
            cmd.Parameters.AddWithValue("@TargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

            using SqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string warehouse_code = reader["warehouse_code"].ToString()!;
                string item_id = reader["item_id"].ToString()!;
                int month_no = Convert.ToInt32(reader["month_no"]);
                decimal net_demand_qty = reader["net_demand_qty"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["net_demand_qty"]);

                if (month_no < 1 || month_no > 24) continue;        // ตรวจสอบ monthNo อยู่ในช่วง 1-24
                string key = warehouse_code + "|" + item_id;        // ใช้ WarehouseCode และ ItemId เป็น Key
                if (!map.ContainsKey(key))                          // ตรวจสอบว่ามี Key อยู่ใน Dictionary (ตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP) หรือไม่ ถ้าไม่มีให้สร้าง SkuCleansingCandidate ใหม่
                {
                    map[key] = new SkuCleansingCandidate { warehouse_code = warehouse_code, item_id = item_id };
                }


                // ยอดความต้องการแท้จริง 24 เดือนและการประกอบอาเรย์(Array Population): ดึงชุดข้อมูล net_demand_qty เรียงตาม month_no(1..24) รายคลังและราย SKU จากตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP 
                // โดยสร้างอาเรย์ decimal[24] กำหนดค่าเริ่มต้นเป็น 0.0000m ทั้งหมด แล้วแมปข้อมูลตามตำแหน่ง Demand24M[month_no - 1] = net_demand_qty เพื่อให้มั่นใจว่าอาเรย์มีขนาด 24 งวดสมบูรณ์ 100 % แม้บางงวดจะไม่มีข้อมูลในฐานข้อมูล

                // เพิ่มค่า net_demand_qty ลงใน Demand24M ของ SkuCleansingCandidate ตาม month_no
                map[key].Demand24M[month_no - 1] += net_demand_qty;
            }

            return map.Values.ToList();
        }


        // ======================================================
        // PBI-05: โหลดเกณฑ์ Normality 3 ตัวจาก -> ทดสอบเฉพาะ SKU ที่ PENDING
        // -> แยกสาย Normal (PBI-06 Sigma) / Non-normal (PBI-07 IQR) แล้วแสดงสรุป
        // คืน (normal, nonNormal): normal ส่งต่อ PBI-06 แล้ว | nonNormal ต้องส่งต่อ PBI-07
        // ======================================================
        static (List<SkuNormalityResult> Normal, List<SkuNormalityResult> NonNormal) RunNormality(SqlConnection connection, List<SkuCleansingCandidate> candidates)
        {
            decimal pValueThreshold = LoadLovDecimal(connection, "JB_PVALUE_THRESHOLD", NormalityEngine.DefaultPValueThreshold);
            decimal skewLimit = LoadLovDecimal(connection, "SKEW_NORMAL_LIMIT", NormalityEngine.DefaultSkewLimit);
            decimal kurtLimit = LoadLovDecimal(connection, "KURT_NORMAL_LIMIT", NormalityEngine.DefaultKurtLimit);

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
        static List<CleansedDemandSeries> RunSigmaCapping(SqlConnection connection, List<SkuNormalityResult> normal)
        {
            decimal cvThreshold = LoadLovDecimal(connection, "CV_THRESHOLD", SigmaCappingEngine.DefaultCvThreshold);
            decimal sigmaCapMultiplier = LoadLovDecimal(connection, "SIGMA_CAP_MULTIPLIER", SigmaCappingEngine.DefaultSigmaCapMultiplier);

            List<CleansedDemandSeries> cleansed = SigmaCappingEngine.CapAll(normal, cvThreshold, sigmaCapMultiplier);

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
    }
}