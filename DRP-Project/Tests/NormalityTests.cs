using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class NormalityTests
    {
        // เกณฑ์ที่ใช้ทดสอบ (ค่าเดียวกับ M_LOV: JB_PVALUE_THRESHOLD, SKEW_NORMAL_LIMIT, KURT_NORMAL_LIMIT)
        private const decimal PLimit = 0.05m;
        private const decimal SkewLimit = 0.5m;
        private const decimal KurtLimit = 1.0m;

        // AC 5.1: ความคลาดเคลื่อนสูงสุดเมื่อเทียบกับ Excel Template V1
        private const decimal Tolerance = 0.0001m;

        private const string Sigma = "PBI-06 (Sigma)";
        private const string Iqr = "PBI-07 (IQR)";

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-05-01 ถึง TC-05-10)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Demand24M,            // ตำแหน่งที่ 3  <- Input (ยอดขาย 24 เดือน)
            int HistoryMonths,              // ตำแหน่งที่ 4  <- Input (FM-03)
            decimal Sd,                     // ตำแหน่งที่ 5  <- Input (FM-07)
            decimal ExpectedSkewness,       // ตำแหน่งที่ 6  <- ผลที่ควรได้ (FM-10)
            decimal ExpectedKurtosis,       // ตำแหน่งที่ 7  <- ผลที่ควรได้ (FM-11)
            decimal ExpectedJbScore,        // ตำแหน่งที่ 8  <- ผลที่ควรได้ (FM-12)
            decimal ExpectedJbPValue,       // ตำแหน่งที่ 9  <- ผลที่ควรได้ (FM-12)
            bool ExpectedIsNormal,          // ตำแหน่งที่ 10 <- ผลที่ควรได้ (Triple Gate)
            string ExpectedTarget           // ตำแหน่งที่ 11 <- ผลที่ควรได้ (PBI-06 / PBI-07)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-05-01 ถึง TC-05-10) ----------
            List<TestCase> cases = new()
            {
                new("TC-05-01", "Symmetric Normal Bell Curve",
                    new decimal[] { 92, 95, 97, 98, 99, 100, 100, 101, 102, 103, 105, 108,
                                    93, 96, 98, 99, 100, 100, 101, 101, 102, 104, 106, 100 },
                    24, 3.8107m, -0.1080m, 0.2820m, 0.0411m, 0.9797m, true, Sigma),

                new("TC-05-02", "Right-Skewed Distribution (Template Row 2 / FM-10 Ex.1)",
                    new decimal[] { 60, 75, 55, 140, 65, 80, 50, 90, 155, 65, 70, 60,
                                    70, 85, 145, 60, 75, 55, 150, 65, 80, 95, 120, 75 },
                    24, 32.3029m, 1.2189m, 0.2151m, 5.2155m, 0.0737m, false, Iqr),

                new("TC-05-03", "Left-Skewed Distribution",
                    new decimal[] { 150, 135, 155, 70, 145, 130, 160, 120, 55, 145, 140, 150,
                                    140, 125, 65, 150, 135, 155, 60, 145, 130, 115, 90, 135 },
                    24, 32.3029m, -1.2189m, 0.2151m, 5.2155m, 0.0737m, false, Iqr),

                new("TC-05-04", "High Kurtosis Isolated Rejection (Leptokurtic / Fat Tail)",
                    new decimal[] { 94, 100, 106, 97, 125, 100, 103, 97, 95, 105, 100, 75,
                                    96, 104, 100, 98, 122, 102, 93, 107, 100, 78, 97, 103 },
                    24, 10.4728m, 0.0379m, 2.4772m, 3.0690m, 0.2156m, false, Iqr),

                new("TC-05-05", "Low P-Value Rejection (Gate 1 JB Failure)",
                    new decimal[] { 95, 100, 105, 98, 130, 100, 102, 98, 96, 104, 100, 70,
                                    97, 103, 100, 99, 128, 101, 95, 105, 100, 74, 98, 102 },
                    24, 12.2155m, 0.1137m, 3.3991m, 6.2508m, 0.0439m, false, Iqr),

                new("TC-05-06", "Active Window 15M Calculation (FM-10 Ex.2)",
                    Pad(9, 90, 105, 95, 115, 85, 100, 120, 90, 100, 95, 110, 85, 105, 95, 110),
                    15, 50.1519m, 0.3036m, -0.7825m, 0.7143m, 0.6997m, true, Sigma),

                new("TC-05-07", "Skewness Exact Threshold Boundary (Skewness = +0.5000)",
                    new decimal[] { 99, 99, 94, 95, 96, 97, 98, 99, 100, 100, 101, 102,
                                    103, 104, 105, 106, 108, 110, 95, 98, 100, 102, 94, 109 },
                    24, 4.6149m, 0.5000m, -0.4794m, 1.2676m, 0.5306m, true, Sigma),

                new("TC-05-08", "Zero SD / Constant Demand Safety",
                    Enumerable.Repeat(100m, 24).ToArray(),
                    24, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 1.0000m, true, Sigma),

                new("TC-05-09", "Kurtosis Exact Threshold Boundary (Kurtosis = +1.0000)",
                    new decimal[] { 86, 87, 105, 95, 96, 97, 98, 99, 100, 100, 101, 102,
                                    103, 104, 105, 106, 108, 110, 95, 98, 100, 102, 118, 111 },
                    24, 7.0706m, 0.0245m, 1.0000m, 0.3196m, 0.8523m, true, Sigma),

                new("TC-05-10", "Short History Safety Guard (HistoryMonths = 8 < 12 — FM-10 Ex.3)",
                    Pad(16, 50, 60, 55, 65, 50, 60, 55, 65),
                    8, 27.8843m, 0.0000m, 0.0000m, 0.0000m, 1.0000m, true, Sigma),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: NormalityEngine Normality Testing (FM-10 ถึง FM-12) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง SKU จำลอง แล้วเรียก Normality Engine โดยส่ง input เข้าไปตรงๆ
                var c = new SkuCleansingCandidate
                {
                    warehouse_code = "WH",
                    item_id = tc.Id,
                    Demand24M = tc.Demand24M,
                    HistoryMonths = tc.HistoryMonths,
                    Sd = tc.Sd
                };
                SkuNormalityResult r = NormalityEngine.Evaluate(c, PLimit, SkewLimit, KurtLimit);

                // ตรวจว่า SKU ถูกส่งไปสายไหนจริง (Partition)
                var (normal, nonNormal) = NormalityEngine.Partition(new List<SkuNormalityResult> { r });
                string actualTarget = normal.Count == 1 ? Sigma : Iqr;

                // เทียบผลลัพธ์ทีละฟิลด์ (AC 5.1: คลาดเคลื่อนได้ไม่เกิน 0.0001)
                bool isPass = Near(r.Skewness, tc.ExpectedSkewness) &&
                    Near(r.Kurtosis, tc.ExpectedKurtosis) &&
                    Near(r.JbScore, tc.ExpectedJbScore) &&
                    Near(r.JbPValue, tc.ExpectedJbPValue) &&
                    r.IsNormal == tc.ExpectedIsNormal &&
                    actualTarget == tc.ExpectedTarget;

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
                Console.WriteLine($"   Input    : demand24M=[{string.Join(", ", tc.Demand24M)}], history={tc.HistoryMonths}, sd={tc.Sd}");
                Console.WriteLine($"   Expected : skew={tc.ExpectedSkewness}, kurt={tc.ExpectedKurtosis}, jb={tc.ExpectedJbScore}, p={tc.ExpectedJbPValue}, isNormal={tc.ExpectedIsNormal}, target={tc.ExpectedTarget}");
                Console.WriteLine($"   Actual   : skew={r.Skewness}, kurt={r.Kurtosis}, jb={r.JbScore}, p={r.JbPValue}, isNormal={r.IsNormal}, target={actualTarget}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }

        // เทียบค่าตามเกณฑ์ AC 5.1 (คลาดเคลื่อนไม่เกิน 0.0001)
        private static bool Near(decimal actual, decimal expected)
        {
            return Math.Abs(actual - expected) <= Tolerance;
        }

        // ตัวช่วยสร้างอาเรย์ 24 งวด: เติม 0 นำหน้าตามจำนวนที่ระบุ แล้วตามด้วยยอดขายช่วงที่เริ่มขายจริง
        // เช่น Pad(9, 90, 105, ...) = M1..M9 เป็น 0 และ M10.. เป็นค่าที่ใส่ (รวมเป็น 24 งวดเสมอ)
        private static decimal[] Pad(int leadingZeros, params decimal[] values)
        {
            decimal[] demand = new decimal[24];
            Array.Copy(values, 0, demand, leadingZeros, values.Length);
            return demand;
        }
    }
}
