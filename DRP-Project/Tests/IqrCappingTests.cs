using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class IqrCappingTests
    {
        // เกณฑ์ที่ใช้ทดสอบ (ค่าเดียวกับ M_LOV: SKEW_MODERATE_LIMIT, KURT_MODERATE_LIMIT)
        private const decimal SkewLimit = 1.0m;
        private const decimal KurtLimit = 3.0m;

        // ชื่อวิธีตัดยอด (FM-15)
        private const string Iqr15 = "1.5IQR";
        private const string Iqr30 = "3.0IQR";
        private const string Keep = "KEEP";

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-07-01 ถึง TC-07-08)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Demand24M,            // ตำแหน่งที่ 3  <- Input (ยอดขาย 24 เดือน)
            decimal Skewness,               // ตำแหน่งที่ 4  <- Input (FM-10 จาก PBI-05)
            decimal Kurtosis,               // ตำแหน่งที่ 5  <- Input (FM-11 จาก PBI-05)
            decimal ExpectedQ1,             // ตำแหน่งที่ 6  <- ผลที่ควรได้ (FM-15 ขั้นที่ 2.2)
            decimal ExpectedQ3,             // ตำแหน่งที่ 7  <- ผลที่ควรได้ (FM-15 ขั้นที่ 2.2)
            decimal ExpectedIqr,            // ตำแหน่งที่ 8  <- ผลที่ควรได้ (FM-15 ขั้นที่ 2.3)
            string ExpectedMethod,          // ตำแหน่งที่ 9  <- ผลที่ควรได้ ('1.5IQR' / '3.0IQR' / 'KEEP')
            decimal ExpectedCapLimit,       // ตำแหน่งที่ 10 <- ผลที่ควรได้ (FM-15 ขั้นที่ 2.5, KEEP = 0)
            int ExpectedCappedMonths,       // ตำแหน่งที่ 11 <- ผลที่ควรได้ (จำนวนงวดที่ถูกตัด)
            decimal ExpectedTotalReduced,   // ตำแหน่งที่ 12 <- ผลที่ควรได้ (ยอดตัดทอนรวม 24 งวด)
            decimal ExpectedTotalCleansed   // ตำแหน่งที่ 13 <- ผลที่ควรได้ (ยอดสะอาดรวม 24 งวด)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-07-01 ถึง TC-07-08) ----------
            List<TestCase> cases = new()
            {
                new("TC-07-01", "Standard 1.5IQR Capping (FM-15 Ex.1)",
                    new decimal[] { 38, 39, 42, 44, 42, 38, 45, 42, 38, 42, 68, 39,
                                    42, 44, 45, 38, 42, 42, 45, 38, 42, 42, 45, 42 },
                    0.65m, 1.8m,
                    39m, 44m, 5m, Iqr15, 51.5m, 1, 16.5m, 1007.5m),

                new("TC-07-02", "Standard 3.0IQR Capping & Linear Interpolation",
                    new decimal[] { 20, 20, 20, 20, 20, 20, 24, 30, 95, 30, 30, 30,
                                    30, 30, 30, 30, 30, 30, 32, 36, 40, 40, 40, 40 },
                    2.15m, 4.5m,
                    23m, 33m, 10m, Iqr30, 63m, 1, 32m, 735m),

                new("TC-07-03", "Exact Threshold Boundary (|skew| = 1.0, |kurt| = 3.0)",
                    new decimal[] { 30, 35, 40, 45, 50, 50, 50, 55, 60, 60, 65, 70,
                                    70, 70, 70, 70, 70, 70, 70, 75, 80, 80, 85, 115 },
                    -1.0m, 3.0m,
                    50m, 70m, 20m, Iqr15, 100m, 1, 15m, 1520m),

                new("TC-07-04", "Zero IQR Protection Guard (IQR == 0 -> KEEP)",
                    new decimal[] { 10, 10, 50, 50, 50, 50, 50, 50, 50, 50, 50, 200,
                                    50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 200 },
                    3.2m, 12.0m,
                    50m, 50m, 0m, Keep, 0m, 0, 0m, 1420m),

                new("TC-07-05", "Non-Normal Demand Without Outlier",
                    new decimal[] { 25, 30, 35, 40, 40, 40, 40, 45, 45, 50, 50, 55,
                                    55, 60, 60, 60, 60, 60, 60, 65, 70, 75, 80, 85 },
                    0.8m, 1.2m,
                    40m, 60m, 20m, Iqr15, 90m, 0, 0m, 1285m),

                new("TC-07-06", "Exact Cap Limit Boundary Equality (Demand == CapLimit)",
                    new decimal[] { 25, 30, 35, 40, 40, 40, 40, 45, 45, 50, 50, 55,
                                    55, 60, 60, 60, 60, 60, 60, 65, 70, 75, 80, 90 },
                    0.85m, 1.3m,
                    40m, 60m, 20m, Iqr15, 90m, 0, 0m, 1290m),

                new("TC-07-07", "Isolated Kurtosis Trigger & Multiple Outlier Months",
                    new decimal[] { 30, 30, 35, 35, 35, 40, 40, 45, 45, 45, 50, 50,
                                    50, 55, 55, 55, 60, 60, 60, 65, 70, 150, 150, 160 },
                    0.7m, 3.5m,
                    40m, 60m, 20m, Iqr30, 120m, 3, 100m, 1370m),

                new("TC-07-08", "High-Volume Scale & 4-Decimal Precision Interpolation",
                    new decimal[] { 12500.25m, 15000.5m, 18000m, 20000.75m, 22000m, 25000.5m,
                                    28000.25m, 30000m, 32000.5m, 35000m, 38000.75m, 40000m,
                                    42000.5m, 45000m, 48000.25m, 50000m, 52000.75m, 55000m,
                                    58000.5m, 60000m, 65000.25m, 70000m, 75000.5m, 135000.5m },
                    1.5m, 2.0m,
                    27250.3125m, 55750.125m, 28499.8125m, Iqr30, 141249.5625m, 0, 0m, 1070506.75m),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: IqrCappingEngine IQR Capping (FM-15) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง SKU จำลองสาย Non-normal (ข้อมูลที่ PBI-05 ส่งต่อมา) แล้วเรียก Engine โดยส่ง input เข้าไปตรงๆ
                var sku = new SkuNormalityResult
                {
                    WarehouseCode = "WH",
                    ItemId = tc.Id,
                    Demand24M = tc.Demand24M,
                    HistoryMonths = 24,
                    AvgDemand = CleansingEngine.CalculateAvgDemand(tc.Demand24M),
                    Sd = CleansingEngine.CalculateSd(tc.Demand24M),
                    Skewness = tc.Skewness,
                    Kurtosis = tc.Kurtosis,
                    JbPValue = 0.0100m,
                    IsNormal = false
                };
                CleansedDemandSeries r = IqrCappingEngine.Cap(sku, SkewLimit, KurtLimit);

                // Q1, Q3, IQR: เรียงยอดขายจากน้อยไปมาก แล้วหาตำแหน่ง 25% และ 75% (FM-15 ขั้นที่ 2.1 - 2.3)
                decimal[] sorted = (decimal[])tc.Demand24M.Clone();
                Array.Sort(sorted);
                decimal q1 = IqrCappingEngine.PercentileInc(sorted, 0.25m);
                decimal q3 = IqrCappingEngine.PercentileInc(sorted, 0.75m);
                decimal iqr = q3 - q1;

                // ตรวจยอดรายงวด: เกินเพดานให้ลดเหลือเท่าเพดาน, ไม่เกินให้คงเดิม, KEEP คงเดิมทุกงวด (AC 7.1, 7.3)
                bool monthsOk = true;
                for (int t = 0; t < 24; t++)
                {
                    decimal expectedCleansed = tc.ExpectedMethod == Keep
                        ? tc.Demand24M[t]
                        : Math.Min(tc.Demand24M[t], tc.ExpectedCapLimit);

                    if (r.CleansedDemand24M[t] != expectedCleansed ||
                        r.QtyReduced24M[t] != tc.Demand24M[t] - expectedCleansed)
                    {
                        monthsOk = false;
                    }
                }

                // ตรวจสมดุล: สะอาด + ตัดทอน = เดิม ทุกงวด และ SUM(ตัดทอน) = SUM(เดิม) - SUM(สะอาด) (AC 7.4)
                bool conserved = r.QtyReduced24M.Sum() == r.OriginalDemand24M.Sum() - r.CleansedDemand24M.Sum();
                for (int t = 0; t < 24; t++)
                {
                    if (r.CleansedDemand24M[t] + r.QtyReduced24M[t] != r.OriginalDemand24M[t]) conserved = false;
                }

                // ตรวจอาเรย์ 24 ช่อง และทศนิยมไม่เกิน 4 ตำแหน่ง (AC 7.5)
                bool arraysOk = r.OriginalDemand24M.Length == 24 && r.CleansedDemand24M.Length == 24 && r.QtyReduced24M.Length == 24 &&
                    r.CleansedDemand24M.Concat(r.QtyReduced24M).Append(r.CapLimit)
                        .All(x => Math.Round(x, 4, MidpointRounding.AwayFromZero) == x);

                // ตรวจ Metadata 6 ฟิลด์ที่ส่งต่อให้ PBI-08 (AC 7.5)
                bool metadataOk = r.HistoryMonths == sku.HistoryMonths && r.AvgDemand == sku.AvgDemand && r.Sd == sku.Sd &&
                    r.Skewness == sku.Skewness && r.Kurtosis == sku.Kurtosis && r.JbPValue == sku.JbPValue;

                // เทียบผลลัพธ์ทีละฟิลด์
                bool isPass = q1 == tc.ExpectedQ1 &&
                    q3 == tc.ExpectedQ3 &&
                    iqr == tc.ExpectedIqr &&
                    r.CleansingMethod == tc.ExpectedMethod &&
                    r.CapLimit == tc.ExpectedCapLimit &&
                    r.QtyReduced24M.Count(q => q > 0m) == tc.ExpectedCappedMonths &&
                    r.QtyReduced24M.Sum() == tc.ExpectedTotalReduced &&
                    r.CleansedDemand24M.Sum() == tc.ExpectedTotalCleansed &&
                    monthsOk && conserved && arraysOk && metadataOk;

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
                Console.WriteLine($"   Input    : demand24M=[{string.Join(", ", tc.Demand24M)}], skew={tc.Skewness}, kurt={tc.Kurtosis}");
                Console.WriteLine($"   Expected : q1={tc.ExpectedQ1:F4}, q3={tc.ExpectedQ3:F4}, iqr={tc.ExpectedIqr:F4}, method={tc.ExpectedMethod}, capLimit={tc.ExpectedCapLimit:F4}, cappedMonths={tc.ExpectedCappedMonths}, totalReduced={tc.ExpectedTotalReduced:F4}, totalCleansed={tc.ExpectedTotalCleansed:F4}");
                Console.WriteLine($"   Actual   : q1={q1:F4}, q3={q3:F4}, iqr={iqr:F4}, method={r.CleansingMethod}, capLimit={r.CapLimit:F4}, cappedMonths={r.QtyReduced24M.Count(q => q > 0m)}, totalReduced={r.QtyReduced24M.Sum():F4}, totalCleansed={r.CleansedDemand24M.Sum():F4}");
                Console.WriteLine($"   Checks   : monthsOk={monthsOk}, conserved={conserved}, arraysOk={arraysOk}, metadataOk={metadataOk}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }
    }
}