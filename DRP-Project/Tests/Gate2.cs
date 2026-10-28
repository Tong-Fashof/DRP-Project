using DRP_Project.src.Cleansing;

namespace DRP_Project.Tests
{
    class Gate2
    {
        // เหตุผลที่ Gate 2 ใช้ (ตรงตาม DEC-PBI11: ฤดูกาลก่อนแนวโน้ม)
        private const string Seasonal = "Seasonal Protected (Lag-12 r >= 0.5)";
        private const string Trend = "Trend Protected (|r| >= 0.5)";

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-04-01 ถึง TC-04-12)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] Demand24M,            // ตำแหน่งที่ 3  <- Input (ยอดขาย 24 เดือน)
            int HistoryMonths,              // ตำแหน่งที่ 4  <- Input (จำนวนเดือนประวัติ ส่งต่อจาก Gate 1)
            decimal ProtectFlag,            // ตำแหน่งที่ 5  <- Input (PROTECT_TREND_SEASON จาก M_LOV)
            decimal ExpectedTrendR,         // ตำแหน่งที่ 6  <- ผลที่ควรได้ (FM-08)
            decimal ExpectedLag12R,         // ตำแหน่งที่ 7  <- ผลที่ควรได้ (FM-09)
            string ExpectedStatus,          // ตำแหน่งที่ 8  <- ผลที่ควรได้ (KEEP / PENDING)
            string? ExpectedMethod,         // ตำแหน่งที่ 9  <- ผลที่ควรได้ ('KEEP' หรือ null)
            string? ExpectedReason          // ตำแหน่งที่ 10 <- ผลที่ควรได้ (เหตุผล หรือ null)
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-04-01 ถึง TC-04-12) ----------
            decimal[] growth = { 20, 35, 30, 45, 55, 50, 70, 65, 80, 95, 90, 105, 115, 130, 125, 145, 140, 160, 155, 175, 170, 185, 200, 195 };

            List<TestCase> cases = new()
            {
                new("TC-04-01", "System Protect Flag Disabled (flag = 0)",
                    growth,
                    24, 0m, 0.9944m, -0.2936m, "PENDING", null, null),

                new("TC-04-02", "Growth Trend Up Protected",
                    growth,
                    24, 1m, 0.9944m, -0.2936m, "KEEP", "KEEP", Trend),

                new("TC-04-03", "Declining Trend Down Protected",
                    new decimal[] { 200, 185, 190, 175, 165, 170, 150, 155, 140, 125, 130, 115, 105, 90, 95, 75, 80, 60, 65, 45, 50, 35, 20, 25 },
                    24, 1m, -0.9944m, -0.2936m, "KEEP", "KEEP", Trend),

                new("TC-04-04", "Active Window Trend (15M History)",
                    Pad(9, 40, 50, 45, 60, 55, 70, 65, 80, 95, 90, 105, 100, 110, 115, 120),
                    15, 1m, 0.9817m, 0.0000m, "KEEP", "KEEP", Trend),

                new("TC-04-05", "Seasonal Pattern Repeat Protected",
                    new decimal[] { 40, 45, 50, 45, 55, 180, 210, 200, 170, 50, 45, 50,
                                    45, 50, 45, 55, 50, 190, 220, 200, 190, 60, 50, 45 },
                    24, 1m, 0.1375m, 0.9953m, "KEEP", "KEEP", Seasonal),

                new("TC-04-06", "Seasonal vs Trend Conflict (Precedence)",
                    new decimal[] { 30, 35, 40, 45, 50, 120, 140, 130, 110, 60, 65, 70,
                                    90, 95, 100, 105, 110, 200, 230, 215, 190, 120, 125, 130 },
                    24, 1m, 0.6974m, 0.9995m, "KEEP", "KEEP", Seasonal),

                new("TC-04-07", "Incomplete History for Seasonality (< 24M)",
                    Pad(6, 55, 62, 48, 65, 52, 58, 60, 50, 64, 53, 59, 61, 49, 63, 55, 58, 52, 60),
                    18, 1m, 0.0124m, 0.0000m, "PENDING", null, null),

                new("TC-04-08", "Zero SD in Year 1 (Flat Constant Y1)",
                    Flat(100m, 12).Concat(new decimal[] { 90, 110, 95, 105, 85, 115, 92, 108, 96, 104, 88, 112 }).ToArray(),
                    24, 1m, 0.0470m, 0.0000m, "PENDING", null, null),

                // เอกสารไม่ได้ระบุผลตัดสินไว้ (ข้อความขาด): Lag12R = 0 และ TrendR = 0.9996 ต้องได้ Trend
                new("TC-04-09", "Extreme Linear Trend Bound Guard",
                    new decimal[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
                                    135, 140, 155, 160, 175, 180, 195, 200, 215, 220, 235, 240 },
                    24, 1m, 0.9996m, 0.0000m, "KEEP", "KEEP", Trend),

                new("TC-04-10", "Normal Non-Trend Non-Seasonal Demand",
                    new decimal[] { 95, 105, 92, 108, 98, 102, 106, 94, 101, 99, 93, 107,
                                    98, 104, 96, 102, 105, 93, 101, 99, 94, 106, 97, 103 },
                    24, 1m, 0.0112m, 0.3525m, "PENDING", null, null),

                new("TC-04-11", "Seasonal Exact Threshold Boundary (Lag12R = 0.5000)",
                    new decimal[] { 80, 90, 100, 100, 110, 120, 120, 110, 100, 100, 90, 80,
                                    80, 110, 90, 100, 120, 100, 100, 120, 100, 90, 110, 80 },
                    24, 1m, 0.0000m, 0.5000m, "KEEP", "KEEP", Seasonal),

                new("TC-04-12", "Trend Exact Threshold Boundary (TrendR = 0.5000)",
                    new decimal[] { 117, 39, 106, 58, 110, 62, 109, 71, 103, 85, 107, 89,
                                    91, 113, 95, 117, 89, 131, 88, 140, 92, 144, 81, 163 },
                    24, 1m, 0.5000m, -0.6609m, "KEEP", "KEEP", Trend),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: CleansingEngine Keep Gate 2 (FM-08 ถึง FM-09) =====\n");

            foreach (TestCase tc in cases)
            {
                // สร้าง SKU จำลอง ใส่ค่าที่ Gate 2 รับต่อจาก Gate 1 (HistoryMonths, Sd) แล้วเรียก Gate 2
                var c = new SkuCleansingCandidate { warehouse_code = "WH", item_id = tc.Id, Demand24M = tc.Demand24M };
                c.HistoryMonths = tc.HistoryMonths;
                c.Sd = CleansingEngine.CalculateSd(c.Demand24M);
                CleansingEngine.GateEvaluate_2(c, tc.ProtectFlag);

                // เทียบผลลัพธ์ทีละฟิลด์ (TrendR และ Lag12R ปัด 4 ตำแหน่งมาจาก Engine แล้ว)
                bool isPass = c.TrendR == tc.ExpectedTrendR &&
                    c.Lag12R == tc.ExpectedLag12R &&
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
                Console.WriteLine($"   Input    : demand24M=[{string.Join(", ", tc.Demand24M)}], history={tc.HistoryMonths}, protectFlag={tc.ProtectFlag}");
                Console.WriteLine($"   Expected : trendR={tc.ExpectedTrendR}, lag12R={tc.ExpectedLag12R}, status={tc.ExpectedStatus}, method={tc.ExpectedMethod ?? "null"}, reason={tc.ExpectedReason ?? "null"}");
                Console.WriteLine($"   Actual   : trendR={c.TrendR}, lag12R={c.Lag12R}, status={c.CleansingStatus}, method={c.CleansingMethod ?? "null"}, reason={c.CleansingReason ?? "null"}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }

        // ตัวช่วยสร้างอาเรย์ 24 งวด: เติม 0 ไว้หน้า zeroMonths งวด แล้วตามด้วยยอดขายที่ระบุ (รวมต้องครบ 24)
        // เช่น Pad(9, 40, 50, ...) = เดือน 1..9 เป็น 0 และเดือน 10 เป็นต้นไปตามค่าที่ใส่
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
