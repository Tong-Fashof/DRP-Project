using DRP_Project.src.Cleansing;
using DRP_Project.src.ModelRouting;

namespace DRP_Project.Tests
{
    // Unit Test ของ SesFallbackEngine (PBI-12 Step 7: โมเดล 1.3 SES) ตรง TC-12-01 ถึง TC-12-04
    // และ End-to-End (TC-E2E-01, TC-E2E-02): ModelEngine ทุก Step -> ForecastResultAssembler -> SesFallbackEngine
    // E2E เรียก Engine จริงของโปรเจกต์ทีละขั้นตามลำดับเดียวกับ Program.cs (เกณฑ์ใช้ Default ตามสเปก, ไม่ต่อฐานข้อมูล)
    // รันด้วยแฟล็ก --test-ses
    class SesFallbackTests
    {
        // ----- ตัวช่วยบันทึกค่า Expected / Actual ของแต่ละเคส (ใช้พิมพ์ผล Input -> Expected -> Actual) -----
        private static class TestContext
        {
            public static string CurrentInput = "";
            public static List<string> ExpectedList = new();
            public static List<string> ActualList = new();

            public static void Reset(string input)
            {
                CurrentInput = input;
                ExpectedList.Clear();
                ActualList.Clear();
            }

            public static void Record(string fieldName, object? expected, object? actual)
            {
                ExpectedList.Add($"{fieldName}={expected ?? "null"}");
                ActualList.Add($"{fieldName}={actual ?? "null"}");
            }

            public static void RecordDecimal(string fieldName, decimal expected, decimal actual)
            {
                ExpectedList.Add($"{fieldName}={expected:F4}");
                ActualList.Add($"{fieldName}={actual:F4}");
            }
        }

        private static class Assert
        {
            // เทียบ decimal แบบมี delta (ยอมรับความคลาดเคลื่อนจากการปัดเศษ 4 ตำแหน่ง)
            public static void AreEqual(decimal expected, decimal actual, decimal delta, string fieldName)
            {
                TestContext.RecordDecimal(fieldName, expected, actual);
                decimal diff = Math.Abs(expected - actual);
                if (diff > delta)
                    throw new Exception($"Assertion Failed for [{fieldName}]: Expected {expected:F4} (±{delta}), but got {actual:F4} (Diff: {diff:F4})");
            }

            public static void AreEqual<T>(T expected, T actual, string fieldName)
            {
                TestContext.Record(fieldName, expected, actual);
                if (!EqualityComparer<T>.Default.Equals(expected, actual))
                    throw new Exception($"Assertion Failed for [{fieldName}]: Expected '{expected}', but got '{actual}'");
            }

            public static void IsTrue(bool condition, string message)
            {
                TestContext.Record(message, true, condition);
                if (!condition)
                    throw new Exception($"Assertion Failed: {message}");
            }

            public static void Throws<T>(Action action, string testName) where T : Exception
            {
                TestContext.Record(testName, typeof(T).Name, "Executing");
                try
                {
                    action();
                }
                catch (T)
                {
                    TestContext.ActualList[TestContext.ActualList.Count - 1] = $"{testName}={typeof(T).Name}";
                    return;
                }
                catch (Exception ex)
                {
                    TestContext.ActualList[TestContext.ActualList.Count - 1] = $"{testName}={ex.GetType().Name}";
                    throw new Exception($"Assertion Failed for [{testName}]: Expected {typeof(T).Name}, but got {ex.GetType().Name}: {ex.Message}");
                }
                TestContext.ActualList[TestContext.ActualList.Count - 1] = $"{testName}=None";
                throw new Exception($"Assertion Failed for [{testName}]: Expected {typeof(T).Name}, but no exception was thrown.");
            }
        }

        private static int _passCount;
        private static int _failCount;

        // รัน 1 เคส: จับ Exception จาก Assert แล้วพิมพ์ PASS / FAIL (รูปแบบเดียวกับ Test อื่นของโปรเจกต์: Input -> Expected -> Actual)
        private static void RunTest(string testName, Action testAction, string inputSummary)
        {
            int colon = testName.IndexOf(':');
            string id = colon > 0 ? testName.Substring(0, colon).Trim() : testName;
            string desc = colon > 0 ? testName.Substring(colon + 1).Trim() : "";

            TestContext.Reset(inputSummary);
            try
            {
                testAction();
                _passCount++;
                Console.WriteLine($"[PASS] {id} - {desc}");
                PrintDetail(null);
            }
            catch (Exception ex)
            {
                _failCount++;
                Console.WriteLine($"[FAIL] {id} - {desc}");
                PrintDetail(ex.Message);
            }
        }

