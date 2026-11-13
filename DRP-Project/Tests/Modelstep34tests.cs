using DRP_Project.src.Cleansing;
using DRP_Project.src.ModelRouting;

namespace DRP_Project.Tests
{
    // Unit Test ของ ModelEngine (PBI-10 Step 3 & Step 4) ตรงตาม Jira SCRUM-21 (TC-10-01 ถึง TC-10-10)
    // ตรวจตรรกะคัดกรองสินค้าใหม่ประวัติสั้น (1.4 <12M SMA) และสินค้ายานานๆ ขายที (1.5 Croston Method)
    // รันด้วยแฟล็ก --test-model-34 (ไม่ต่อฐานข้อมูล)
    class ModelStep34Tests
    {
        // ข้อความโมเดลตามข้อกำหนด PBI-10
        private const string SmaName = "Simple Moving Average (<12M SMA)";
        private const string SmaReason = "Sales history is less than 12 months";
        private const string CrostonName = "Croston Method";
        private const string CrostonReason = "Intermittent demand pattern with ADI > 1.32";

        // ข้อความโมเดลที่ PBI-09 ติดป้ายไว้แล้ว (ใช้ใน TC-10-09)
        private const string NoSalesName = "No Sales";
        private const string NoSalesReason = "No sales in the past 24 months (Active Months = 0)";
        private const string L3MName = "No Sales Last 3 Months (No Sales L3M)";
        private const string L3MReason = "No sales in the last 3 consecutive months";

