using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    // PBI-06: Unit Test ของ Sigma Capping Engine (รัน: --test-sigma | ไม่ต่อฐานข้อมูล)
    //
    // แต่ละเคสทำ 3 ขั้นเหมือนกัน:
    //   1) สร้าง SKU จำลองสาย Normal (ข้อมูลที่ PBI-05 ส่งต่อมา: Demand24M, AvgDemand, Sd)
    //   2) เรียก SigmaCappingEngine.Cap(...)
    //   3) เทียบผลกับค่าที่ควรได้ แล้วแสดง Input / Expected / Actual (รูปแบบเดียวกับ Gate 2)
    //
    // ส่วนที่ 1: TC-06-01 ถึง TC-06-08 (ตรงตามตารางใน Jira)
    // ส่วนที่ 2: EX-06-01 ถึง EX-06-08 (เรื่องที่ตาราง Jira ไม่ครอบคลุม เช่น ค่าคอนฟิก, Input ผิดเงื่อนไข)
    class SigmaCappingTests
    {
        // ใช้ค่าและชื่อวิธีจาก SigmaCappingEngine โดยตรง (ไม่พิมพ์ซ้ำ)
        private const decimal CvLimit = SigmaCappingEngine.DefaultCvThreshold;          // 0.5
        private const decimal SigmaCap = SigmaCappingEngine.DefaultSigmaCapMultiplier;  // 3.0
        private const string Sigma2 = SigmaCappingEngine.Method2Sigma;                  // '2SIGMA'
        private const string Sigma3 = SigmaCappingEngine.Method3Sigma;                  // '3SIGMA'

        private static int passCount = 0;
        private static int failCount = 0;

        // งวดที่คาดว่าจะถูกตัด: Month (เริ่มที่ 1) และยอดที่ถูกตัดทอน
        // ยอดสะอาดของงวดนั้นต้องเท่ากับ CapLimit เสมอ | งวดที่ไม่อยู่ในรายการต้องคงยอดเดิมและ reduced = 0
        private record Capped(int Month, decimal Reduced);

        // 1 เคสทดสอบ (ตรงตามตาราง TC-06-01 ถึง TC-06-08)
        private record TestCase(
            string Id,
            string Description,
            decimal[] Demand24M,            // Input: ยอดขาย 24 เดือน
            decimal AvgDemand,              // Input: FM-06 จาก PBI-05
            decimal Sd,                     // Input: FM-07 จาก PBI-05
            decimal ExpectedCv,             // FM-13 (ปัด 4 ตำแหน่ง)
            string ExpectedMethod,          // '2SIGMA' / '3SIGMA'
            decimal ExpectedCapLimit,       // FM-14
            Capped[] ExpectedCapped,        // งวดที่ถูกตัด (ว่าง = ไม่มี)
            decimal ExpectedTotalReduced,   // ยอดตัดทอนรวม 24 งวด
            decimal ExpectedTotalCleansed   // ยอดสะอาดรวม 24 งวด
        );

        public static void RunUnitTests()
        {
            passCount = 0;
            failCount = 0;

            Console.WriteLine("===== Unit Test: SigmaCappingEngine Sigma Capping (FM-13 ถึง FM-14) =====\n");

            RunMainCases();
            RunExtraTests();

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {passCount + failCount} เคส)");
            Console.WriteLine("=========================================");
        }

        // ======================================================
        // ส่วนที่ 1: TC-06-01 ถึง TC-06-08
        // ======================================================
        private static void RunMainCases()
        {
            List<TestCase> cases = new()
            {
                new("TC-06-01", "Standard 2SIGMA Capping (FM-14 Ex.1)",
                    new decimal[] { 85, 115, 90, 110, 145, 75, 95, 105, 85, 115, 90, 110,
                                    75, 125, 85, 115, 90, 110, 55, 125, 85, 115, 90, 110 },
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    new Capped[] { new(5, 5.0000m) },
                    5.0000m, 2395.0000m),

                new("TC-06-02", "Standard 3SIGMA Capping (FM-14 Ex.2)",
                    new decimal[] { 60, 75, 60, 110, 65, 60, 75, 280, 45, 60, 75, 135,
                                    40, 60, 75, 110, 65, 60, 55, 135, 45, 60, 75, 40 },
                    80.0000m, 50.0000m,
                    0.6250m, Sigma3, 230.0000m,
                    new Capped[] { new(8, 50.0000m) },
                    50.0000m, 1870.0000m),

                new("TC-06-03", "CV Exact Threshold Boundary (CV = 0.5000)",
                    new decimal[] { 23, 26, 27, 48, 62, 62, 64, 70, 71, 83, 86, 104,
                                    106, 111, 112, 115, 120, 122, 130, 140, 141, 183, 184, 210 },
                    100.0000m, 50.0000m,
                    0.5000m, Sigma2, 200.0000m,
                    new Capped[] { new(24, 10.0000m) },
                    10.0000m, 2390.0000m),

                new("TC-06-04", "Normal Demand Without Outlier",
                    new decimal[] { 90, 90, 90, 90, 95, 95, 95, 95, 100, 100, 100, 100,
                                    100, 100, 100, 100, 105, 105, 105, 105, 110, 110, 110, 110 },
                    100.0000m, 6.5938m,
                    0.0659m, Sigma2, 113.1876m,
                    Array.Empty<Capped>(),
                    0.0000m, 2400.0000m),

                new("TC-06-05", "Exact Cap Limit Boundary (Demand == CapLimit)",
                    new decimal[] { 67, 86, 70, 116, 74, 96, 91, 113, 127, 102, 96, 105,
                                    103, 113, 73, 96, 120, 128, 101, 95, 67, 110, 111, 140 },
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    Array.Empty<Capped>(),
                    0.0000m, 2400.0000m),

                new("TC-06-06", "Zero Sales Activity Safety",
                    Flat(0m, 24),
                    0.0000m, 0.0000m,
                    0.0000m, Sigma2, 0.0000m,
                    Array.Empty<Capped>(),
                    0.0000m, 0.0000m),

                new("TC-06-07", "Multiple Outlier Months Capping",
                    new decimal[] { 103, 96, 86, 92, 92, 96, 93, 79, 103, 98, 85, 87,
                                    97, 112, 93, 99, 110, 82, 92, 89, 76, 145, 145, 150 },
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    new Capped[] { new(22, 5.0000m), new(23, 5.0000m), new(24, 10.0000m) },
                    20.0000m, 2380.0000m),

                new("TC-06-08", "High-Volume Scale & 4-Decimal Precision",
                    Flat(95000.5000m, 23).Concat(new decimal[] { 160000.7500m }).ToArray(),
                    97708.8438m, 13268.1205m,
                    0.1358m, Sigma2, 124245.0848m,
                    new Capped[] { new(24, 35755.6652m) },
                    35755.6652m, 2309256.5848m),
            };

            foreach (TestCase tc in cases)
            {
                CleansedDemandSeries r = SigmaCappingEngine.Cap(MakeSku(tc.Id, tc.Demand24M, tc.AvgDemand, tc.Sd), CvLimit, SigmaCap);

                // CV จาก Engine (ปัด 4 ตำแหน่งเฉพาะตอนเทียบ/แสดงผล)
                decimal actualCv = Math.Round(SigmaCappingEngine.CalculateCv(tc.AvgDemand, tc.Sd), 4, MidpointRounding.AwayFromZero);

                List<string> problems = Compare(tc, r, actualCv);

                Report(tc.Id, tc.Description,
                    input: $"demand24M=[{string.Join(", ", tc.Demand24M)}], avg={tc.AvgDemand:F4}, sd={tc.Sd:F4}, cvLimit={CvLimit}, sigmaCap={SigmaCap}",
                    expected: $"cv={tc.ExpectedCv:F4}, method={tc.ExpectedMethod}, capLimit={tc.ExpectedCapLimit:F4}, capped={FormatExpectedCapped(tc)}, totalReduced={tc.ExpectedTotalReduced:F4}, totalCleansed={tc.ExpectedTotalCleansed:F4}",
                    actual: $"cv={actualCv:F4}, method={r.CleansingMethod}, capLimit={r.CapLimit:F4}, capped={FormatActualCapped(r)}, totalReduced={r.QtyReduced24M.Sum():F4}, totalCleansed={r.CleansedDemand24M.Sum():F4}",
                    isPass: problems.Count == 0,
                    whyFailed: string.Join(", ", problems));
            }
        }

        // เทียบผลทีละหัวข้อ คืนรายการหัวข้อที่ไม่ผ่าน (ว่าง = ผ่านทั้งหมด)
        private static List<string> Compare(TestCase tc, CleansedDemandSeries r, decimal actualCv)
        {
            var problems = new List<string>();

            // AC 6.5: อาเรย์ต้อง 24 ช่อง (ตรวจก่อน เพราะหัวข้ออื่นต้องอ่านอาเรย์)
            if (r.OriginalDemand24M.Length != 24 || r.CleansedDemand24M.Length != 24 || r.QtyReduced24M.Length != 24)
            {
                problems.Add("AC 6.5 อาเรย์ต้องมี 24 ช่อง");
                return problems;
            }

            // FM-13 / AC 6.1
            if (actualCv != tc.ExpectedCv) problems.Add("CV");
            if (r.CleansingMethod != tc.ExpectedMethod) problems.Add("Method (AC 6.1)");

            // FM-14 / AC 6.2 / AC 6.3
            if (r.CapLimit != tc.ExpectedCapLimit) problems.Add("CapLimit");
            if (!MonthsMatch(tc, r)) problems.Add("ยอดรายงวด (AC 6.2)");
            if (r.QtyReduced24M.Sum() != tc.ExpectedTotalReduced) problems.Add("TotalReduced");
            if (r.CleansedDemand24M.Sum() != tc.ExpectedTotalCleansed) problems.Add("TotalCleansed");

            // กฎที่ต้องจริงเสมอทุกเคส
            if (r.CapLimit < 0m) problems.Add("AC 6.3 CapLimit ติดลบ");
            if (!ConservationHolds(r)) problems.Add("AC 6.4 cleansed + reduced != original");
            if (!AllFourDecimals(r)) problems.Add("AC 6.5 ทศนิยมเกิน 4 ตำแหน่ง");

            return problems;
        }

        // งวดที่ถูกตัด: cleansed = CapLimit และ reduced ตามที่ระบุ | งวดอื่น: คงยอดเดิม และ reduced = 0
        private static bool MonthsMatch(TestCase tc, CleansedDemandSeries r)
        {
            for (int t = 0; t < 24; t++)
            {
                Capped? capped = tc.ExpectedCapped.FirstOrDefault(c => c.Month == t + 1);
                decimal expectedCleansed = capped != null ? tc.ExpectedCapLimit : tc.Demand24M[t];
                decimal expectedReduced = capped != null ? capped.Reduced : 0m;

                if (r.CleansedDemand24M[t] != expectedCleansed || r.QtyReduced24M[t] != expectedReduced) return false;
            }
            return true;
        }

        // AC 6.4: cleansed + reduced == original ทุกงวด และ SUM(reduced) == SUM(original) - SUM(cleansed)
        private static bool ConservationHolds(CleansedDemandSeries r)
        {
            for (int t = 0; t < 24; t++)
            {
                if (r.CleansedDemand24M[t] + r.QtyReduced24M[t] != r.OriginalDemand24M[t]) return false;
            }
            return r.QtyReduced24M.Sum() == r.OriginalDemand24M.Sum() - r.CleansedDemand24M.Sum();
        }

        // AC 6.5: ทุกตัวเลขมีทศนิยมไม่เกิน 4 ตำแหน่ง
        private static bool AllFourDecimals(CleansedDemandSeries r)
        {
            return r.OriginalDemand24M.Concat(r.CleansedDemand24M).Concat(r.QtyReduced24M).Append(r.CapLimit)
                .All(x => Math.Round(x, 4, MidpointRounding.AwayFromZero) == x);
        }

        // ======================================================
        // ส่วนที่ 2: เคสเพิ่มเติม (EX-06-01 ถึง EX-06-08)
        // ใช้ข้อมูลตัวอย่างจากงาน โดยให้ CleansingEngine คำนวณ AvgDemand/Sd (FM-06, FM-07) ให้เอง
        // ======================================================
        private static void RunExtraTests()
        {
            decimal[] ex1 = { 85, 115, 90, 110, 145, 75, 95, 105, 85, 115, 90, 110,
                              75, 125, 85, 115, 90, 110, 55, 125, 85, 115, 90, 110 };   // avg 100, sd 20 -> 2SIGMA
            decimal[] ex2 = { 60, 75, 60, 110, 65, 60, 75, 280, 45, 60, 75, 135,
                              40, 60, 75, 110, 65, 60, 55, 135, 45, 60, 75, 40 };       // avg 80, sd 50 -> 3SIGMA

            // EX-06-01: 3SIGMA ใช้ SIGMA_CAP_MULTIPLIER จากคอนฟิก (DEC-PBI06-02)
            {
                var r = SigmaCappingEngine.Cap(MakeSku("EX-06-01", ex2), CvLimit, 2.5m);
                Report("EX-06-01", "3SIGMA ใช้ SIGMA_CAP_MULTIPLIER จากคอนฟิก (2.5)",
                    "ข้อมูลเดียวกับ TC-06-02, sigmaCap=2.5",
                    $"method={Sigma3}, capLimit=205.0000 (80 + 2.5 x 50), M8 reduced=75.0000",
                    $"method={r.CleansingMethod}, capLimit={r.CapLimit:F4}, M8 reduced={r.QtyReduced24M[7]:F4}",
                    r.CleansingMethod == Sigma3 && r.CapLimit == 205.0000m && r.QtyReduced24M[7] == 75.0000m);
            }

            // EX-06-02: 2SIGMA ตัวคูณคงที่ 2.0 ไม่อ่านจาก SIGMA_CAP_MULTIPLIER
            {
                var r = SigmaCappingEngine.Cap(MakeSku("EX-06-02", ex1), CvLimit, 9.0m);
                Report("EX-06-02", "2SIGMA ไม่ใช้ SIGMA_CAP_MULTIPLIER (ตัวคูณคงที่ 2.0)",
                    "ข้อมูลเดียวกับ TC-06-01, sigmaCap=9.0",
                    $"method={Sigma2}, capLimit=140.0000 (100 + 2.0 x 20)",
                    $"method={r.CleansingMethod}, capLimit={r.CapLimit:F4}",
                    r.CleansingMethod == Sigma2 && r.CapLimit == 140.0000m);
            }

            // EX-06-03: CV_THRESHOLD อ่านจากคอนฟิก
            {
                var r = SigmaCappingEngine.Cap(MakeSku("EX-06-03", ex1), 0.1m, SigmaCap);
                Report("EX-06-03", "CV_THRESHOLD จากคอนฟิก (0.1) ทำให้ CV 0.2 กลายเป็น 3SIGMA",
                    "ข้อมูลเดียวกับ TC-06-01, cvLimit=0.1",
                    $"method={Sigma3}, capLimit=160.0000 (100 + 3.0 x 20)",
                    $"method={r.CleansingMethod}, capLimit={r.CapLimit:F4}",
                    r.CleansingMethod == Sigma3 && r.CapLimit == 160.0000m);
            }

            // EX-06-04: CV เกินเกณฑ์เพียงเล็กน้อย (0.500001) ต้องเป็น 3SIGMA
            {
                decimal sd = 100m * CvLimit + 0.0001m;   // 50.0001
                var r = SigmaCappingEngine.Cap(MakeSku("EX-06-04", Flat(100m, 24), 100m, sd), CvLimit, SigmaCap);
                Report("EX-06-04", "CV เกิน CV_THRESHOLD เพียงเล็กน้อย (0.500001) ต้องได้ 3SIGMA",
                    $"demand24M=[100 x 24], avg=100.0000, sd={sd:F4}",
                    $"method={Sigma3}, capLimit=250.0003 (100 + 3.0 x 50.0001)",
                    $"method={r.CleansingMethod}, capLimit={r.CapLimit:F4}",
                    r.CleansingMethod == Sigma3 && r.CapLimit == 250.0003m);
            }

            // EX-06-05: CapLimit ปัด 4 ตำแหน่งแบบ AwayFromZero (100.00005 -> 100.0001)
            {
                decimal cap = SigmaCappingEngine.CalculateCapLimit(100m, 0.000025m, Sigma2, SigmaCap);
                Report("EX-06-05", "CapLimit ปัดเศษ 4 ตำแหน่งแบบ AwayFromZero",
                    "avg=100.0000, sd=0.000025, method=2SIGMA (100 + 2.0 x 0.000025 = 100.00005)",
                    "capLimit=100.0001",
                    $"capLimit={cap:F4}",
                    cap == 100.0001m);
            }

            // EX-06-06: รับเฉพาะ IsNormal = true
            {
                var sku = MakeSku("EX-06-06", ex1);
                sku.IsNormal = false;
                bool threw = Throws<ArgumentException>(() => SigmaCappingEngine.Cap(sku, CvLimit, SigmaCap));
                Report("EX-06-06", "SKU ที่ IsNormal = false ต้องไม่เข้า PBI-06",
                    "IsNormal=false",
                    "throw ArgumentException",
                    threw ? "throw ArgumentException" : "ไม่ throw",
                    threw);
            }

            // EX-06-07: Demand24M ต้องมี 24 งวด
            {
                var sku = MakeSku("EX-06-07", new decimal[12], 100m, 20m);
                bool threw = Throws<ArgumentException>(() => SigmaCappingEngine.Cap(sku, CvLimit, SigmaCap));
                Report("EX-06-07", "Demand24M ไม่ครบ 24 งวดต้อง throw",
                    "Demand24M.Length=12",
                    "throw ArgumentException",
                    threw ? "throw ArgumentException" : "ไม่ throw",
                    threw);
            }

            // EX-06-08: OriginalDemand24M เป็นสำเนา และ Metadata ส่งต่อจาก PBI-05 ครบ
            {
                decimal[] source = (decimal[])ex1.Clone();
                var r = SigmaCappingEngine.Cap(MakeSku("EX-06-08", source), CvLimit, SigmaCap);
                source[4] = 999m;   // แก้อาเรย์ต้นทางหลังตัดยอด ต้องไม่กระทบผลลัพธ์

                bool pass = r.OriginalDemand24M[4] == 145m &&
                    r.WarehouseCode == "WH" && r.ItemId == "EX-06-08" && r.HistoryMonths == 24 &&
                    r.AvgDemand == 100m && r.Sd == 20m &&
                    r.Skewness == 0.1234m && r.Kurtosis == -0.5678m && r.JbPValue == 0.4321m;
                Report("EX-06-08", "OriginalDemand24M เป็นสำเนา และ Metadata ส่งต่อจาก PBI-05 ครบ",
                    "แก้ source[4] = 999 หลังเรียก Cap, skew=0.1234, kurt=-0.5678, jbP=0.4321",
                    "original[4]=145.0000, HistoryMonths/AvgDemand/Sd/Skewness/Kurtosis/JbPValue ตรงต้นทาง",
                    $"original[4]={r.OriginalDemand24M[4]:F4}, history={r.HistoryMonths}, avg={r.AvgDemand:F4}, sd={r.Sd:F4}, skew={r.Skewness}, kurt={r.Kurtosis}, jbP={r.JbPValue}",
                    pass);
            }
        }

        // ======================================================
        // ตัวช่วยแสดงผล
        // ======================================================

        // แสดงผลเทียบ expected vs actual (รูปแบบเดียวกับ Gate 2) ถ้า FAIL จะบอกหัวข้อที่ไม่ผ่านเพิ่ม 1 บรรทัด
        private static void Report(string id, string description, string input, string expected, string actual, bool isPass, string whyFailed = "")
        {
            if (isPass) passCount++; else failCount++;

            Console.WriteLine($"[{(isPass ? "PASS" : "FAIL")}] {id} - {description}");
            Console.WriteLine($"   Input    : {input}");
            Console.WriteLine($"   Expected : {expected}");
            Console.WriteLine($"   Actual   : {actual}");
            if (!isPass && whyFailed != "") Console.WriteLine($"   ไม่ผ่านที่ : {whyFailed}");
            Console.WriteLine();
        }

        private static string FormatExpectedCapped(TestCase tc)
        {
            if (tc.ExpectedCapped.Length == 0) return "none";
            return string.Join("; ", tc.ExpectedCapped.Select(c =>
                $"M{c.Month}: {tc.Demand24M[c.Month - 1]:F4} -> {tc.ExpectedCapLimit:F4} (ตัด {c.Reduced:F4})"));
        }

        private static string FormatActualCapped(CleansedDemandSeries r)
        {
            var parts = new List<string>();
            for (int t = 0; t < 24; t++)
            {
                if (r.QtyReduced24M[t] != 0m)
                {
                    parts.Add($"M{t + 1}: {r.OriginalDemand24M[t]:F4} -> {r.CleansedDemand24M[t]:F4} (ตัด {r.QtyReduced24M[t]:F4})");
                }
            }
            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }

        // ======================================================
        // ตัวช่วยสร้างข้อมูล
        // ======================================================

        // SKU จำลองสาย Normal ที่ PBI-05 ส่งต่อมา (IsNormal = true) โดยระบุ avg/sd เอง
        private static SkuNormalityResult MakeSku(string id, decimal[] demand, decimal avg, decimal sd)
        {
            return new SkuNormalityResult
            {
                WarehouseCode = "WH",
                ItemId = id,
                Demand24M = demand,
                HistoryMonths = 24,
                AvgDemand = avg,
                Sd = sd,
                Skewness = 0.1234m,
                Kurtosis = -0.5678m,
                JbPValue = 0.4321m,
                IsNormal = true
            };
        }

        // SKU จำลองแบบเดียวกัน แต่ให้ CleansingEngine คำนวณ AvgDemand (FM-06) และ Sd (FM-07) จากยอดขายให้
        private static SkuNormalityResult MakeSku(string id, decimal[] demand)
        {
            return MakeSku(id, demand, CleansingEngine.CalculateAvgDemand(demand), CleansingEngine.CalculateSd(demand));
        }

        private static decimal[] Flat(decimal qty, int n)
        {
            return Enumerable.Repeat(qty, n).ToArray();
        }

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (TException)
            {
                return true;
            }
        }
    }

}