        private static void PrintDetail(string? error)
        {
            Console.WriteLine($"   Input    : {TestContext.CurrentInput}");
            if (TestContext.ExpectedList.Count > 0)
            {
                Console.WriteLine($"   Expected : {string.Join(", ", TestContext.ExpectedList)}");
                Console.WriteLine($"   Actual   : {string.Join(", ", TestContext.ActualList)}");
            }
            if (error != null) Console.WriteLine($"   Error    : {error}");
            Console.WriteLine();
        }

        private static void PrintSummary()
        {
            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {_passCount} / FAIL {_failCount} (ทั้งหมด {_passCount + _failCount} เคส)");
            Console.WriteLine("=========================================");
        }

        public static void RunUnitTests()
        {
            _passCount = 0;
            _failCount = 0;

            Console.WriteLine("===== Unit Test: SesFallbackEngine (PBI-12 Step 7) =====\n");
            RunTest("TC-12-01: Standard 24M Steady Sales Fallback to Step 7 (1.3 SES)", Test_TC12_01_HappyCase_StandardFallbackToSes, "history=24, CleanSeasonalR=0.1500, CleanTrendR=0.1200 (Standard Fallback to SES)");
            RunTest("TC-12-02: Multi-SKU Mixed Batch (7 Preserved Models + 1 Fallback Step 7 SKU)", Test_TC12_02_HappyEdge_MultiSkuMixedBatch, "Multi-SKU Mixed Batch (8 SKUs: 7 Prior Models Preserved + 1 Fallback SES)");
            RunTest("TC-12-03: Inconsistent Pipeline State Guard A (Model Assigned but Short-Circuit False)", Test_TC12_03_EdgeCase_InconsistentStateA, "Inconsistent State A: Model Assigned ('1.1') but IsShortCircuited = false");
            RunTest("TC-12-04: Inconsistent Pipeline State Guard B (Model Null but Short-Circuit True)", Test_TC12_04_EdgeCase_InconsistentStateB, "Inconsistent State B: Model Null but IsShortCircuited = true");
            Console.WriteLine("===== Unit Test: End-to-End Pipeline (ModelEngine -> Assembler -> SES) =====\n");
            RunTest("TC-E2E-01: Master Pipeline 7-Model Orchestration Chain", Test_TC_E2E_01_MasterPipelineChain, "Full End-to-End Orchestrator Chain with 10 SKUs (Covering Models 1.1 to 1.7)");
            RunTest("TC-E2E-02: Direct Handoff from CleansedSkuDemandDto (Step 6 -> Step 7)", Test_TC_E2E_02_ExecuteFromCleansedDtosHandoff, "Direct handoff from CleansedSkuDemandDto list covering Models 1.1 to 1.7");

            PrintSummary();
        }

        // ----- ตัวช่วยเรียก Engine จริง -----

        // Input จำลอง 1 SKU (ฟิลด์เหมือนข้อมูลที่ไหลมาใน Memory Pipeline) | Test แปลงเป็น State + Candidate ก่อนเรียก Engine จริง
        private class SkuPipelineCandidateInput
        {
            public string WarehouseCode { get; set; } = "";
            public string ItemId { get; set; } = "";
            public string? AsOfPeriod { get; set; }
            public decimal[] RawSalesQty24M { get; set; } = new decimal[24];
            public decimal[] OosLostQty24M { get; set; } = new decimal[24];
            public decimal[] NetDemandQty24M { get; set; } = new decimal[24];
            public decimal[] CleansedDemandQty24M { get; set; } = new decimal[24];
            public string? AssignedModelId { get; set; }
            public string? AssignedModelName { get; set; }
            public string? SelectionReason { get; set; }
            public bool IsShortCircuited { get; set; }
        }

        private static SkuModelRoutingState ToState(SkuPipelineCandidateInput i) => new SkuModelRoutingState
        {
            WarehouseCode = i.WarehouseCode,
            ItemId = i.ItemId,
            CleansedDemand24M = i.CleansedDemandQty24M,
            AssignedModelId = i.AssignedModelId,
            AssignedModelName = i.AssignedModelName,
            SelectionReason = i.SelectionReason,
            IsShortCircuited = i.IsShortCircuited
        };