        // เกณฑ์ ADI_THRESHOLD ค่ามาตรฐาน (ค่า Default ของ M_LOV)
        private const decimal DefaultAdi = 1.320000m;

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-10-01 ถึง TC-10-08)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal[] CleansedDemand24M,    // ตำแหน่งที่ 3  <- Input (ยอดขายสะอาด 24 งวด Index 0..23)
            int HistoryMonths,              // ตำแหน่งที่ 4  <- Input (Metadata FM-03)
            int CleanActiveMonths,          // ตำแหน่งที่ 5  <- Input (Metadata FM-04)
            decimal CleanAdi,               // ตำแหน่งที่ 6  <- Input (Metadata FM-05 = 24 / ActiveMonths)
            decimal AdiThreshold,           // ตำแหน่งที่ 7  <- Input (ADI_THRESHOLD จาก M_LOV)
            string? ExpectedModelId,        // ตำแหน่งที่ 8  <- ผลที่ควรได้ ("1.4" / "1.5" / null = ส่งต่อ PBI-11)
            string? ExpectedModelName,      // ตำแหน่งที่ 9  <- ผลที่ควรได้
            string? ExpectedReason,         // ตำแหน่งที่ 10 <- ผลที่ควรได้
            bool ExpectedShortCircuited     // ตำแหน่งที่ 11 <- ผลที่ควรได้ (true = ยุติ Model Priority Chain)
        );

        private static int _passCount;
        private static int _failCount;

        public static void RunUnitTests()
        {
            _passCount = 0;
            _failCount = 0;

            Console.WriteLine("===== Unit Test: ModelEngine (PBI-10 Step 3 & 4) =====\n");

            // 1. ตรรกะเลือกโมเดลหลัก TC-10-01 ถึง TC-10-08
            RunMainRoutingTests();

            // 2. Pass-Through จาก Step 1 & 2 (TC-10-09) และ Fallback ค่า ADI_THRESHOLD (TC-10-10)
            RunPassThroughAndFallbackTests();

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {_passCount} / FAIL {_failCount} (ทั้งหมด {_passCount + _failCount} เคส)");
            Console.WriteLine("=========================================");
        }

        // ---------- TC-10-01 ถึง TC-10-08: ตรรกะเลือกโมเดล (ตรวจ 4 ฟิลด์ Routing) ----------
        private static void RunMainRoutingTests()
        {
            List<TestCase> cases = new()
            {
                // เริ่มขาย M16 -> ประวัติ 9 เดือน < 12 -> Step 3 (1.4)
                new("TC-10-01", "Standard New Product Short History (1.4 <12M SMA)",
                    Segments((0m, 15), (50m, 9)),            // M1..M15 = 0, M16..M24 = 50
                    9, 9, 2.6667m, DefaultAdi,
                    "1.4", SmaName, SmaReason, true),

                // ขอบเขตบน Step 3: ประวัติ 11 เดือน < 12 -> 1.4
                new("TC-10-02", "Step 3 Upper Boundary (HistoryMonths = 11 < 12)",
                    Segments((0m, 13), (60m, 11)),           // M1..M13 = 0, M14..M24 = 60
                    11, 11, 2.1818m, DefaultAdi,
                    "1.4", SmaName, SmaReason, true),

                // ขอบเขตล่าง Step 3: ประวัติ 12 เดือนพอดี -> ไม่เข้า Step 3 -> Step 4: ADI 2.0 > 1.32 -> 1.5
                new("TC-10-03", "Step 3 Lower Exclusion Boundary (HistoryMonths = 12 -> 1.5 Croston)",
                    Segments((0m, 12), (100m, 12)),          // M1..M12 = 0, M13..M24 = 100
                    12, 12, 2.0000m, DefaultAdi,
                    "1.5", CrostonName, CrostonReason, true),

                // ประวัติสั้น (6 เดือน) และ ADI สูง (8.0 > 1.32) พร้อมกัน -> Step 3 ต้องชนะ Step 4 เสมอ
                new("TC-10-04", "Strict Step 3 over Step 4 Priority Override (Conflict Case)",
                    Segments((0m, 18), (40m, 1), (0m, 2), (30m, 1), (0m, 1), (20m, 1)),   // ขาย M19, M22, M24
                    6, 3, 8.0000m, DefaultAdi,
                    "1.4", SmaName, SmaReason, true),

                // ประวัติครบ 24 เดือน ขาย 10 เดือน -> ADI 2.4 > 1.32 -> Step 4 (1.5)
                new("TC-10-05", "Standard Intermittent Demand with Full History (1.5 Croston Method)",
                    new decimal[] { 50m, 0m, 50m, 0m, 0m, 50m, 0m, 50m, 0m, 0m, 50m, 0m,
                                    0m, 50m, 0m, 50m, 0m, 0m, 50m, 0m, 0m, 50m, 0m, 50m },
                    24, 10, 2.4000m, DefaultAdi,
                    "1.5", CrostonName, CrostonReason, true),

                // ขาย 18 เดือน -> ADI 1.3333 > 1.32 (จำนวนเดือนขายมากสุดที่ยังเข้า 1.5) -> 1.5
                new("TC-10-06", "Step 4 Discrete Boundary Above Threshold (18 Active Months -> CleanAdi = 1.3333 > 1.32)",
                    Segments((20m, 17), (0m, 6), (20m, 1)),  // M1..M17 = 20, M18..M23 = 0, M24 = 20
                    24, 18, 1.3333m, DefaultAdi,
                    "1.5", CrostonName, CrostonReason, true),

                // ขาย 19 เดือน -> ADI 1.2632 <= 1.32 -> ไม่เข้า Step 3 & 4 ส่งต่อ PBI-11
                new("TC-10-07", "Step 4 Discrete Boundary Below Threshold (19 Active Months -> CleanAdi = 1.2632 <= 1.32)",
                    Segments((30m, 18), (0m, 5), (30m, 1)),  // M1..M18 = 30, M19..M23 = 0, M24 = 30
                    24, 19, 1.2632m, DefaultAdi,
                    null, null, null, false),

                // ADI = ADI_THRESHOLD พอดี (1.5000 = 1.500000) -> ต้องใช้ > เคร่งครัด จึงไม่เข้า 1.5 ส่งต่อ PBI-11
                new("TC-10-08", "Step 4 Exact Threshold Equality Boundary (CleanAdi == ADI_THRESHOLD -> Step 5)",
                    Segments((25m, 15), (0m, 8), (25m, 1)),  // M1..M15 = 25, M16..M23 = 0, M24 = 25
                    24, 16, 1.5000m, 1.500000m,
                    null, null, null, false),
            };

            foreach (TestCase tc in cases)
            {
                // สร้าง State จำลองที่ PBI-09 ส่งต่อมา (ยังไม่ถูกตัดจบ) แล้วเรียก Engine ผ่านทางเข้าจริงที่ Pipeline ใช้
                SkuModelRoutingState state = CreateTestState(tc.Id, tc.CleansedDemand24M, tc.HistoryMonths, tc.CleanActiveMonths, tc.CleanAdi);
                SkuModelRoutingState result = RunModel1415(new List<SkuModelRoutingState> { state }, tc.AdiThreshold)[0];

                // เทียบผลลัพธ์ทีละฟิลด์ + ตรวจว่า Metadata ในตาราง Jira สะท้อนเวกเตอร์ยอดขายจริง (กันพิมพ์เลขผิด)
                bool metadataMatchesVector = MetadataMatchesVector(tc.CleansedDemand24M, tc.HistoryMonths, tc.CleanActiveMonths, tc.CleanAdi);
                bool isPass = HasRouting(result, tc.ExpectedModelId, tc.ExpectedModelName, tc.ExpectedReason, tc.ExpectedShortCircuited) &&
                              metadataMatchesVector;

                RecordResult(isPass, tc.Id, tc.Description,
                    $"demand24M=[{string.Join(", ", tc.CleansedDemand24M)}], history={tc.HistoryMonths}, activeMonths={tc.CleanActiveMonths}, cleanAdi={tc.CleanAdi}, adiThreshold={tc.AdiThreshold}",
                    $"id={tc.ExpectedModelId ?? "null"}, name={tc.ExpectedModelName ?? "null"}, reason={tc.ExpectedReason ?? "null"}, shortCircuited={tc.ExpectedShortCircuited}, metadataMatchesVector=True",
                    $"id={result.AssignedModelId ?? "null"}, name={result.AssignedModelName ?? "null"}, reason={result.SelectionReason ?? "null"}, shortCircuited={result.IsShortCircuited}, metadataMatchesVector={metadataMatchesVector}");
            }
        }

        // ---------- TC-10-09 และ TC-10-10: เคสพิเศษ (ตรวจต่างจากตารางหลัก) ----------
        private static void RunPassThroughAndFallbackTests()
        {
            // ===== TC-10-09: Short-Circuit Pass-Through from Step 1 & 2 =====
            // SKU A ได้ 1.7 แล้ว (ประวัติ 0 < 12) และ SKU B ได้ 1.6 แล้ว (ADI 2.4 > 1.32)
            // ถ้า Engine เผลอประเมินซ้ำ A จะกลายเป็น 1.4 และ B จะกลายเป็น 1.5 -> ต้องคงค่าเดิม 100% และยังอยู่ในผลลัพธ์ครบ
            var skuA = CreateTestState("TC-10-09-A", Segments((0m, 24)), 0, 0, 0.0000m);
            skuA.AssignedModelId = "1.7";
            skuA.AssignedModelName = NoSalesName;
            skuA.SelectionReason = NoSalesReason;
            skuA.IsShortCircuited = true;

            var skuB = CreateTestState("TC-10-09-B",
                new decimal[] { 50m, 0m, 50m, 0m, 50m, 0m, 50m, 0m, 50m, 0m, 50m, 0m,
                                50m, 0m, 50m, 0m, 50m, 0m, 50m, 0m, 0m, 0m, 0m, 0m },
                24, 10, 2.4000m);
            skuB.AssignedModelId = "1.6";
            skuB.AssignedModelName = L3MName;
            skuB.SelectionReason = L3MReason;
            skuB.IsShortCircuited = true;

            List<SkuModelRoutingState> passThrough = RunModel1415(new List<SkuModelRoutingState> { skuA, skuB }, DefaultAdi);

            bool passThroughPass = passThrough.Count == 2 &&
                                   HasRouting(passThrough[0], "1.7", NoSalesName, NoSalesReason, true) &&
                                   HasRouting(passThrough[1], "1.6", L3MName, L3MReason, true) &&
                                   passThrough[0].HistoryMonths == 0 && passThrough[0].CleanActiveMonths == 0 && passThrough[0].CleanAdi == 0.0000m &&
                                   passThrough[1].HistoryMonths == 24 && passThrough[1].CleanActiveMonths == 10 && passThrough[1].CleanAdi == 2.4000m;

            RecordResult(passThroughPass, "TC-10-09", "Short-Circuit Pass-Through from Step 1 & 2 (Preservation of Prior Decisions)",
                "SKU A: ได้ 1.7 แล้ว (history=0, active=0, adi=0.0000) | SKU B: ได้ 1.6 แล้ว (history=24, active=10, adi=2.4000)",
                "Count=2 ตามลำดับ | A: id=1.7, name=No Sales, shortCircuited=true | B: id=1.6, name=No Sales L3M, shortCircuited=true | Metadata ไม่เปลี่ยน (ไม่ถูกประเมินซ้ำ)",
                $"Count={passThrough.Count} | " + string.Join(" | ", passThrough.Select(s =>
                    $"{s.ItemId}: id={s.AssignedModelId ?? "null"}, name={s.AssignedModelName ?? "null"}, shortCircuited={s.IsShortCircuited}, history={s.HistoryMonths}, active={s.CleanActiveMonths}, adi={s.CleanAdi}")));

            // ===== TC-10-10: M_LOV Configuration Fallback Guard =====
            // M_LOV ไม่พบ ADI_THRESHOLD -> Pipeline ส่งค่า Default ให้ Engine (การอ่านจาก DB อยู่ใน LoadLovDecimal ซึ่ง Unit Test นี้ไม่ต่อฐานข้อมูล)
            // จึงตรวจ 2 อย่าง: (1) ค่า Default ที่ Pipeline ใช้ = 1.320000 (2) Engine ใช้ค่านี้ตัดสินได้ถูกต้องโดยไม่โยน Exception
            decimal fallbackThreshold = ModelEngine.DefaultAdiThreshold;
            bool defaultOk = fallbackThreshold == 1.320000m;

            decimal[] fallbackDemand = Segments((20m, 17), (0m, 6), (20m, 1));   // M1..M17 = 20, M18..M23 = 0, M24 = 20
            var fallbackState = CreateTestState("TC-10-10", fallbackDemand, 24, 18, 1.3333m);

            bool noException = true;
            SkuModelRoutingState? fallbackResult = null;
            try
            {
                fallbackResult = RunModel1415(new List<SkuModelRoutingState> { fallbackState }, fallbackThreshold)[0];
            }
            catch (Exception)
            {
                noException = false;
            }

            bool fallbackPass = defaultOk && noException && fallbackResult != null &&
                                HasRouting(fallbackResult, "1.5", CrostonName, CrostonReason, true) &&
                                MetadataMatchesVector(fallbackDemand, 24, 18, 1.3333m);

            RecordResult(fallbackPass, "TC-10-10", "M_LOV Configuration Fallback Guard (Graceful Default Handling)",
                $"demand24M=[{string.Join(", ", fallbackDemand)}], history=24, activeMonths=18, cleanAdi=1.3333, M_LOV ไม่พบ ADI_THRESHOLD -> ใช้ค่า Default",
                "defaultThreshold=1.320000, noException=True, id=1.5, name=Croston Method, shortCircuited=true",
                $"defaultThreshold={fallbackThreshold}, noException={noException}, id={fallbackResult?.AssignedModelId ?? "null"}, name={fallbackResult?.AssignedModelName ?? "null"}, shortCircuited={fallbackResult?.IsShortCircuited}");
        }

        // พิมพ์ผล 1 เคส (รูปแบบเดียวกับ ModelStep12Tests / NormalityTests: Input -> Expected -> Actual) และนับ PASS / FAIL
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

        // รัน Model 1.4 แล้ว Model 1.5 ตามลำดับเดียวกับ Pipeline (ผ่าน ModelEngine ตัวเดียวกัน) แล้วคืน List เดิม
        private static List<SkuModelRoutingState> RunModel1415(List<SkuModelRoutingState> states, decimal adiThreshold)
        {
            ModelEngine.ApplyModel(states, ModelEngine.ApplyModel14);
            ModelEngine.ApplyModel(states, state => ModelEngine.ApplyModel15(state, adiThreshold));
            return states;
        }


        // สร้าง State จำลองจาก PBI-09 (ฟิลด์ที่ไม่เกี่ยวกับเคสใช้ค่ากลางๆ ฟิลด์ Routing เริ่มเป็น null / false = ยังไม่ถูกตัดจบ)
        private static SkuModelRoutingState CreateTestState(string id, decimal[] demand, int historyMonths, int activeMonths, decimal adi)
        {
            return new SkuModelRoutingState
            {
                WarehouseCode = "WH",
                ItemId = id,
                CleansedDemand24M = demand,
                CleanMean = 10.0000m,
                CleanSd = 10.0000m,
                CleanCv = 0.2000m,
                CleanActiveMonths = activeMonths,
                CleanAdi = adi,
                CleanTrendR = 0.0000m,
                CleanSeasonalR = 0.0000m,
                HistoryMonths = historyMonths,
                OutlierMethod = "KEEP",
                Skewness = null,
                Kurtosis = null,
                JbPValue = null,
                AssignedModelId = null,
                AssignedModelName = null,
                SelectionReason = null,
                IsShortCircuited = false
            };
        }

        // เทียบ 4 ฟิลด์ Routing ของ State กับค่าที่คาดหวัง
        private static bool HasRouting(SkuModelRoutingState s, string? id, string? name, string? reason, bool shortCircuited)
        {
            return s.AssignedModelId == id &&
                   s.AssignedModelName == name &&
                   s.SelectionReason == reason &&
                   s.IsShortCircuited == shortCircuited;
        }

        // ตรวจว่า Metadata (FM-03, FM-04, FM-05) ในตารางทดสอบตรงกับค่าที่คำนวณจากเวกเตอร์ยอดขายจริง (ใช้สูตรเดียวกับ PBI-03/08)
        private static bool MetadataMatchesVector(decimal[] demand, int historyMonths, int activeMonths, decimal adi)
        {
            int calcActive = CleansingEngine.CalculateActiveMonths(demand);
            decimal calcAdi = Math.Round(CleansingEngine.CalculateAdi(calcActive), 4, MidpointRounding.AwayFromZero);

            return CleansingEngine.CalculateHistoryMonths(demand) == historyMonths &&
                   calcActive == activeMonths &&
                   calcAdi == adi;
        }

        // ----- ตัวช่วยสร้างอาเรย์ 24 งวด -----

        // ต่อช่วงยอดขายตามลำดับ (ยอดขาย, จำนวนเดือน) เช่น Segments((0m, 15), (50m, 9)) = M1..M15 เป็น 0 และ M16..M24 เป็น 50
        // รวมต้องครบ 24 งวดพอดี (ไม่ครบจะโยน Exception ทันที กันพิมพ์เวกเตอร์ผิด)
        private static decimal[] Segments(params (decimal Qty, int Months)[] parts)
        {
            var demand = new List<decimal>();
            foreach (var part in parts)
            {
                demand.AddRange(Enumerable.Repeat(part.Qty, part.Months));
            }

            if (demand.Count != 24)
                throw new InvalidOperationException($"เวกเตอร์ทดสอบต้องมี 24 งวด (ได้ {demand.Count})");

            return demand.ToArray();
        }
    }
}