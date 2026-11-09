using DRP_Project.src.Cleansing;
using DRP_Project.src.ModelRouting;

namespace DRP_Project.Tests
{
    // Unit Test ของ PriorityChainStep12Engine (PBI-09 Step 1 & Step 2) ตรงตาม Jira SCRUM-20 (TC-09-01 ถึง TC-09-08)
    // ตรวจตรรกะคัดกรองสินค้าไม่เคยขาย (1.7 No Sales) และสินค้าไม่มียอดขาย 3 เดือนล่าสุด (1.6 No Sales L3M)
    // รันด้วยแฟล็ก --test-priority-step12 (ไม่ต่อฐานข้อมูล)
    class ModelStep12Tests
    {
        // ข้อความโมเดลตามข้อกำหนด PBI-09
        private const string NoSalesName = "No Sales";
        private const string NoSalesReason = "No sales in the past 24 months (Active Months = 0)";
        private const string L3MName = "No Sales Last 3 Months (No Sales L3M)";
        private const string L3MReason = "No sales in the last 3 consecutive months";

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-09-01 ถึง TC-09-06)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] CleansedDemand24M,    // ตำแหน่งที่ 3  <- Input (ยอดขายสะอาด 24 งวด Index 0..23)
            int CleanActiveMonths,          // ตำแหน่งที่ 4  <- Input (Metadata จาก PBI-08)
            int HistoryMonths,              // ตำแหน่งที่ 5  <- Input (Metadata จาก PBI-08)
            decimal CleanMean,              // ตำแหน่งที่ 6  <- Input (Metadata จาก PBI-08)
            string? ExpectedModelId,        // ตำแหน่งที่ 7  <- ผลที่ควรได้ ("1.7" / "1.6" / null = ส่งต่อ Step 3)
            string? ExpectedModelName,      // ตำแหน่งที่ 8  <- ผลที่ควรได้
            string? ExpectedReason,         // ตำแหน่งที่ 9  <- ผลที่ควรได้
            bool ExpectedShortCircuited     // ตำแหน่งที่ 10 <- ผลที่ควรได้ (true = ยุติ Model Priority Chain)
        );

        private static int _passCount;
        private static int _failCount;

        public static void RunUnitTests()
        {
            _passCount = 0;
            _failCount = 0;

            Console.WriteLine("===== Unit Test: PriorityChainStep12Engine (PBI-09 Step 1 & 2) =====\n");

            // 1. ตรรกะเลือกโมเดลหลัก TC-09-01 ถึง TC-09-06
            RunMainRoutingTests();

            // 2. Metadata Conservation (TC-09-07) และ Guard & Batch Integrity (TC-09-08)
            RunMetadataAndGuardTests();

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {_passCount} / FAIL {_failCount} (ทั้งหมด {_passCount + _failCount} เคส)");
            Console.WriteLine("=========================================");
        }

        // ---------- TC-09-01 ถึง TC-09-06: ตรรกะเลือกโมเดล (ตรวจ 4 ฟิลด์ Routing) ----------
        private static void RunMainRoutingTests()
        {
            List<TestCase> cases = new()
            {
                // ยอดขาย 0 ตลอด 24 เดือน -> Step 1 (1.7) และยุติทันที
                new("TC-09-01", "Dead Stock 24M Zero Sales (Step 1)",
                    Flat(0m, 24),
                    0, 0, 0.0000m,
                    "1.7", NoSalesName, NoSalesReason, true),

                // M22..M24 เป็น 0 -> Step 2 (1.6) และยุติทันที
                new("TC-09-02", "Discontinued Item No Sales L3M (Step 2)",
                    BuildCustomDemand(50m, 0m, 0m, 0m),      // M1..M21 = 50, M22..M24 = 0
                    21, 24, 43.7500m,
                    "1.6", L3MName, L3MReason, true),

                // ขอบเขต: ขายใน M22 (Idx 21) -> ไม่เข้า L3M ส่งต่อ Step 3
                new("TC-09-03", "L3M Boundary: Sales in M22 Only",
                    BuildCustomDemand(40m, 10m, 0m, 0m),     // M1..M21 = 40, M22 = 10
                    22, 24, 35.4167m,
                    null, null, null, false),

                // ขอบเขต: ขายใน M24 (Idx 23) -> ไม่เข้า L3M ส่งต่อ Step 3
                new("TC-09-04", "L3M Boundary: Recovery in M24 Only",
                    BuildCustomDemand(30m, 0m, 0m, 5m),      // M1..M21 = 30, M24 = 5
                    22, 24, 26.4583m,
                    null, null, null, false),

                // ขายเฉพาะ M1 -> ผ่าน Step 1 (active = 1) แต่ตก Step 2 (1.6)
                new("TC-09-05", "Single Sale in M1, Zero in M2..M24",
                    SingleSaleInM1(100m),
                    1, 24, 4.1667m,
                    "1.6", L3MName, L3MReason, true),

                // ขายสม่ำเสมอทั้ง 24 เดือน -> ผ่านทั้ง Step 1 & 2 ส่งต่อ Step 3
                new("TC-09-06", "Normal Active Item Pass-Through",
                    Flat(100m, 24),
                    24, 24, 100.0000m,
                    null, null, null, false),
            };

            foreach (TestCase tc in cases)
            {
                var dto = CreateTestDto(tc.Id, tc.CleansedDemand24M, tc.CleanActiveMonths, tc.HistoryMonths, tc.CleanMean, "KEEP");
                SkuModelRoutingState result = PriorityChainStep12Engine.Evaluate(dto);

                // เทียบผลลัพธ์ทีละฟิลด์
                bool isPass = result.AssignedModelId == tc.ExpectedModelId &&
                              result.AssignedModelName == tc.ExpectedModelName &&
                              result.SelectionReason == tc.ExpectedReason &&
                              result.IsShortCircuited == tc.ExpectedShortCircuited;

                RecordResult(isPass, tc.Id, tc.Description,
                    $"demand24M=[{string.Join(", ", tc.CleansedDemand24M)}], activeMonths={tc.CleanActiveMonths}, history={tc.HistoryMonths}, mean={tc.CleanMean}",
                    $"id={tc.ExpectedModelId ?? "null"}, name={tc.ExpectedModelName ?? "null"}, reason={tc.ExpectedReason ?? "null"}, shortCircuited={tc.ExpectedShortCircuited}",
                    $"id={result.AssignedModelId ?? "null"}, name={result.AssignedModelName ?? "null"}, reason={result.SelectionReason ?? "null"}, shortCircuited={result.IsShortCircuited}");
            }
        }

        // ---------- TC-09-07 และ TC-09-08: เคสพิเศษ (ตรวจต่างจากตารางหลัก) ----------
        private static void RunMetadataAndGuardTests()
        {
            // ===== TC-09-07: 19-Property Metadata Conservation =====
            // กลุ่ม KEEP: ActiveMonths = 18, Skewness/Kurtosis/JbPValue = null (ขาย 18 เดือนล่าสุด จึงต้องผ่านต่อ)
            var keepDto = CreateTestDto("TC-09-07-KEEP", PadDemand(6, Flat(30m, 18)), 18, 18, 22.5000m, "KEEP");
            keepDto.CleanSd = 14.0000m;
            keepDto.CleanCv = 0.6222m;
            keepDto.CleanAdi = 1.3333m;

            // กลุ่ม 2SIGMA: Skewness = 0.1250, Kurtosis = 0.3500, JbPValue = 0.6500
            var sigmaDto = CreateTestDto("TC-09-07-2SIGMA", Flat(100m, 24), 24, 24, 100.0000m, "2SIGMA");
            sigmaDto.CleanSd = 1.9000m;
            sigmaDto.CleanCv = 0.0190m;
            sigmaDto.CleanTrendR = 0.0500m;
            sigmaDto.CleanSeasonalR = 0.1000m;
            sigmaDto.Skewness = 0.1250m;
            sigmaDto.Kurtosis = 0.3500m;
            sigmaDto.JbPValue = 0.6500m;

            var inputs = new List<CleansedSkuDemandDto> { keepDto, sigmaDto };
            List<SkuModelRoutingState> outputs = PriorityChainStep12Engine.EvaluateAll(inputs);

            int propCount = typeof(SkuModelRoutingState).GetProperties().Length;   // 15 DTO + 4 Routing
            List<string> keepMismatches = FindMismatches(inputs[0], outputs[0]);
            List<string> sigmaMismatches = FindMismatches(inputs[1], outputs[1]);

            bool metadataPass = propCount == 19 &&
                                keepMismatches.Count == 0 && sigmaMismatches.Count == 0 &&
                                !outputs[0].IsShortCircuited && !outputs[1].IsShortCircuited;

            RecordResult(metadataPass, "TC-09-07", "19-Property Metadata Conservation",
                "KEEP (active=18, skew/kurt/jb=null) และ 2SIGMA (skew=0.1250, kurt=0.3500, jb=0.6500)",
                "properties=19, mismatches=[] ทั้ง 2 กลุ่ม (Metadata 15 ฟิลด์ตรง Input 100%), shortCircuited=false ทั้งคู่",
                $"properties={propCount}, KEEP mismatches=[{string.Join(",", keepMismatches)}], 2SIGMA mismatches=[{string.Join(",", sigmaMismatches)}], shortCircuited=[{outputs[0].IsShortCircuited},{outputs[1].IsShortCircuited}]");

            // ===== TC-09-08: Array Length Guard & Batch Integrity =====
            // Sub-case A: อาเรย์ 23 งวด หรือ null ต้องโยน ArgumentException
            bool throws23 = ThrowsArgumentException(new decimal[23]);
            bool throwsNull = ThrowsArgumentException(null!);

            // Sub-case B: Batch รวม 3 SKU (1.7, 1.6, Pending)
            var batch = new List<CleansedSkuDemandDto>
            {
                CreateTestDto("B1-1.7", Flat(0m, 24), 0, 0, 0m, "KEEP"),
                CreateTestDto("B2-1.6", BuildCustomDemand(50m, 0m, 0m, 0m), 21, 24, 43.7500m, "KEEP"),
                CreateTestDto("B3-Pending", Flat(100m, 24), 24, 24, 100.0000m, "KEEP"),
            };
            List<SkuModelRoutingState> batchResult = PriorityChainStep12Engine.EvaluateAll(batch);

            bool batchPass = batchResult.Count == 3 &&
                             batchResult[0].AssignedModelId == "1.7" &&
                             batchResult[1].AssignedModelId == "1.6" &&
                             batchResult[2].AssignedModelId == null && !batchResult[2].IsShortCircuited;

            RecordResult(throws23 && throwsNull && batchPass, "TC-09-08", "Array Length Guard & Batch Integrity",
                "Sub-case A: array 23 งวด และ null | Sub-case B: Batch 3 SKU (1.7, 1.6, Pending)",
                "A: throw ArgumentException ทั้งคู่ | B: Count=3 ตามลำดับ [1.7, 1.6, null]",
                $"A: array23 throw={throws23}, null throw={throwsNull} | B: Count={batchResult.Count}, models=[{string.Join(", ", batchResult.Select(r => r.AssignedModelId ?? "null"))}]");
        }

        // พิมพ์ผล 1 เคส (รูปแบบเดียวกับ Gate2 / NormalityTests: Input -> Expected -> Actual) และนับ PASS / FAIL
        private static void RecordResult(bool isPass, string id, string description, string input, string expected, string actual)
        {
            if (isPass) _passCount++;
            else _failCount++;

            Console.WriteLine($"[{(isPass ? "PASS" : "FAIL")}] {id} - {description}");
            Console.WriteLine($"   Input    : {input}");
            Console.WriteLine($"   Expected : {expected}");
            Console.WriteLine($"   Actual   : {actual}");
            Console.WriteLine();
        }

        // สร้าง DTO จำลองจาก PBI-08 (ฟิลด์ที่ไม่เกี่ยวกับเคสใช้ค่ากลางๆ ส่วน Normality Metadata เริ่มเป็น null)
        private static CleansedSkuDemandDto CreateTestDto(string id, decimal[] demand, int activeMonths, int historyMonths, decimal mean, string method)
        {
            return new CleansedSkuDemandDto
            {
                WarehouseCode = "WH",
                ItemId = id,
                CleansedDemand24M = demand,
                CleanMean = mean,
                CleanSd = 10.0000m,
                CleanCv = 0.2000m,
                CleanActiveMonths = activeMonths,
                CleanAdi = 1.0000m,
                CleanTrendR = 0.0000m,
                CleanSeasonalR = 0.0000m,
                HistoryMonths = historyMonths,
                OutlierMethod = method,
                Skewness = null,
                Kurtosis = null,
                JbPValue = null
            };
        }

        // ส่งอาเรย์เข้า Engine: คืน true ถ้าโยน ArgumentException (ตามที่คาดหวัง)
        private static bool ThrowsArgumentException(decimal[] demand)
        {
            try
            {
                PriorityChainStep12Engine.Evaluate(new CleansedSkuDemandDto { WarehouseCode = "WH", ItemId = "GUARD", CleansedDemand24M = demand });
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        // เทียบ Metadata 15 ฟิลด์ของ DTO กับ State คืนรายชื่อฟิลด์ที่ไม่ตรง (ว่าง = ตรงครบ)
        private static List<string> FindMismatches(CleansedSkuDemandDto d, SkuModelRoutingState s)
        {
            var bad = new List<string>();
            if (s.WarehouseCode != d.WarehouseCode) bad.Add(nameof(s.WarehouseCode));
            if (s.ItemId != d.ItemId) bad.Add(nameof(s.ItemId));
            if (!s.CleansedDemand24M.SequenceEqual(d.CleansedDemand24M)) bad.Add(nameof(s.CleansedDemand24M));
            if (s.CleanMean != d.CleanMean) bad.Add(nameof(s.CleanMean));
            if (s.CleanSd != d.CleanSd) bad.Add(nameof(s.CleanSd));
            if (s.CleanCv != d.CleanCv) bad.Add(nameof(s.CleanCv));
            if (s.CleanActiveMonths != d.CleanActiveMonths) bad.Add(nameof(s.CleanActiveMonths));
            if (s.CleanAdi != d.CleanAdi) bad.Add(nameof(s.CleanAdi));
            if (s.CleanTrendR != d.CleanTrendR) bad.Add(nameof(s.CleanTrendR));
            if (s.CleanSeasonalR != d.CleanSeasonalR) bad.Add(nameof(s.CleanSeasonalR));
            if (s.HistoryMonths != d.HistoryMonths) bad.Add(nameof(s.HistoryMonths));
            if (s.OutlierMethod != d.OutlierMethod) bad.Add(nameof(s.OutlierMethod));
            if (s.Skewness != d.Skewness) bad.Add(nameof(s.Skewness));
            if (s.Kurtosis != d.Kurtosis) bad.Add(nameof(s.Kurtosis));
            if (s.JbPValue != d.JbPValue) bad.Add(nameof(s.JbPValue));
            return bad;
        }

        // ----- ตัวช่วยสร้างอาเรย์ 24 งวด -----

        // ยอดขายคงที่ qty จำนวน n งวด
        private static decimal[] Flat(decimal qty, int n) => Enumerable.Repeat(qty, n).ToArray();

        // M1..M21 เท่ากัน แล้วระบุ M22, M23, M24 แยก เช่น BuildCustomDemand(50, 0, 0, 0) = M1..M21 เป็น 50 และ M22..M24 เป็น 0
        private static decimal[] BuildCustomDemand(decimal m1To21, decimal m22, decimal m23, decimal m24)
            => Flat(m1To21, 21).Concat(new[] { m22, m23, m24 }).ToArray();

        // ขายแค่ M1 (Idx 0) งวดเดียว ที่เหลือเป็น 0
        private static decimal[] SingleSaleInM1(decimal qty)
        {
            var demand = new decimal[24];
            demand[0] = qty;
            return demand;
        }

        // เติม 0 นำหน้า leadingZeros งวด แล้วตามด้วยยอดขายที่ระบุ (รวมต้องครบ 24)
        private static decimal[] PadDemand(int leadingZeros, decimal[] values)
            => new decimal[leadingZeros].Concat(values).ToArray();
    }
}