        // เดือนที่พบ = 24 เสมอ (ตรวจ MISSING MONTHS ผ่านจำนวนแถวตอนโหลดจาก DB ซึ่ง Unit Test นี้ไม่ต่อฐานข้อมูล)
        private static SkuCleansingCandidate ToCandidate(SkuPipelineCandidateInput i) => new SkuCleansingCandidate
        {
            warehouse_code = i.WarehouseCode,
            item_id = i.ItemId,
            Demand24M = i.NetDemandQty24M,
            RawSales24M = i.RawSalesQty24M,
            OosUplift24M = i.OosLostQty24M,
            MonthsPresent = 24
        };

        // PBI-12: กำหนด 1.3 SES ให้ SKU ที่ยังไม่มีโมเดล (แก้ในที่ในรายการเดิม) แล้วคืนรายการเดิม
        private static List<SkuForecastResult> Evaluate(IEnumerable<SkuForecastResult> items)
        {
            List<SkuForecastResult> list = items.ToList();
            SesFallbackEngine.ApplyAll(list);
            return list;
        }

        // E2E: ยอดสะอาด -> DTO ของ PBI-08 (BuildDto ตัวจริง คำนวณ History/ADI/Trend/Seasonal) -> โมเดล 1.7..1.2 -> Assembler -> SES
        private static List<SkuForecastResult> ExecutePipeline(IEnumerable<SkuPipelineCandidateInput> inputs)
        {
            List<SkuPipelineCandidateInput> list = inputs.ToList();

            List<CleansedSkuDemandDto> dtos = list.Select(i => CleansedDatasetFinalizeEngine.BuildDto(new CleansedDemandSeries
            {
                WarehouseCode = i.WarehouseCode,
                ItemId = i.ItemId,
                OriginalDemand24M = i.NetDemandQty24M,
                CleansedDemand24M = i.CleansedDemandQty24M,
                QtyReduced24M = new decimal[24],
                CleansingMethod = "KEEP",
                HistoryMonths = CleansingEngine.CalculateHistoryMonths(i.CleansedDemandQty24M)
            })).ToList();

            return RunChain(dtos, list.Select(ToCandidate).ToList());
        }

        // E2E: เริ่มจาก DTO สะอาดที่ PBI-08 ส่งมาโดยตรง (ยอดขายดิบ = ยอดสะอาด, ยอด OOS = 0)
        private static List<SkuForecastResult> ExecuteFromCleansedDtos(List<CleansedSkuDemandDto> dtos)
        {
            List<SkuCleansingCandidate> candidates = dtos.Select(d => new SkuCleansingCandidate
            {
                warehouse_code = d.WarehouseCode,
                item_id = d.ItemId,
                Demand24M = d.CleansedDemand24M,
                RawSales24M = d.CleansedDemand24M,
                OosUplift24M = new decimal[24],
                MonthsPresent = 24
            }).ToList();

            return RunChain(dtos, candidates);
        }

        // ลำดับเดียวกับ Program.cs: 1.7 -> 1.6 -> 1.4 -> 1.5 -> 1.1 -> 1.2 -> Assembler (PBI-11B) -> SES (PBI-12)
        private static List<SkuForecastResult> RunChain(List<CleansedSkuDemandDto> dtos, List<SkuCleansingCandidate> candidates)
        {
            List<SkuModelRoutingState> states = ModelEngine.ToStates(dtos);
            ModelEngine.ApplyModel(states, ModelEngine.ApplyModel17);
            ModelEngine.ApplyModel(states, ModelEngine.ApplyModel16);
            ModelEngine.ApplyModel(states, ModelEngine.ApplyModel14);
            ModelEngine.ApplyModel(states, s => ModelEngine.ApplyModel15(s, ModelEngine.DefaultAdiThreshold));
            ModelEngine.ApplyModel(states, s => ModelEngine.ApplyModel11(s, ModelEngine.DefaultSeasonalThreshold));
            ModelEngine.ApplyModel(states, s => ModelEngine.ApplyModel12(s, ModelEngine.DefaultTrendThreshold));

            List<SkuForecastResult> results = ForecastResultAssembler.AssembleAll(states, candidates, "202610", new AssemblerConfig());
            SesFallbackEngine.ApplyAll(results);
            return results;
        }

        // ================= TC-12-01 ถึง TC-12-04 =================

