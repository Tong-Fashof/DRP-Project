using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class SigmaCappingTests
    {
        // เกณฑ์ที่ใช้ทดสอบ (ค่าเดียวกับ M_LOV: CV_THRESHOLD = 0.5, SIGMA_CAP_MULTIPLIER = 3.0)
        private const decimal DefaultCvLimit = SigmaCappingEngine.DefaultCvThreshold;
        private const decimal DefaultSigmaCap = SigmaCappingEngine.DefaultSigmaCapMultiplier;

        // ชื่อวิธีตัดยอด (ใช้ค่าจาก Engine)
        private const string Sigma2 = SigmaCappingEngine.Method2Sigma;   // '2SIGMA'
        private const string Sigma3 = SigmaCappingEngine.Method3Sigma;   // '3SIGMA'

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-06-01 ถึง TC-06-08)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Demand24M,            // ตำแหน่งที่ 3  <- Input (ยอดขาย 24 เดือน)
            decimal AvgDemand,              // ตำแหน่งที่ 4  <- Input (FM-06 ส่งต่อจาก PBI-05)
            decimal Sd,                     // ตำแหน่งที่ 5  <- Input (FM-07 ส่งต่อจาก PBI-05)
            decimal ExpectedCv,             // ตำแหน่งที่ 6  <- ผลที่ควรได้ (FM-13)
            string ExpectedMethod,          // ตำแหน่งที่ 7  <- ผลที่ควรได้ ('2SIGMA' / '3SIGMA')
            decimal ExpectedCapLimit,       // ตำแหน่งที่ 8  <- ผลที่ควรได้ (FM-14)
            int[] ExpectedCappedMonths,     // ตำแหน่งที่ 9  <- เดือนที่ควรถูกตัด (เดือน 1..24) ว่าง = ไม่มี
            decimal ExpectedTotalReduced,   // ตำแหน่งที่ 10 <- ผลที่ควรได้ (ยอดตัดทอนรวม 24 งวด)
            decimal ExpectedTotalCleansed   // ตำแหน่งที่ 11 <- ผลที่ควรได้ (ยอดรวมสะอาด 24 งวด)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-06-01 ถึง TC-06-08) ----------
            decimal[] ex1 = { 85, 115, 90, 110, 145, 75, 95, 105, 85, 115, 90, 110,
                              75, 125, 85, 115, 90, 110, 55, 125, 85, 115, 90, 110 };
            decimal[] ex2 = { 60, 75, 60, 110, 65, 60, 75, 280, 45, 60, 75, 135,
                              40, 60, 75, 110, 65, 60, 55, 135, 45, 60, 75, 40 };

            List<TestCase> cases = new()
            {
                new("TC-06-01", "Standard 2SIGMA Capping (FM-14 Ex.1)",
                    ex1,
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    new int[] { 5 }, 5.0000m, 2395.0000m),

                new("TC-06-02", "Standard 3SIGMA Capping (FM-14 Ex.2)",
                    ex2,
                    80.0000m, 50.0000m,
                    0.6250m, Sigma3, 230.0000m,
                    new int[] { 8 }, 50.0000m, 1870.0000m),

                new("TC-06-03", "CV Exact Threshold Boundary (CV = 0.5000)",
                    new decimal[] { 23, 26, 27, 48, 62, 62, 64, 70, 71, 83, 86, 104,
                                    106, 111, 112, 115, 120, 122, 130, 140, 141, 183, 184, 210 },
                    100.0000m, 50.0000m,
                    0.5000m, Sigma2, 200.0000m,
                    new int[] { 24 }, 10.0000m, 2390.0000m),

                new("TC-06-04", "Normal Demand Without Outlier",
                    new decimal[] { 90, 90, 90, 90, 95, 95, 95, 95, 100, 100, 100, 100,
                                    100, 100, 100, 100, 105, 105, 105, 105, 110, 110, 110, 110 },
                    100.0000m, 6.5938m,
                    0.0659m, Sigma2, 113.1876m,
                    Array.Empty<int>(), 0.0000m, 2400.0000m),

                new("TC-06-05", "Exact Cap Limit Boundary (Demand == CapLimit)",
                    new decimal[] { 67, 86, 70, 116, 74, 96, 91, 113, 127, 102, 96, 105,
                                    103, 113, 73, 96, 120, 128, 101, 95, 67, 110, 111, 140 },
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    Array.Empty<int>(), 0.0000m, 2400.0000m),

                new("TC-06-06", "Zero Sales Activity Safety",
                    Flat(0m, 24),
                    0.0000m, 0.0000m,
                    0.0000m, Sigma2, 0.0000m,
                    Array.Empty<int>(), 0.0000m, 0.0000m),

                new("TC-06-07", "Multiple Outlier Months Capping",
                    new decimal[] { 103, 96, 86, 92, 92, 96, 93, 79, 103, 98, 85, 87,
                                    97, 112, 93, 99, 110, 82, 92, 89, 76, 145, 145, 150 },
                    100.0000m, 20.0000m,
                    0.2000m, Sigma2, 140.0000m,
                    new int[] { 22, 23, 24 }, 20.0000m, 2380.0000m),

                new("TC-06-08", "High-Volume Scale & 4-Decimal Precision",
                    Flat(95000.5000m, 23).Concat(new decimal[] { 160000.7500m }).ToArray(),
                    97708.8438m, 13268.1205m,
                    0.1358m, Sigma2, 124245.0848m,
                    new int[] { 24 }, 35755.6652m, 2309256.5848m),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: SigmaCappingEngine Sigma Capping (FM-13 ถึง FM-14) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง SKU จำลองสาย Normal ที่ PBI-05 ส่งต่อมา แล้วเรียก Engine
                var sku = new SkuNormalityResult
                {
                    WarehouseCode = "WH",
                    ItemId = tc.Id,
                    Demand24M = tc.Demand24M,
                    HistoryMonths = 24,
                    AvgDemand = tc.AvgDemand,
                    Sd = tc.Sd,
                    IsNormal = true
                };
                CleansedDemandSeries r = SigmaCappingEngine.Cap(sku, DefaultCvLimit, DefaultSigmaCap);

                // CV ปัด 4 ตำแหน่งตอนเทียบ (Engine เทียบเกณฑ์ด้วย CV เต็มความละเอียด)
                decimal cv = Math.Round(SigmaCappingEngine.CalculateCv(tc.AvgDemand, tc.Sd), 4, MidpointRounding.AwayFromZero);

                // เดือนที่ถูกตัดจริง (เดือนที่ QtyReduced > 0)
                int[] cappedMonths = Enumerable.Range(1, 24).Where(m => r.QtyReduced24M[m - 1] > 0m).ToArray();

                // เทียบผลลัพธ์ทีละฟิลด์
                bool isPass = cv == tc.ExpectedCv &&
                    r.CleansingMethod == tc.ExpectedMethod &&
                    r.CapLimit == tc.ExpectedCapLimit &&
                    cappedMonths.SequenceEqual(tc.ExpectedCappedMonths) &&
                    r.QtyReduced24M.Sum() == tc.ExpectedTotalReduced &&
                    r.CleansedDemand24M.Sum() == tc.ExpectedTotalCleansed &&
                    MonthsCorrect(tc, r);

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
                Console.WriteLine($"   Input    : demand24M=[{string.Join(", ", tc.Demand24M)}], avg={tc.AvgDemand:F4}, sd={tc.Sd:F4}, cvLimit={DefaultCvLimit}, sigmaCap={DefaultSigmaCap}");
                Console.WriteLine($"   Expected : cv={tc.ExpectedCv:F4}, method={tc.ExpectedMethod}, capLimit={tc.ExpectedCapLimit:F4}, cappedMonths=[{string.Join(", ", tc.ExpectedCappedMonths)}], totalReduced={tc.ExpectedTotalReduced:F4}, totalCleansed={tc.ExpectedTotalCleansed:F4}");
                Console.WriteLine($"   Actual   : cv={cv:F4}, method={r.CleansingMethod}, capLimit={r.CapLimit:F4}, cappedMonths=[{string.Join(", ", cappedMonths)}], totalReduced={r.QtyReduced24M.Sum():F4}, totalCleansed={r.CleansedDemand24M.Sum():F4}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }

        // ตรวจรายงวดทั้ง 24 เดือน (AC 6.2 และ AC 6.4):
        // เดือนที่ถูกตัด -> ยอดสะอาด = CapLimit | เดือนอื่น -> คงยอดเดิม
        // และทุกเดือนต้องได้ QtyReduced = ยอดเดิม - ยอดสะอาด (เท่ากับ cleansed + reduced == original)
        private static bool MonthsCorrect(TestCase tc, CleansedDemandSeries r)
        {
            for (int t = 0; t < 24; t++)
            {
                bool isCapped = tc.ExpectedCappedMonths.Contains(t + 1);
                decimal expectedCleansed = isCapped ? tc.ExpectedCapLimit : tc.Demand24M[t];
                decimal expectedReduced = tc.Demand24M[t] - expectedCleansed;

                if (r.CleansedDemand24M[t] != expectedCleansed || r.QtyReduced24M[t] != expectedReduced)
                    return false;
            }
            return true;
        }

        // ตัวช่วยสร้างยอดขายคงที่ qty จำนวน n งวด
        private static decimal[] Flat(decimal qty, int n)
        {
            return Enumerable.Repeat(qty, n).ToArray();
        }
    }

}