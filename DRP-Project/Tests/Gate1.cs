using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class Gate1
    {
        // เกณฑ์ ADI ที่ใช้ทดสอบ (ค่าเดียวกับ M_LOV: ADI_THRESHOLD)
        private const decimal AdiThreshold = 1.32m;

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-03-01 ถึง TC-03-12)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Demand24M,            // ตำแหน่งที่ 3  <- Input (ยอดขาย 24 เดือน)
            int ExpectedHistoryMonths,      // ตำแหน่งที่ 4  <- ผลที่ควรได้ (FM-03)
            int ExpectedActiveMonths,       // ตำแหน่งที่ 5  <- ผลที่ควรได้ (FM-04)
            decimal ExpectedAdi,            // ตำแหน่งที่ 6  <- ผลที่ควรได้ (FM-05)
            decimal ExpectedSd,             // ตำแหน่งที่ 7  <- ผลที่ควรได้ (FM-07)
            string ExpectedStatus,          // ตำแหน่งที่ 8  <- ผลที่ควรได้ (KEEP / PENDING)
            string? ExpectedMethod,         // ตำแหน่งที่ 9  <- ผลที่ควรได้ ('KEEP' หรือ null)
            string? ExpectedReason,         // ตำแหน่งที่ 10 <- ผลที่ควรได้ (เหตุผล หรือ null)
            int SdDecimals = 4              // จำนวนทศนิยมที่ใช้ปัดค่า SD ก่อนเทียบ (ปกติ 4)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-03-01 ถึง TC-03-12) ----------
            decimal[] alternating = new decimal[24];
            for (int i = 0; i < 24; i++)
                alternating[i] = (i % 2 == 0) ? 10.0001m : 10.0002m;

            List<TestCase> cases = new()
            {
                new("TC-03-01", "Normal Eligible for Gate 2",
                    new decimal[] { 55, 60, 52, 68, 50, 65, 58, 70, 53, 62, 57, 66, 54, 61, 59, 67, 51, 64, 56, 69, 52, 63, 58, 60 },
                    24, 24, 1.0000m, 6.0211m, "PENDING", null, null),

                new("TC-03-02", "Short History (< 12M)",
                    Months(100m, R(17, 24)),
                    8, 8, 3.0000m, 48.1543m, "KEEP", "KEEP", "History < 12M"),

                new("TC-03-03", "History Boundary 11M",
                    Months(50m, R(14, 24)),
                    11, 11, 2.1818m, 25.4489m, "KEEP", "KEEP", "History < 12M"),

                new("TC-03-04", "History Boundary 12M (Fail ADI)",
                    Months(100m, 13, 15, 17, 19, 21, 23),
                    12, 6, 4.0000m, 44.2326m, "KEEP", "KEEP", "Intermittent Demand (ADI > 1.32)"),

                new("TC-03-05", "Intermittent Demand (Active 15)",
                    Months(100m, R(1, 15)),
                    24, 15, 1.6000m, 49.4535m, "KEEP", "KEEP", "Intermittent Demand (ADI > 1.32)"),

                new("TC-03-06", "ADI Boundary (Active 18)",
                    Months(100m, R(1, 18)),
                    24, 18, 1.3333m, 44.2326m, "KEEP", "KEEP", "Intermittent Demand (ADI > 1.32)"),

                new("TC-03-07", "ADI Non-Intermittent (Active 19)",
                    Months(100m, R(1, 19)),
                    24, 19, 1.2632m, 41.4851m, "PENDING", null, null),

                new("TC-03-08", "Zero Variance (Flat Demand)",
                    Months(100m, R(1, 24)),
                    24, 24, 1.0000m, 0.0000m, "KEEP", "KEEP", "Zero Variance (SD = 0)"),

                new("TC-03-09", "Zero Sales All 24 Months",
                    Months(0m),
                    0, 0, 0.0000m, 0.0000m, "KEEP", "KEEP", "History < 12M"),

                new("TC-03-10", "Single Sales Month (History = 1M)",
                    Months(50m, 24),
                    1, 1, 24.0000m, 10.2062m, "KEEP", "KEEP", "History < 12M"),

                new("TC-03-11", "Decimal Precision (SD > 0)",
                    alternating,
                    24, 24, 1.0000m, 0.00005m, "PENDING", null, null, 5),

                new("TC-03-12", "Precedence Order (History first)",
                    Months(50m, R(17, 24)),
                    8, 8, 3.0000m, 24.0772m, "KEEP", "KEEP", "History < 12M"),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: CleansingEngine Keep Gate 1 (FM-03 ถึง FM-07) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง SKU จำลอง แล้วเรียก Gate 1 โดยส่ง input เข้าไปตรงๆ
                var c = new SkuCleansingCandidate { warehouse_code = "WH", item_id = tc.Id, Demand24M = tc.Demand24M };
                CleansingEngine.GateEvaluate_1(c, AdiThreshold);

                // เทียบผลลัพธ์ทีละฟิลด์ (ADI ปัด 4 ตำแหน่ง, SD ปัดตาม SdDecimals)
                bool isPass = c.HistoryMonths == tc.ExpectedHistoryMonths &&
                    c.ActiveMonths == tc.ExpectedActiveMonths &&
                    Math.Round(c.Adi, 4) == tc.ExpectedAdi &&
                    Math.Round(c.Sd, tc.SdDecimals) == tc.ExpectedSd &&
                    c.CleansingStatus == tc.ExpectedStatus &&
                    c.CleansingMethod == tc.ExpectedMethod &&
                    c.CleansingReason == tc.ExpectedReason;

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
                Console.WriteLine($"   Input    : demand24M=[{string.Join(", ", tc.Demand24M)}]");
                Console.WriteLine($"   Expected : history={tc.ExpectedHistoryMonths}, active={tc.ExpectedActiveMonths}, adi={tc.ExpectedAdi}, sd={tc.ExpectedSd}, status={tc.ExpectedStatus}, method={tc.ExpectedMethod ?? "null"}, reason={tc.ExpectedReason ?? "null"}");
                Console.WriteLine($"   Actual   : history={c.HistoryMonths}, active={c.ActiveMonths}, adi={c.Adi}, sd={c.Sd}, status={c.CleansingStatus}, method={c.CleansingMethod ?? "null"}, reason={c.CleansingReason ?? "null"}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }

        // ตัวช่วยสร้างอาเรย์ 24 งวด: ใส่ qty ในเดือนที่ระบุ (เดือน 1..24) ที่เหลือเป็น 0
        // เช่น Months(100m, 3, 5) = เดือน 3 และ 5 ขาย 100 ที่เหลือ 0
        private static decimal[] Months(decimal qty, params int[] months)
        {
            decimal[] demand = new decimal[24];
            foreach (int m in months)
                demand[m - 1] = qty;    // เดือน 1 อยู่ที่ index 0
            return demand;
        }

        // ตัวช่วยสร้างลำดับเดือนต่อเนื่อง เช่น R(17, 24) = 17,18,...,24
        private static int[] R(int from, int to)
        {
            return Enumerable.Range(from, to - from + 1).ToArray();
        }
    }
}