        private static void Test_TC12_01_HappyCase_StandardFallbackToSes()
        {
            var item = new SkuForecastResult
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "MED-PARA-500",
                AsOfPeriod = "202610",
                RawTotal24M = 2400.0000m,
                OosTotal24M = 0.0000m,
                RawAvg = 100.0000m,
                RawSd = 5.2500m,
                RawCv = 0.0525m,
                RawTtlMth = 24,
                RawActiveMth = 24,
                RawAdi = 1.0000m,
                CleanTtlMth = 24,
                CleanActiveMth = 24,
                CleanAdi = 1.0000m,
                CleanMean = 100.0000m,
                CleanSd = 5.2500m,
                CleanCv = 0.0525m,
                CleanCv2 = 0.0028m,
                CleanLag12R = 0.3525m,
                DetectTrend = 0.0112m,
                DemandPattern = "Smooth",
                XyzClass = "X",
                AssignedModelId = null,
                DemandGroup = null,
                SelectionReason = null,
                IsShortCircuited = false,
                OutlierStatus24M = Enumerable.Repeat("NORMAL", 24).ToArray()
            };

            var results = Evaluate(new[] { item });
            Assert.AreEqual(1, results.Count, "Results count");
            var res = results[0];

            Assert.AreEqual("1.3", res.AssignedModelId, "assigned_model_id");
            Assert.AreEqual("G3", res.DemandGroup, "demand_group");
            Assert.AreEqual("NO STRONG TREND / SEASONAL SCREEN", res.SelectionReason, "selection_reason");
            Assert.IsTrue(res.IsShortCircuited, "is_short_circuited must be true");

            // Deterministic metrics preservation
            Assert.AreEqual(100.0000m, res.CleanMean, 0.0001m, "clean_mean");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "clean_adi");
            Assert.AreEqual(0.3525m, res.CleanLag12R.Value, 0.0001m, "clean_lag12_r");
            Assert.AreEqual(0.0112m, res.DetectTrend.Value, 0.0001m, "detect_trend");
        }

        /// <summary>
        /// TC-12-02: แบทช์คลังผสม 8 SKUs (7 โมเดลเดิม 1.1-1.7 คงค่าเดิม 100% ปลอดการกลายพันธุ์ + 1 SKU ใหม่ได้รับโมเดล 1.3/G3)
        /// </summary>
        private static void Test_TC12_02_HappyEdge_MultiSkuMixedBatch()
        {
            var batch = new List<SkuForecastResult>
            {
                // SKU-01: Step 5 Seasonal
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-01", AsOfPeriod = "202610",
                    AssignedModelId = "1.1", DemandGroup = "G1",
                    SelectionReason = "Full 24M history with strong seasonal pattern (Lag-12 r >= 0.5)",
                    IsShortCircuited = true, CleanLag12R = 0.9953m, DetectTrend = 0.1375m
                },
                // SKU-02: Step 6 Trend
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-02", AsOfPeriod = "202610",
                    AssignedModelId = "1.2", DemandGroup = "G2",
                    SelectionReason = "Abs(Trend r) >= 0.5 with no dominant seasonal pattern",
                    IsShortCircuited = true, CleanLag12R = 0.4812m, DetectTrend = 0.9995m
                },
                // SKU-03: Step 7 Pre-assigned SES
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-03", AsOfPeriod = "202610",
                    AssignedModelId = "1.3", DemandGroup = "G3",
                    SelectionReason = "NO STRONG TREND / SEASONAL SCREEN",
                    IsShortCircuited = true, CleanMean = 83.0000m
                },
                // SKU-04: Step 3 Short History SMA
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-04", AsOfPeriod = "202610",
                    AssignedModelId = "1.4", DemandGroup = "G4",
                    SelectionReason = "Sales history is less than 12 months",
                    IsShortCircuited = true, CleanTtlMth = 9
                },
                // SKU-05: Step 4 Croston
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-05", AsOfPeriod = "202610",
                    AssignedModelId = "1.5", DemandGroup = "G5",
                    SelectionReason = "Intermittent demand pattern with ADI > 1.32",
                    IsShortCircuited = true, CleanAdi = 3.0000m
                },
                // SKU-06: Step 2 No Sales L3M
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-06", AsOfPeriod = "202610",
                    AssignedModelId = "1.6", DemandGroup = "No Sales Last 3 Months",
                    SelectionReason = "No sales in the last 3 consecutive months",
                    IsShortCircuited = true, CleanActiveMth = 21
                },
                // SKU-07: Step 1 Dead Stock
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-07", AsOfPeriod = "202610",
                    AssignedModelId = "1.7", DemandGroup = "No Sales",
                    SelectionReason = "No sales in the past 24 months (Active Months = 0)",
                    IsShortCircuited = true, CleanActiveMth = 0
                },
                // SKU-08: Pending Step 7 Fallback
                new SkuForecastResult
                {
                    WarehouseCode = "MDC (B2B)", ItemId = "SKU-08", AsOfPeriod = "202610",
                    AssignedModelId = null, DemandGroup = null,
                    SelectionReason = null,
                    IsShortCircuited = false, CleanMean = 100.0000m, CleanAdi = 1.0000m
                }
            };

            var results = Evaluate(batch);
            Assert.AreEqual(8, results.Count, "Batch count");

            // Verify SKU-01 to SKU-07 preserved 100%
            Assert.AreEqual("1.1", results[0].AssignedModelId, "SKU-01 model");
            Assert.AreEqual("G1", results[0].DemandGroup, "SKU-01 demand_group");
            Assert.AreEqual("Full 24M history with strong seasonal pattern (Lag-12 r >= 0.5)", results[0].SelectionReason, "SKU-01 reason");
            Assert.IsTrue(results[0].IsShortCircuited, "SKU-01 short circuit");

            Assert.AreEqual("1.2", results[1].AssignedModelId, "SKU-02 model");
            Assert.AreEqual("G2", results[1].DemandGroup, "SKU-02 demand_group");
            Assert.AreEqual("Abs(Trend r) >= 0.5 with no dominant seasonal pattern", results[1].SelectionReason, "SKU-02 reason");
            Assert.IsTrue(results[1].IsShortCircuited, "SKU-02 short circuit");

            Assert.AreEqual("1.3", results[2].AssignedModelId, "SKU-03 model");
            Assert.AreEqual("G3", results[2].DemandGroup, "SKU-03 demand_group");
            Assert.AreEqual("NO STRONG TREND / SEASONAL SCREEN", results[2].SelectionReason, "SKU-03 reason");
            Assert.IsTrue(results[2].IsShortCircuited, "SKU-03 short circuit");

            Assert.AreEqual("1.4", results[3].AssignedModelId, "SKU-04 model");
            Assert.AreEqual("G4", results[3].DemandGroup, "SKU-04 demand_group");
            Assert.AreEqual("Sales history is less than 12 months", results[3].SelectionReason, "SKU-04 reason");
            Assert.IsTrue(results[3].IsShortCircuited, "SKU-04 short circuit");

            Assert.AreEqual("1.5", results[4].AssignedModelId, "SKU-05 model");
            Assert.AreEqual("G5", results[4].DemandGroup, "SKU-05 demand_group");
            Assert.AreEqual("Intermittent demand pattern with ADI > 1.32", results[4].SelectionReason, "SKU-05 reason");
            Assert.IsTrue(results[4].IsShortCircuited, "SKU-05 short circuit");

            Assert.AreEqual("1.6", results[5].AssignedModelId, "SKU-06 model");
            Assert.AreEqual("No Sales Last 3 Months", results[5].DemandGroup, "SKU-06 demand_group");
            Assert.AreEqual("No sales in the last 3 consecutive months", results[5].SelectionReason, "SKU-06 reason");
            Assert.IsTrue(results[5].IsShortCircuited, "SKU-06 short circuit");

            Assert.AreEqual("1.7", results[6].AssignedModelId, "SKU-07 model");
            Assert.AreEqual("No Sales", results[6].DemandGroup, "SKU-07 demand_group");
            Assert.AreEqual("No sales in the past 24 months (Active Months = 0)", results[6].SelectionReason, "SKU-07 reason");
            Assert.IsTrue(results[6].IsShortCircuited, "SKU-07 short circuit");

            // Verify SKU-08 allocated Step 7 Fallback SES
            Assert.AreEqual("1.3", results[7].AssignedModelId, "SKU-08 model");
            Assert.AreEqual("G3", results[7].DemandGroup, "SKU-08 demand_group");
            Assert.AreEqual("NO STRONG TREND / SEASONAL SCREEN", results[7].SelectionReason, "SKU-08 reason");
            Assert.IsTrue(results[7].IsShortCircuited, "SKU-08 short circuit");

            // Verify 100% complete coverage: zero null model ids
            Assert.IsTrue(results.All(s => !string.IsNullOrEmpty(s.AssignedModelId)), "All SKUs must have assigned model id");
            Assert.IsTrue(results.All(s => s.IsShortCircuited), "All SKUs must have is_short_circuited = true");
        }

        /// <summary>
        /// TC-12-03: Inconsistent State Guard A (Model Assigned but Short-Circuit False -> InvalidOperationException)
        /// </summary>
        private static void Test_TC12_03_EdgeCase_InconsistentStateA()
        {
            var item = new SkuForecastResult
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-INCONSISTENT-A",
                AsOfPeriod = "202610",
                AssignedModelId = "1.1",
                IsShortCircuited = false // Conflict!
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                Evaluate(new[] { item });
            }, "TC-12-03 Inconsistent State A");
        }

        /// <summary>
        /// TC-12-04: Inconsistent State Guard B (Model Null but Short-Circuit True -> InvalidOperationException)
        /// </summary>
        private static void Test_TC12_04_EdgeCase_InconsistentStateB()
        {
            var item = new SkuForecastResult
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-INCONSISTENT-B",
                AsOfPeriod = "202610",
                AssignedModelId = null,
                IsShortCircuited = true // Conflict!
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                Evaluate(new[] { item });
            }, "TC-12-04 Inconsistent State B");
        }
        // ================= TC-E2E-01, TC-E2E-02 =================

        private static void Test_TC_E2E_01_MasterPipelineChain()
        {
            var candidates = new List<SkuPipelineCandidateInput>();

            // SKU 1: New Product (Short History 6M -> Model 1.4 SMA)
            decimal[] d1 = new decimal[24];
            for (int i = 18; i < 24; i++) d1[i] = 100m;
            candidates.Add(new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-SMA-1.4",
                AsOfPeriod = "202610",
                RawSalesQty24M = d1,
                OosLostQty24M = new decimal[24],
                NetDemandQty24M = d1,
                CleansedDemandQty24M = d1,
                IsShortCircuited = false
            });

            // SKU 2: Intermittent Demand (Active = 5 months -> ADI = 4.80 > 1.32 -> Model 1.5 Croston)
            decimal[] d2 = new decimal[24];
            d2[0] = 50m; d2[6] = 50m; d2[12] = 50m; d2[18] = 50m; d2[23] = 50m;
            candidates.Add(new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-CROSTON-1.5",
                AsOfPeriod = "202610",
                RawSalesQty24M = d2,
                OosLostQty24M = new decimal[24],
                NetDemandQty24M = d2,
                CleansedDemandQty24M = d2,
                IsShortCircuited = false
            });

            // SKU 3: Seasonal Pattern (Strong lag-12 correlation -> Model 1.1 Seasonal TSD)
            decimal[] d3 = new decimal[24] {
                45m, 50m, 45m, 55m, 50m, 190m, 220m, 200m, 190m, 60m, 50m, 45m,
                45m, 50m, 45m, 55m, 50m, 190m, 220m, 200m, 190m, 60m, 50m, 45m
            };
            candidates.Add(new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-SEASONAL-1.1",
                AsOfPeriod = "202610",
                RawSalesQty24M = d3,
                OosLostQty24M = new decimal[24],
                NetDemandQty24M = d3,
                CleansedDemandQty24M = d3,
                IsShortCircuited = false
            });

            // SKU 4: Linear Upward Trend (Strong trend correlation -> Model 1.2 Trend DES)
            decimal[] d4 = new decimal[24];
            for (int i = 0; i < 24; i++) d4[i] = 100m + (i * 10m);
            candidates.Add(new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TREND-1.2",
                AsOfPeriod = "202610",
                RawSalesQty24M = d4,
                OosLostQty24M = new decimal[24],
                NetDemandQty24M = d4,
                CleansedDemandQty24M = d4,
                IsShortCircuited = false
            });

            // SKU 5: Steady Flat Sales (No strong seasonal / trend -> Step 7 Model 1.3 SES Fallback)
            decimal[] d5 = new decimal[24] {
                95.0000m, 105.0000m, 92.0000m, 108.0000m, 98.0000m, 102.0000m, 106.0000m, 94.0000m, 101.0000m, 99.0000m, 93.0000m, 107.0000m,
                98.0000m, 104.0000m, 96.0000m, 102.0000m, 105.0000m, 93.0000m, 101.0000m, 99.0000m, 94.0000m, 106.0000m, 97.0000m, 103.0000m
            };
            candidates.Add(new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-SES-1.3",
                AsOfPeriod = "202610",
                RawSalesQty24M = d5,
                OosLostQty24M = new decimal[24],
                NetDemandQty24M = d5,
                CleansedDemandQty24M = d5,
                IsShortCircuited = false
            });

            // Execute Master Orchestrator Pipeline
            var deliverables = ExecutePipeline(candidates);

            Assert.AreEqual(5, deliverables.Count, "Deliverables Count");

            // Verify SKU 1 -> 1.4 SMA
            var res1 = deliverables.First(x => x.ItemId == "SKU-SMA-1.4");
            Assert.AreEqual("1.4", res1.AssignedModelId, "SKU 1 Model");
            Assert.AreEqual("G4", res1.DemandGroup, "SKU 1 Demand Group");
            Assert.IsTrue(res1.IsShortCircuited, "SKU 1 Short-Circuited");

            // Verify SKU 2 -> 1.5 Croston
            var res2 = deliverables.First(x => x.ItemId == "SKU-CROSTON-1.5");
            Assert.AreEqual("1.5", res2.AssignedModelId, "SKU 2 Model");
            Assert.AreEqual("G5", res2.DemandGroup, "SKU 2 Demand Group");
            Assert.IsTrue(res2.IsShortCircuited, "SKU 2 Short-Circuited");

            // Verify SKU 3 -> 1.1 Seasonal TSD
            var res3 = deliverables.First(x => x.ItemId == "SKU-SEASONAL-1.1");
            Assert.AreEqual("1.1", res3.AssignedModelId, "SKU 3 Model");
            Assert.AreEqual("G1", res3.DemandGroup, "SKU 3 Demand Group");
            Assert.IsTrue(res3.IsShortCircuited, "SKU 3 Short-Circuited");

            // Verify SKU 4 -> 1.2 Trend DES
            var res4 = deliverables.First(x => x.ItemId == "SKU-TREND-1.2");
            Assert.AreEqual("1.2", res4.AssignedModelId, "SKU 4 Model");
            Assert.AreEqual("G2", res4.DemandGroup, "SKU 4 Demand Group");
            Assert.IsTrue(res4.IsShortCircuited, "SKU 4 Short-Circuited");

            // Verify SKU 5 -> 1.3 SES Fallback
            var res5 = deliverables.First(x => x.ItemId == "SKU-SES-1.3");
            Assert.AreEqual("1.3", res5.AssignedModelId, "SKU 5 Model");
            Assert.AreEqual("G3", res5.DemandGroup, "SKU 5 Demand Group");
            Assert.IsTrue(res5.IsShortCircuited, "SKU 5 Short-Circuited");
        }

        private static void Test_TC_E2E_02_ExecuteFromCleansedDtosHandoff()
        {
            var dtos = new List<CleansedSkuDemandDto>();

            // SKU 1: Model 1.7 Zero Demand (all 0)
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-ZERO-1.7",
                CleansedDemand24M = new decimal[24],
                CleanActiveMonths = 0,
                HistoryMonths = 0,
                OutlierMethod = "KEEP"
            });

            // SKU 2: Model 1.6 Discontinued (L3M == 0)
            decimal[] d2 = new decimal[24];
            for (int i = 0; i < 21; i++) d2[i] = 80m;
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-DISC-1.6",
                CleansedDemand24M = d2,
                CleanActiveMonths = 21,
                HistoryMonths = 24,
                OutlierMethod = "KEEP"
            });

            // SKU 3: Model 1.4 Short History (< 12M SMA)
            decimal[] d3 = new decimal[24];
            for (int i = 18; i < 24; i++) d3[i] = 100m;
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-SMA-1.4",
                CleansedDemand24M = d3,
                CleanActiveMonths = 6,
                HistoryMonths = 6,
                OutlierMethod = "KEEP"
            });

            // SKU 4: Model 1.5 Croston (Intermittent ADI > 1.32)
            decimal[] d4 = new decimal[24];
            d4[0] = 50m; d4[6] = 50m; d4[12] = 50m; d4[18] = 50m; d4[23] = 50m;
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-CROSTON-1.5",
                CleansedDemand24M = d4,
                CleanActiveMonths = 5,
                CleanAdi = 4.8000m,
                HistoryMonths = 24,
                OutlierMethod = "KEEP"
            });

            // SKU 5: Model 1.1 Seasonal TSD (Seasonal r >= 0.5)
            decimal[] d5 = new decimal[24] {
                45m, 50m, 45m, 55m, 50m, 190m, 220m, 200m, 190m, 60m, 50m, 45m,
                45m, 50m, 45m, 55m, 50m, 190m, 220m, 200m, 190m, 60m, 50m, 45m
            };
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-SEASONAL-1.1",
                CleansedDemand24M = d5,
                CleanActiveMonths = 24,
                CleanAdi = 1.0000m,
                CleanSeasonalR = 0.9850m,
                HistoryMonths = 24,
                OutlierMethod = "KEEP"
            });

            // SKU 6: Model 1.2 Trend DES (|Trend r| >= 0.5)
            decimal[] d6 = new decimal[24];
            for (int i = 0; i < 24; i++) d6[i] = 100m + (i * 10m);
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-TREND-1.2",
                CleansedDemand24M = d6,
                CleanActiveMonths = 24,
                CleanAdi = 1.0000m,
                CleanTrendR = 0.9990m,
                CleanSeasonalR = 0.0000m,
                HistoryMonths = 24,
                OutlierMethod = "KEEP"
            });

            // SKU 7: Model 1.3 SES Fallback
            decimal[] d7 = new decimal[24] {
                95.0000m, 105.0000m, 92.0000m, 108.0000m, 98.0000m, 102.0000m, 106.0000m, 94.0000m, 101.0000m, 99.0000m, 93.0000m, 107.0000m,
                98.0000m, 104.0000m, 96.0000m, 102.0000m, 105.0000m, 93.0000m, 101.0000m, 99.0000m, 94.0000m, 106.0000m, 97.0000m, 103.0000m
            };
            dtos.Add(new CleansedSkuDemandDto
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "DTO-SES-1.3",
                CleansedDemand24M = d7,
                CleanActiveMonths = 24,
                CleanAdi = 1.0000m,
                CleanTrendR = 0.0500m,
                CleanSeasonalR = 0.0200m,
                HistoryMonths = 24,
                OutlierMethod = "KEEP"
            });

            // Execute Handoff Adapter
            var deliverables = ExecuteFromCleansedDtos(dtos);

            Assert.AreEqual(7, deliverables.Count, "Deliverables Count");

            // Verify all 7 Models
            var r1 = deliverables.First(x => x.ItemId == "DTO-ZERO-1.7");
            Assert.AreEqual("1.7", r1.AssignedModelId, "SKU 1 Model (1.7)");
            Assert.AreEqual("No Sales", r1.DemandGroup, "SKU 1 Demand Group");
            Assert.IsTrue(r1.IsShortCircuited, "SKU 1 Short-Circuited");

            var r2 = deliverables.First(x => x.ItemId == "DTO-DISC-1.6");
            Assert.AreEqual("1.6", r2.AssignedModelId, "SKU 2 Model (1.6)");
            Assert.AreEqual("No Sales Last 3 Months", r2.DemandGroup, "SKU 2 Demand Group");
            Assert.IsTrue(r2.IsShortCircuited, "SKU 2 Short-Circuited");

            var r3 = deliverables.First(x => x.ItemId == "DTO-SMA-1.4");
            Assert.AreEqual("1.4", r3.AssignedModelId, "SKU 3 Model (1.4)");
            Assert.AreEqual("G4", r3.DemandGroup, "SKU 3 Demand Group");
            Assert.IsTrue(r3.IsShortCircuited, "SKU 3 Short-Circuited");

            var r4 = deliverables.First(x => x.ItemId == "DTO-CROSTON-1.5");
            Assert.AreEqual("1.5", r4.AssignedModelId, "SKU 4 Model (1.5)");
            Assert.AreEqual("G5", r4.DemandGroup, "SKU 4 Demand Group");
            Assert.IsTrue(r4.IsShortCircuited, "SKU 4 Short-Circuited");

            var r5 = deliverables.First(x => x.ItemId == "DTO-SEASONAL-1.1");
            Assert.AreEqual("1.1", r5.AssignedModelId, "SKU 5 Model (1.1)");
            Assert.AreEqual("G1", r5.DemandGroup, "SKU 5 Demand Group");
            Assert.IsTrue(r5.IsShortCircuited, "SKU 5 Short-Circuited");

            var r6 = deliverables.First(x => x.ItemId == "DTO-TREND-1.2");
            Assert.AreEqual("1.2", r6.AssignedModelId, "SKU 6 Model (1.2)");
            Assert.AreEqual("G2", r6.DemandGroup, "SKU 6 Demand Group");
            Assert.IsTrue(r6.IsShortCircuited, "SKU 6 Short-Circuited");

            var r7 = deliverables.First(x => x.ItemId == "DTO-SES-1.3");
            Assert.AreEqual("1.3", r7.AssignedModelId, "SKU 7 Model (1.3)");
            Assert.AreEqual("G3", r7.DemandGroup, "SKU 7 Demand Group");
            Assert.IsTrue(r7.IsShortCircuited, "SKU 7 Short-Circuited");
        }
    }
}
