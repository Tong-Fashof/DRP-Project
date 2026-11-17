using DRP_Project.src.Cleansing;
using DRP_Project.src.ModelRouting;

namespace DRP_Project.Tests
{
    // Unit Test ของ ForecastResultAssembler (PBI-11B ประกอบผลลัพธ์ระดับ SKU 72 คอลัมน์) ตรง TC-11B-01 ถึง TC-11B-08
    // เรียก ForecastResultAssembler.AssembleAll ตัวจริง (Input จำลอง -> State + Candidate เหมือนที่ Pipeline ส่งเข้ามา)
    // ตรวจครบ 72 คอลัมน์ในเคสหลัก + สถานะ Outlier 24 เดือน | เวลา calculated_at / created_at ตรวจว่าเป็นเวลา UTC ช่วงที่รัน
    // รันด้วยแฟล็ก --test-assembler (ไม่ต่อฐานข้อมูล)
    class ForecastResultAssemblerTests
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

        private static DateTime _runStart;
        private static DateTime _runEnd;

        public static void RunUnitTests()
        {
            _passCount = 0;
            _failCount = 0;

            Console.WriteLine("===== Unit Test: ForecastResultAssembler (PBI-11B) =====\n");

            RunTest("TC-11B-01: Normal 2SIGMA Capping, OOS Uplift & Pending Step 7 Handoff", Test_TC11B_01_Normal2SigmaAndStep7Handoff, "Normal 2SIGMA Series, CapLimit=140, OosUplift=15.0000, Step 7 Handoff");
            RunTest("TC-11B-02: Right-Skewed 3.0IQR Capping & Extreme Outlier Detection", Test_TC11B_02_RightSkewed3IqrAndExtremeOutlier, "Right-Skewed 3.0IQR Series, CapLimit=63, Extreme Outliers Detected");
            RunTest("TC-11B-03: Protect Gate Pass-Through (Zero Capping Enforcement)", Test_TC11B_03_ProtectGatePassThrough, "Protect Gate Enabled (flag=1), Trend Protected Series, Zero Capping");
            RunTest("TC-11B-04: Short History Active Window (4 <= N < 12: Skew/JB=NULL, Caps!=NULL)", Test_TC11B_04_ShortHistoryActiveWindow, "History N=8 (4 <= N < 12), Active Window, Skew/JB=null, Caps!=null");
            RunTest("TC-11B-05: Ultra-Short History Active Window (N < 4: Both Skew/JB=NULL & Caps=NULL)", Test_TC11B_05_UltraShortHistoryActiveWindow, "History N=3 (N < 4), Ultra-Short History, Both Skew/JB=null, Caps=null");
            RunTest("TC-11B-06: Model 1.7 Zero Demand Early-Exit (DB Constraint Integrity)", Test_TC11B_06_ZeroDemandEarlyExit, "Model 1.7 Zero Demand Early-Exit (All 24M = 0)");
            RunTest("TC-11B-07: Multi-SKU Relative Rows & Warehouse XYZ Percentile Ranking", Test_TC11B_07_MultiSkuWarehouseXyzPercentile, "Multi-SKU Batch (3 SKUs), Warehouse XYZ Percentile Ranking");
            RunTest("TC-11B-08: Source Check Integrity & Inbound Validation Guards", Test_TC11B_08_SourceCheckAndInboundValidationGuards, "Source Check Integrity & Inbound Validation Guards (Missing/Corrupt Fields)");

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

        // แปลง Input จำลองเป็น State + Candidate แล้วเรียก ForecastResultAssembler.AssembleAll (ค่าเกณฑ์ใช้ Default ตามสเปก)
        // inputs = null -> ส่ง null เข้า Engine เพื่อตรวจ Guard (ArgumentNullException)
        private static List<SkuForecastResult> Assemble(IEnumerable<SkuPipelineCandidateInput>? inputs)
        {
            _runStart = DateTime.UtcNow;

            List<SkuModelRoutingState> states = null!;
            List<SkuCleansingCandidate> candidates = null!;
            string asOfPeriod = "";
            if (inputs != null)
            {
                List<SkuPipelineCandidateInput> list = inputs.ToList();
                states = list.Select(ToState).ToList();
                candidates = list.Select(ToCandidate).ToList();
                asOfPeriod = list.Count > 0 ? list[0].AsOfPeriod ?? "" : "";
            }

            List<SkuForecastResult> results = ForecastResultAssembler.AssembleAll(states, candidates, asOfPeriod, new AssemblerConfig());
            _runEnd = DateTime.UtcNow;
            return results;
        }

        // Engine ตั้งเวลาเองเป็น DateTime.UtcNow ตอนประกอบ จึงตรวจว่าอยู่ในช่วงที่รัน (ไม่เทียบกับค่าตายตัว)
        private static void AssertAuditTime(DateTime actual, string fieldName)
        {
            bool inWindow = actual >= _runStart && actual <= _runEnd && actual.Kind == DateTimeKind.Utc;
            TestContext.Record(fieldName, "UTC time within run window", inWindow ? "UTC time within run window" : actual.ToString("O"));
            if (!inWindow)
                throw new Exception($"Assertion Failed for [{fieldName}]: {actual:O} is not a UTC time within the run window");
        }

        // ================= TC-11B-01 ถึง TC-11B-08 =================

        private static void Test_TC11B_01_Normal2SigmaAndStep7Handoff()
        {
            var rawSales = new decimal[24] {
                68.0000m, 75.0000m, 82.0000m, 86.0000m, 140.0000m, 92.0000m, 95.0000m, 97.0000m, 99.0000m, 100.0000m, 100.0000m, 101.0000m,
                103.0000m, 105.0000m, 108.0000m, 112.0000m, 115.0000m, 119.0000m, 125.0000m, 132.0000m, 94.0000m, 98.0000m, 102.0000m, 106.0000m
            };
            var oosLost = new decimal[24] {
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 5.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m,
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m
            };
            var netDemand = new decimal[24] {
                68.0000m, 75.0000m, 82.0000m, 86.0000m, 145.0000m, 92.0000m, 95.0000m, 97.0000m, 99.0000m, 100.0000m, 100.0000m, 101.0000m,
                103.0000m, 105.0000m, 108.0000m, 112.0000m, 115.0000m, 119.0000m, 125.0000m, 132.0000m, 94.0000m, 98.0000m, 102.0000m, 106.0000m
            };
            var cleansedDemand = new decimal[24] {
                68.0000m, 75.0000m, 82.0000m, 86.0000m, 136.6087m, 92.0000m, 95.0000m, 97.0000m, 99.0000m, 100.0000m, 100.0000m, 101.0000m,
                103.0000m, 105.0000m, 108.0000m, 112.0000m, 115.0000m, 119.0000m, 125.0000m, 132.0000m, 94.0000m, 98.0000m, 102.0000m, 106.0000m
            };

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC01",
                AsOfPeriod = "202610",
                RawSalesQty24M = rawSales,
                OosLostQty24M = oosLost,
                NetDemandQty24M = netDemand,
                CleansedDemandQty24M = cleansedDemand,
                AssignedModelId = null,
                AssignedModelName = null,
                SelectionReason = null,
                IsShortCircuited = false
            };
            var results = Assemble(new[] { input });

            Assert.AreEqual(1, results.Count, "Results count");
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC01", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(2454.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(5.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(102.4583m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(17.0752m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.1667m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(2, res.SigmaUsed.Value, "9. sigma_used");
            Assert.AreEqual(24, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(24, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0000m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(0.4259m, res.RawSkewness.Value, 0.0001m, "13. raw_skewness");
            Assert.AreEqual(0.9839m, res.RawKurtosis.Value, 0.0001m, "14. raw_kurtosis");
            Assert.AreEqual(0.9391m, res.JbStatistic.Value, 0.0001m, "15. jb_statistic");
            Assert.AreEqual(0.6253m, res.JbPValue.Value, 0.0001m, "16. jb_p_value");
            Assert.AreEqual("Approx Normal", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("SIGMA", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("2SIGMA", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(68.3079m, res.Lower2Sigma.Value, 0.0001m, "20. lower_2sigma");
            Assert.AreEqual(136.6087m, res.Upper2Sigma.Value, 0.0001m, "21. upper_2sigma");
            Assert.AreEqual(51.2327m, res.Lower3Sigma.Value, 0.0001m, "22. lower_3sigma");
            Assert.AreEqual(153.6839m, res.Upper3Sigma.Value, 0.0001m, "23. upper_3sigma");
            Assert.AreEqual(94.7500m, res.Q1.Value, 0.0001m, "24. q1");
            Assert.AreEqual(109.0000m, res.Q3.Value, 0.0001m, "25. q3");
            Assert.AreEqual(14.2500m, res.Iqr.Value, 0.0001m, "26. iqr");
            Assert.AreEqual(73.3750m, res.Lower15Iqr.Value, 0.0001m, "27. lower_15iqr");
            Assert.AreEqual(130.3750m, res.Upper15Iqr.Value, 0.0001m, "28. upper_15iqr");
            Assert.AreEqual(52.0000m, res.Lower3Iqr.Value, 0.0001m, "29. lower_3iqr");
            Assert.AreEqual(151.7500m, res.Upper3Iqr.Value, 0.0001m, "30. upper_3iqr");
            Assert.AreEqual(2, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(1, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(1, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("CAP HIGH", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("AUTO CAP HIGH ONLY", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(136.6087m, res.SelectedUpperCap.Value, 0.0001m, "40. selected_upper_cap");
            Assert.AreEqual(0.4842m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.1603m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.4221m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(-0.1093m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.2291m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("AUTO 2SIGMA", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(1, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(24, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(24, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(102.1087m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(16.2313m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.1590m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0253m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.5332m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.2442m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual(null, res.DemandGroup, "58. demand_group");
            Assert.AreEqual("Smooth", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual(null, res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("X", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(0.1590m, res.CvEligibleXyz.Value, 0.0001m, "62. cv_eligible_xyz");
            Assert.AreEqual(0.0000m, res.CvPercentile.Value, 0.0001m, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("2SIGMA", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(8.3913m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual(null, res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.1583m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.4887m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(-0.1093m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(false, res.IsShortCircuited, "is_short_circuited");
            Assert.AreEqual("LOW WARNING >2SIGMA", res.OutlierStatus24M[0], "outlier_status_24m[0]");
            Assert.AreEqual("HIGH WARNING >2SIGMA", res.OutlierStatus24M[4], "outlier_status_24m[4]");
            for (int i = 0; i < 24; i++)
            {
                if (i != 0 && i != 4) Assert.AreEqual("NORMAL", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
            }
        }

        private static void Test_TC11B_02_RightSkewed3IqrAndExtremeOutlier()
        {
            var rawSales = new decimal[24] {
                45.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 280.0000m, 50.0000m, 52.0000m, 54.0000m, 56.0000m,
                48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m
            };
            var oosLost = new decimal[24] {
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m,
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m
            };
            var netDemand = new decimal[24] {
                45.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 280.0000m, 50.0000m, 52.0000m, 54.0000m, 56.0000m,
                48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m
            };
            var cleansedDemand = new decimal[24] {
                45.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 82.0000m, 50.0000m, 52.0000m, 54.0000m, 56.0000m,
                48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m, 48.0000m, 50.0000m, 52.0000m, 55.0000m, 58.0000m, 60.0000m
            };

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC02",
                AsOfPeriod = "202610",
                RawSalesQty24M = rawSales,
                OosLostQty24M = oosLost,
                NetDemandQty24M = netDemand,
                CleansedDemandQty24M = cleansedDemand,
                AssignedModelId = null,
                AssignedModelName = null,
                SelectionReason = null,
                IsShortCircuited = false
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC02", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(1506.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(62.7500m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(46.4714m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.7406m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(24, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(24, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0000m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(4.8312m, res.RawSkewness.Value, 0.0001m, "13. raw_skewness");
            Assert.AreEqual(23.5398m, res.RawKurtosis.Value, 0.0001m, "14. raw_kurtosis");
            Assert.AreEqual(430.5702m, res.JbStatistic.Value, 0.0001m, "15. jb_statistic");
            Assert.AreEqual(0.0000m, res.JbPValue.Value, 0.0001m, "16. jb_p_value");
            Assert.AreEqual("Right-Skewed", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("IQR", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("3.0IQR", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(0.0000m, res.Lower2Sigma.Value, 0.0001m, "20. lower_2sigma");
            Assert.AreEqual(155.6928m, res.Upper2Sigma.Value, 0.0001m, "21. upper_2sigma");
            Assert.AreEqual(0.0000m, res.Lower3Sigma.Value, 0.0001m, "22. lower_3sigma");
            Assert.AreEqual(202.1642m, res.Upper3Sigma.Value, 0.0001m, "23. upper_3sigma");
            Assert.AreEqual(50.0000m, res.Q1.Value, 0.0001m, "24. q1");
            Assert.AreEqual(58.0000m, res.Q3.Value, 0.0001m, "25. q3");
            Assert.AreEqual(8.0000m, res.Iqr.Value, 0.0001m, "26. iqr");
            Assert.AreEqual(38.0000m, res.Lower15Iqr.Value, 0.0001m, "27. lower_15iqr");
            Assert.AreEqual(70.0000m, res.Upper15Iqr.Value, 0.0001m, "28. upper_15iqr");
            Assert.AreEqual(26.0000m, res.Lower3Iqr.Value, 0.0001m, "29. lower_3iqr");
            Assert.AreEqual(82.0000m, res.Upper3Iqr.Value, 0.0001m, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(1, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(1, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("CAP HIGH", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("AUTO CAP HIGH ONLY", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(82.0000m, res.SelectedUpperCap.Value, 0.0001m, "40. selected_upper_cap");
            Assert.AreEqual(-0.1017m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(-0.2408m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.1641m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.4931m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(-0.3749m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("AUTO 3.0IQR", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(1, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(24, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(24, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(54.5000m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(7.2532m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.1331m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0177m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.1034m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(-0.2531m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual(null, res.DemandGroup, "58. demand_group");
            Assert.AreEqual("Smooth", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual(null, res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("X", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(0.1331m, res.CvEligibleXyz.Value, 0.0001m, "62. cv_eligible_xyz");
            Assert.AreEqual(0.0000m, res.CvPercentile.Value, 0.0001m, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("3.0IQR", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(198.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual(null, res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(-0.0339m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.3496m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.4931m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(false, res.IsShortCircuited, "is_short_circuited");
            Assert.AreEqual("EXTREME HIGH >3IQR", res.OutlierStatus24M[7], "outlier_status_24m[7]");
            for (int i = 0; i < 24; i++)
            {
                if (i != 7) Assert.AreEqual("NORMAL", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
            }
        }

        private static void Test_TC11B_03_ProtectGatePassThrough()
        {
            var series = new decimal[24] {
                40.0000m, 45.0000m, 50.0000m, 45.0000m, 55.0000m, 180.0000m, 210.0000m, 200.0000m, 170.0000m, 50.0000m, 45.0000m, 50.0000m,
                45.0000m, 50.0000m, 45.0000m, 55.0000m, 50.0000m, 190.0000m, 220.0000m, 200.0000m, 190.0000m, 60.0000m, 50.0000m, 45.0000m
            };
            var oosLost = new decimal[24];

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC03",
                AsOfPeriod = "202610",
                RawSalesQty24M = series,
                OosLostQty24M = oosLost,
                NetDemandQty24M = series,
                CleansedDemandQty24M = series,
                AssignedModelId = "1.1",
                AssignedModelName = "Seasonal TSD (Decomposition)",
                SelectionReason = "REPEATED PATTERN AFTER DETRENDING",
                IsShortCircuited = true
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC03", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(2340.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(97.5000m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(71.0939m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.7292m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(24, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(24, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0000m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(0.7964m, res.RawSkewness.Value, 0.0001m, "13. raw_skewness");
            Assert.AreEqual(-1.3970m, res.RawKurtosis.Value, 0.0001m, "14. raw_kurtosis");
            Assert.AreEqual(4.0810m, res.JbStatistic.Value, 0.0001m, "15. jb_statistic");
            Assert.AreEqual(0.1300m, res.JbPValue.Value, 0.0001m, "16. jb_p_value");
            Assert.AreEqual("Right-Skewed", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("IQR", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("KEEP", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(0.0000m, res.Lower2Sigma.Value, 0.0001m, "20. lower_2sigma");
            Assert.AreEqual(239.6878m, res.Upper2Sigma.Value, 0.0001m, "21. upper_2sigma");
            Assert.AreEqual(0.0000m, res.Lower3Sigma.Value, 0.0001m, "22. lower_3sigma");
            Assert.AreEqual(310.7817m, res.Upper3Sigma.Value, 0.0001m, "23. upper_3sigma");
            Assert.AreEqual(45.0000m, res.Q1.Value, 0.0001m, "24. q1");
            Assert.AreEqual(182.5000m, res.Q3.Value, 0.0001m, "25. q3");
            Assert.AreEqual(137.5000m, res.Iqr.Value, 0.0001m, "26. iqr");
            Assert.AreEqual(0.0000m, res.Lower15Iqr.Value, 0.0001m, "27. lower_15iqr");
            Assert.AreEqual(388.7500m, res.Upper15Iqr.Value, 0.0001m, "28. upper_15iqr");
            Assert.AreEqual(0.0000m, res.Lower3Iqr.Value, 0.0001m, "29. lower_3iqr");
            Assert.AreEqual(595.0000m, res.Upper3Iqr.Value, 0.0001m, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(0, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("KEEP", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("KEEP VALUE", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(null, res.SelectedUpperCap, "40. selected_upper_cap");
            Assert.AreEqual(0.1375m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.9955m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.2134m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.2137m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.9953m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("PROTECT TREND / SEASONAL PATTERN", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(0, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(24, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(24, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(97.5000m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(71.0939m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.7292m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.5317m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.1375m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.9953m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual("G1", res.DemandGroup, "58. demand_group");
            Assert.AreEqual("Erratic", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual("1.1", res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("X", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(0.7292m, res.CvEligibleXyz.Value, 0.0001m, "62. cv_eligible_xyz");
            Assert.AreEqual(0.0000m, res.CvPercentile.Value, 0.0001m, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("KEEP", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(0.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual("REPEATED PATTERN AFTER DETRENDING", res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.9955m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.2134m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.2137m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(true, res.IsShortCircuited, "is_short_circuited");
            for (int i = 0; i < 24; i++)
            {
                Assert.AreEqual("KEEP", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
            }
        }

        private static void Test_TC11B_04_ShortHistoryActiveWindow()
        {
            var series = new decimal[24] {
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m,
                45.0000m, 50.0000m, 55.0000m, 48.0000m, 52.0000m, 58.0000m, 47.0000m, 53.0000m, 50.0000m
            };
            var oosLost = new decimal[24];

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC04",
                AsOfPeriod = "202610",
                RawSalesQty24M = series,
                OosLostQty24M = oosLost,
                NetDemandQty24M = series,
                CleansedDemandQty24M = series,
                AssignedModelId = "1.4",
                AssignedModelName = "Simple Moving Average (SMA)",
                SelectionReason = "HISTORY <12 MONTHS: SMA",
                IsShortCircuited = true
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC04", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(458.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(50.8889m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(4.0757m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.0801m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(9, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(9, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0000m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(null, res.RawSkewness, "13. raw_skewness");
            Assert.AreEqual(null, res.RawKurtosis, "14. raw_kurtosis");
            Assert.AreEqual(null, res.JbStatistic, "15. jb_statistic");
            Assert.AreEqual(null, res.JbPValue, "16. jb_p_value");
            Assert.AreEqual("<12M - SMA", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("NONE", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("KEEP", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(42.7375m, res.Lower2Sigma.Value, 0.0001m, "20. lower_2sigma");
            Assert.AreEqual(59.0403m, res.Upper2Sigma.Value, 0.0001m, "21. upper_2sigma");
            Assert.AreEqual(38.6618m, res.Lower3Sigma.Value, 0.0001m, "22. lower_3sigma");
            Assert.AreEqual(63.1160m, res.Upper3Sigma.Value, 0.0001m, "23. upper_3sigma");
            Assert.AreEqual(48.0000m, res.Q1.Value, 0.0001m, "24. q1");
            Assert.AreEqual(53.0000m, res.Q3.Value, 0.0001m, "25. q3");
            Assert.AreEqual(5.0000m, res.Iqr.Value, 0.0001m, "26. iqr");
            Assert.AreEqual(40.5000m, res.Lower15Iqr.Value, 0.0001m, "27. lower_15iqr");
            Assert.AreEqual(60.5000m, res.Upper15Iqr.Value, 0.0001m, "28. upper_15iqr");
            Assert.AreEqual(33.0000m, res.Lower3Iqr.Value, 0.0001m, "29. lower_3iqr");
            Assert.AreEqual(68.0000m, res.Upper3Iqr.Value, 0.0001m, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(0, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("KEEP", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("KEEP VALUE", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(null, res.SelectedUpperCap, "40. selected_upper_cap");
            Assert.AreEqual(0.0000m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.0000m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.0000m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.7691m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.0000m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("HISTORY <12 MONTHS", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(0, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(9, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(9, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(50.8889m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(4.0757m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.0801m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0064m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.0000m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.0000m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual("G4", res.DemandGroup, "58. demand_group");
            Assert.AreEqual("NEW", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual("1.4", res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("NEW", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(null, res.CvEligibleXyz, "62. cv_eligible_xyz");
            Assert.AreEqual(null, res.CvPercentile, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("KEEP", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(0.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual("HISTORY <12 MONTHS: SMA", res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.0000m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.0000m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.7691m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(true, res.IsShortCircuited, "is_short_circuited");
            for (int i = 0; i < 15; i++) Assert.AreEqual("LEADING ZERO", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
            for (int i = 15; i < 24; i++) Assert.AreEqual("KEEP", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
        }

        private static void Test_TC11B_05_UltraShortHistoryActiveWindow()
        {
            var series = new decimal[24] {
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m,
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 40.0000m, 50.0000m, 60.0000m
            };
            var oosLost = new decimal[24];

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC05",
                AsOfPeriod = "202610",
                RawSalesQty24M = series,
                OosLostQty24M = oosLost,
                NetDemandQty24M = series,
                CleansedDemandQty24M = series,
                AssignedModelId = "1.4",
                AssignedModelName = "Simple Moving Average (SMA)",
                SelectionReason = "HISTORY <12 MONTHS: SMA",
                IsShortCircuited = true
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC05", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(150.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(50.0000m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(10.0000m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.2000m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(3, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(3, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0000m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(null, res.RawSkewness, "13. raw_skewness");
            Assert.AreEqual(null, res.RawKurtosis, "14. raw_kurtosis");
            Assert.AreEqual(null, res.JbStatistic, "15. jb_statistic");
            Assert.AreEqual(null, res.JbPValue, "16. jb_p_value");
            Assert.AreEqual("<12M - SMA", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("NONE", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("KEEP", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(null, res.Lower2Sigma, "20. lower_2sigma");
            Assert.AreEqual(null, res.Upper2Sigma, "21. upper_2sigma");
            Assert.AreEqual(null, res.Lower3Sigma, "22. lower_3sigma");
            Assert.AreEqual(null, res.Upper3Sigma, "23. upper_3sigma");
            Assert.AreEqual(null, res.Q1, "24. q1");
            Assert.AreEqual(null, res.Q3, "25. q3");
            Assert.AreEqual(null, res.Iqr, "26. iqr");
            Assert.AreEqual(null, res.Lower15Iqr, "27. lower_15iqr");
            Assert.AreEqual(null, res.Upper15Iqr, "28. upper_15iqr");
            Assert.AreEqual(null, res.Lower3Iqr, "29. lower_3iqr");
            Assert.AreEqual(null, res.Upper3Iqr, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(0, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("KEEP", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("KEEP VALUE", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(null, res.SelectedUpperCap, "40. selected_upper_cap");
            Assert.AreEqual(0.0000m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.0000m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.0000m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.7615m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.0000m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("HISTORY <12 MONTHS", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(0, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(3, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(3, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0000m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(50.0000m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(10.0000m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.2000m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0400m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.0000m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.0000m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual("G4", res.DemandGroup, "58. demand_group");
            Assert.AreEqual("NEW", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual("1.4", res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("NEW", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(null, res.CvEligibleXyz, "62. cv_eligible_xyz");
            Assert.AreEqual(null, res.CvPercentile, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("KEEP", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(0.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual("HISTORY <12 MONTHS: SMA", res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.0000m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.0000m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.7615m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(true, res.IsShortCircuited, "is_short_circuited");
            for (int i = 0; i < 21; i++) Assert.AreEqual("LEADING ZERO", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
            for (int i = 21; i < 24; i++) Assert.AreEqual("KEEP", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
        }

        private static void Test_TC11B_06_ZeroDemandEarlyExit()
        {
            var zeros = new decimal[24];
            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC06",
                AsOfPeriod = "202610",
                RawSalesQty24M = zeros,
                OosLostQty24M = zeros,
                NetDemandQty24M = zeros,
                CleansedDemandQty24M = zeros,
                AssignedModelId = "1.7",
                AssignedModelName = "Zero Demand (No Sales)",
                SelectionReason = "NO POSITIVE DEMAND IN 24 MONTHS",
                IsShortCircuited = true
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC06", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(0.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(0.0000m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(0.0000m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.0000m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(0, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(0, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(null, res.RawAdi, "12. raw_adi");
            Assert.AreEqual(null, res.RawSkewness, "13. raw_skewness");
            Assert.AreEqual(null, res.RawKurtosis, "14. raw_kurtosis");
            Assert.AreEqual(null, res.JbStatistic, "15. jb_statistic");
            Assert.AreEqual(null, res.JbPValue, "16. jb_p_value");
            Assert.AreEqual("No Sales", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("NONE", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("KEEP", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(null, res.Lower2Sigma, "20. lower_2sigma");
            Assert.AreEqual(null, res.Upper2Sigma, "21. upper_2sigma");
            Assert.AreEqual(null, res.Lower3Sigma, "22. lower_3sigma");
            Assert.AreEqual(null, res.Upper3Sigma, "23. upper_3sigma");
            Assert.AreEqual(null, res.Q1, "24. q1");
            Assert.AreEqual(null, res.Q3, "25. q3");
            Assert.AreEqual(null, res.Iqr, "26. iqr");
            Assert.AreEqual(null, res.Lower15Iqr, "27. lower_15iqr");
            Assert.AreEqual(null, res.Upper15Iqr, "28. upper_15iqr");
            Assert.AreEqual(null, res.Lower3Iqr, "29. lower_3iqr");
            Assert.AreEqual(null, res.Upper3Iqr, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(0, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("KEEP", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("KEEP VALUE", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("OK", res.SourceCheck, "39. source_check");
            Assert.AreEqual(null, res.SelectedUpperCap, "40. selected_upper_cap");
            Assert.AreEqual(0.0000m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.0000m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(0.0000m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.0000m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.0000m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(0, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("NO POSITIVE DEMAND", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(0, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(0, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(0, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(null, res.CleanAdi, "51. clean_adi");
            Assert.AreEqual(0.0000m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(0.0000m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.0000m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0000m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.0000m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.0000m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual("No Sales", res.DemandGroup, "58. demand_group");
            Assert.AreEqual("No Sales", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual("1.7", res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("Excluded", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(null, res.CvEligibleXyz, "62. cv_eligible_xyz");
            Assert.AreEqual(null, res.CvPercentile, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("KEEP", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(0.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual("NO POSITIVE DEMAND IN 24 MONTHS", res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.0000m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(0.0000m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.0000m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(true, res.IsShortCircuited, "is_short_circuited");
            for (int i = 0; i < 24; i++) Assert.AreEqual("NO SALES", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");
        }

        private static void Test_TC11B_07_MultiSkuWarehouseXyzPercentile()
        {
            var zeros = new decimal[24];
            var skuA = new decimal[24] {
                95.0000m, 105.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m,
                100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m
            };
            var skuB = new decimal[24] {
                90.0000m, 110.0000m, 85.0000m, 115.0000m, 95.0000m, 105.0000m, 90.0000m, 110.0000m, 85.0000m, 115.0000m, 95.0000m, 105.0000m,
                90.0000m, 110.0000m, 85.0000m, 115.0000m, 95.0000m, 105.0000m, 90.0000m, 110.0000m, 85.0000m, 115.0000m, 95.0000m, 105.0000m
            };
            var skuC = new decimal[24] {
                70.0000m, 130.0000m, 80.0000m, 120.0000m, 75.0000m, 125.0000m, 70.0000m, 130.0000m, 80.0000m, 120.0000m, 75.0000m, 125.0000m,
                70.0000m, 130.0000m, 80.0000m, 120.0000m, 75.0000m, 125.0000m, 70.0000m, 130.0000m, 80.0000m, 120.0000m, 75.0000m, 125.0000m
            };
            var skuD = new decimal[24] {
                20.0000m, 80.0000m, 30.0000m, 70.0000m, 25.0000m, 75.0000m, 20.0000m, 80.0000m, 30.0000m, 70.0000m, 25.0000m, 75.0000m,
                20.0000m, 80.0000m, 30.0000m, 70.0000m, 25.0000m, 75.0000m, 20.0000m, 80.0000m, 30.0000m, 70.0000m, 25.0000m, 75.0000m
            };
            var skuE = new decimal[24] {
                10.0000m, 110.0000m, 15.0000m, 105.0000m, 10.0000m, 110.0000m, 15.0000m, 105.0000m, 10.0000m, 110.0000m, 15.0000m, 105.0000m,
                10.0000m, 110.0000m, 15.0000m, 105.0000m, 10.0000m, 110.0000m, 15.0000m, 105.0000m, 10.0000m, 110.0000m, 15.0000m, 105.0000m
            };
            var skuF = new decimal[24] {
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m,
                0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 0.0000m, 50.0000m, 50.0000m, 50.0000m, 50.0000m, 50.0000m, 50.0000m
            };

            // Pass in intentionally unsorted order to verify Ordinal sorting and relative row assignment (1..6)
            var inputs = new[]
            {
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-F", AsOfPeriod = "202610", RawSalesQty24M = skuF, OosLostQty24M = zeros, NetDemandQty24M = skuF, CleansedDemandQty24M = skuF, AssignedModelId = "1.4", SelectionReason = "HISTORY <12 MONTHS: SMA", IsShortCircuited = true },
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-C", AsOfPeriod = "202610", RawSalesQty24M = skuC, OosLostQty24M = zeros, NetDemandQty24M = skuC, CleansedDemandQty24M = skuC },
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-A", AsOfPeriod = "202610", RawSalesQty24M = skuA, OosLostQty24M = zeros, NetDemandQty24M = skuA, CleansedDemandQty24M = skuA },
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-E", AsOfPeriod = "202610", RawSalesQty24M = skuE, OosLostQty24M = zeros, NetDemandQty24M = skuE, CleansedDemandQty24M = skuE },
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-B", AsOfPeriod = "202610", RawSalesQty24M = skuB, OosLostQty24M = zeros, NetDemandQty24M = skuB, CleansedDemandQty24M = skuB },
                new SkuPipelineCandidateInput { WarehouseCode = "MDC (B2B)", ItemId = "SKU-D", AsOfPeriod = "202610", RawSalesQty24M = skuD, OosLostQty24M = zeros, NetDemandQty24M = skuD, CleansedDemandQty24M = skuD }
            };
            var results = Assemble(inputs);

            Assert.AreEqual(6, results.Count, "Multi-SKU batch count");

            // SKU-A (Row 1, Percentile 0.0000, X)
            Assert.AreEqual("SKU-A", results[0].ItemId, "Row 1 item_id");
            Assert.AreEqual(1, results[0].SalesRow, "SKU-A sales_row");
            Assert.AreEqual(1, results[0].OosRow, "SKU-A oos_row");
            Assert.AreEqual(1, results[0].RawRowRef, "SKU-A raw_row_ref");
            Assert.AreEqual(0.0147m, results[0].CvEligibleXyz.Value, 0.0001m, "SKU-A cv_eligible_xyz");
            Assert.AreEqual(0.0000m, results[0].CvPercentile.Value, 0.0001m, "SKU-A cv_percentile");
            Assert.AreEqual("X", results[0].XyzClass, "SKU-A xyz_class");

            // SKU-B (Row 2, Percentile 0.2500, Y)
            Assert.AreEqual("SKU-B", results[1].ItemId, "Row 2 item_id");
            Assert.AreEqual(2, results[1].SalesRow, "SKU-B sales_row");
            Assert.AreEqual(0.1103m, results[1].CvEligibleXyz.Value, 0.0001m, "SKU-B cv_eligible_xyz");
            Assert.AreEqual(0.2500m, results[1].CvPercentile.Value, 0.0001m, "SKU-B cv_percentile");
            Assert.AreEqual("Y", results[1].XyzClass, "SKU-B xyz_class");

            // SKU-C (Row 3, Percentile 0.5000, Y)
            Assert.AreEqual("SKU-C", results[2].ItemId, "Row 3 item_id");
            Assert.AreEqual(3, results[2].SalesRow, "SKU-C sales_row");
            Assert.AreEqual(0.2588m, results[2].CvEligibleXyz.Value, 0.0001m, "SKU-C cv_eligible_xyz");
            Assert.AreEqual(0.5000m, results[2].CvPercentile.Value, 0.0001m, "SKU-C cv_percentile");
            Assert.AreEqual("Y", results[2].XyzClass, "SKU-C xyz_class");

            // SKU-D (Row 4, Percentile 0.7500, Z)
            Assert.AreEqual("SKU-D", results[3].ItemId, "Row 4 item_id");
            Assert.AreEqual(4, results[3].SalesRow, "SKU-D sales_row");
            Assert.AreEqual(0.5175m, results[3].CvEligibleXyz.Value, 0.0001m, "SKU-D cv_eligible_xyz");
            Assert.AreEqual(0.7500m, results[3].CvPercentile.Value, 0.0001m, "SKU-D cv_percentile");
            Assert.AreEqual("Z", results[3].XyzClass, "SKU-D xyz_class");

            // SKU-E (Row 5, Percentile 1.0000, Z)
            Assert.AreEqual("SKU-E", results[4].ItemId, "Row 5 item_id");
            Assert.AreEqual(5, results[4].SalesRow, "SKU-E sales_row");
            Assert.AreEqual(0.8098m, results[4].CvEligibleXyz.Value, 0.0001m, "SKU-E cv_eligible_xyz");
            Assert.AreEqual(1.0000m, results[4].CvPercentile.Value, 0.0001m, "SKU-E cv_percentile");
            Assert.AreEqual("Z", results[4].XyzClass, "SKU-E xyz_class");

            // SKU-F (Row 6, Ineligible NEW, CvPercentile = null, XyzClass = NEW)
            Assert.AreEqual("SKU-F", results[5].ItemId, "Row 6 item_id");
            Assert.AreEqual(6, results[5].SalesRow, "SKU-F sales_row");
            Assert.AreEqual(null, results[5].CvEligibleXyz, "SKU-F cv_eligible_xyz");
            Assert.AreEqual(null, results[5].CvPercentile, "SKU-F cv_percentile");
            Assert.AreEqual("NEW", results[5].XyzClass, "SKU-F xyz_class");
        }

        private static void Test_TC11B_08_SourceCheckAndInboundValidationGuards()
        {
            // Sub-case A: Negative value in source
            var rawSales = new decimal[24] {
                100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, -20.0000m,
                100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m
            };
            var oosLost = new decimal[24];
            var netDemand = new decimal[24] {
                100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 0.0000m,
                100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m, 100.0000m
            };

            var input = new SkuPipelineCandidateInput
            {
                WarehouseCode = "MDC (B2B)",
                ItemId = "SKU-TC08",
                AsOfPeriod = "202610",
                RawSalesQty24M = rawSales,
                OosLostQty24M = oosLost,
                NetDemandQty24M = netDemand,
                CleansedDemandQty24M = netDemand,
                AssignedModelId = null,
                AssignedModelName = null,
                SelectionReason = null,
                IsShortCircuited = false
            };
            var results = Assemble(new[] { input });
            var res = results[0];

            Assert.AreEqual("MDC (B2B)", res.WarehouseCode, "1. warehouse_code");
            Assert.AreEqual("SKU-TC08", res.ItemId, "2. item_id");
            Assert.AreEqual("202610", res.AsOfPeriod, "3. as_of_period");
            Assert.AreEqual(2280.0000m, res.RawTotal24M, 0.0001m, "4. raw_total_24m");
            Assert.AreEqual(0.0000m, res.OosTotal24M, 0.0001m, "5. oos_total_24m");
            Assert.AreEqual(95.8333m, res.RawAvg, 0.0001m, "6. raw_avg");
            Assert.AreEqual(20.4124m, res.RawSd, 0.0001m, "7. raw_sd");
            Assert.AreEqual(0.2130m, res.RawCv, 0.0001m, "8. raw_cv");
            Assert.AreEqual(null, res.SigmaUsed, "9. sigma_used");
            Assert.AreEqual(24, res.RawTtlMth, "10. raw_ttl_mth");
            Assert.AreEqual(23, res.RawActiveMth, "11. raw_active_mth");
            Assert.AreEqual(1.0435m, res.RawAdi.Value, 0.0001m, "12. raw_adi");
            Assert.AreEqual(-4.8990m, res.RawSkewness.Value, 0.0001m, "13. raw_skewness");
            Assert.AreEqual(24.0000m, res.RawKurtosis.Value, 0.0001m, "14. raw_kurtosis");
            Assert.AreEqual(446.8294m, res.JbStatistic.Value, 0.0001m, "15. jb_statistic");
            Assert.AreEqual(0.0000m, res.JbPValue.Value, 0.0001m, "16. jb_p_value");
            Assert.AreEqual("Left-Skewed", res.DemandDistribution, "17. demand_distribution");
            Assert.AreEqual("IQR", res.OutlierMethod, "18. outlier_method");
            Assert.AreEqual("DATA ERROR", res.SelectedOutlierTool, "19. selected_outlier_tool");
            Assert.AreEqual(55.0085m, res.Lower2Sigma.Value, 0.0001m, "20. lower_2sigma");
            Assert.AreEqual(136.6581m, res.Upper2Sigma.Value, 0.0001m, "21. upper_2sigma");
            Assert.AreEqual(34.5961m, res.Lower3Sigma.Value, 0.0001m, "22. lower_3sigma");
            Assert.AreEqual(157.0705m, res.Upper3Sigma.Value, 0.0001m, "23. upper_3sigma");
            Assert.AreEqual(100.0000m, res.Q1.Value, 0.0001m, "24. q1");
            Assert.AreEqual(100.0000m, res.Q3.Value, 0.0001m, "25. q3");
            Assert.AreEqual(0.0000m, res.Iqr.Value, 0.0001m, "26. iqr");
            Assert.AreEqual(null, res.Lower15Iqr, "27. lower_15iqr");
            Assert.AreEqual(null, res.Upper15Iqr, "28. upper_15iqr");
            Assert.AreEqual(null, res.Lower3Iqr, "29. lower_3iqr");
            Assert.AreEqual(null, res.Upper3Iqr, "30. upper_3iqr");
            Assert.AreEqual(0, res.WarningCount, "31. warning_count");
            Assert.AreEqual(0, res.ExtremeCount, "32. extreme_count");
            Assert.AreEqual(0, res.HighOutlierCount, "33. high_outlier_count");
            Assert.AreEqual(0, res.LowOutlierCount, "34. low_outlier_count");
            Assert.AreEqual("DATA ERROR", res.OutlierReviewStatus, "35. outlier_review_status");
            Assert.AreEqual("FIX SOURCE", res.Action, "36. action");
            Assert.AreEqual(1, res.SalesRow, "37. sales_row");
            Assert.AreEqual(1, res.OosRow, "38. oos_row");
            Assert.AreEqual("NEGATIVE VALUE IN SOURCE", res.SourceCheck, "39. source_check");
            Assert.AreEqual(null, res.SelectedUpperCap, "40. selected_upper_cap");
            Assert.AreEqual(0.0151m, res.RawTrendR.Value, 0.0001m, "41. raw_trend_r");
            Assert.AreEqual(0.0000m, res.RawLag12R.Value, 0.0001m, "42. raw_lag12_r");
            Assert.AreEqual(-0.4804m, res.RawYear1TrendR.Value, 0.0001m, "43. raw_year1_trend_r");
            Assert.AreEqual(0.0000m, res.RawYear2TrendR.Value, 0.0001m, "44. raw_year2_trend_r");
            Assert.AreEqual(0.0000m, res.RawDetrendedLag12R.Value, 0.0001m, "45. raw_detrended_lag12_r");
            Assert.AreEqual(1, res.NegativeMonthsCount, "46. negative_months_count");
            Assert.AreEqual("NEGATIVE VALUE IN SOURCE", res.OutlierSelectionReason, "47. outlier_selection_reason");
            Assert.AreEqual(0, res.CappedHighMonthsCount, "48. capped_high_months_count");
            Assert.AreEqual(24, res.CleanTtlMth, "49. clean_ttl_mth");
            Assert.AreEqual(23, res.CleanActiveMth, "50. clean_active_mth");
            Assert.AreEqual(1.0435m, res.CleanAdi.Value, 0.0001m, "51. clean_adi");
            Assert.AreEqual(95.8333m, res.CleanMean, 0.0001m, "52. clean_mean");
            Assert.AreEqual(20.4124m, res.CleanSd, 0.0001m, "53. clean_sd");
            Assert.AreEqual(0.2130m, res.CleanCv, 0.0001m, "54. clean_cv");
            Assert.AreEqual(0.0000m, res.CleanCv2, 0.0001m, "55. clean_cv2");
            Assert.AreEqual(0.0151m, res.DetectTrend.Value, 0.0001m, "56. detect_trend");
            Assert.AreEqual(0.0000m, res.CleanDetrendedSeasonalR.Value, 0.0001m, "57. clean_detrended_seasonal_r");
            Assert.AreEqual(null, res.DemandGroup, "58. demand_group");
            Assert.AreEqual("Smooth", res.DemandPattern, "59. demand_pattern");
            Assert.AreEqual(null, res.AssignedModelId, "60. assigned_model_id");
            Assert.AreEqual("X", res.XyzClass, "61. xyz_class");
            Assert.AreEqual(0.2130m, res.CvEligibleXyz.Value, 0.0001m, "62. cv_eligible_xyz");
            Assert.AreEqual(0.0000m, res.CvPercentile.Value, 0.0001m, "63. cv_percentile");
            Assert.AreEqual(1, res.RawRowRef, "64. raw_row_ref");
            Assert.AreEqual("KEEP", res.AppliedOutlierTool, "65. applied_outlier_tool");
            Assert.AreEqual(0.0000m, res.QtyReducedByCap, 0.0001m, "66. qty_reduced_by_cap");
            Assert.AreEqual(null, res.SelectionReason, "67. selection_reason");
            Assert.AreEqual(0.0000m, res.CleanLag12R.Value, 0.0001m, "68. clean_lag12_r");
            Assert.AreEqual(-0.4804m, res.CleanYear1TrendR.Value, 0.0001m, "69. clean_year1_trend_r");
            Assert.AreEqual(0.0000m, res.CleanYear2TrendR.Value, 0.0001m, "70. clean_year2_trend_r");
            AssertAuditTime(res.CalculatedAt, "71. calculated_at");
            AssertAuditTime(res.CreatedAt, "72. created_at");
            Assert.AreEqual(false, res.IsShortCircuited, "is_short_circuited");
            for (int i = 0; i < 24; i++) Assert.AreEqual("DATA ERROR", res.OutlierStatus24M[i], "outlier_status_24m[" + i + "]");

            // Sub-case B: Inbound Validation Guards
            bool threwNullCollection = false;
            try { Assemble(null); }
            catch (ArgumentNullException) { threwNullCollection = true; }
            Assert.IsTrue(threwNullCollection, "Expected ArgumentNullException when candidateStates is null");

            bool threwShortArray = false;
            try
            {
                var badInput = new SkuPipelineCandidateInput
                {
                    WarehouseCode = "MDC (B2B)",
                    ItemId = "SKU-BAD",
                    RawSalesQty24M = new decimal[23],
                    OosLostQty24M = new decimal[24],
                    NetDemandQty24M = new decimal[24],
                    CleansedDemandQty24M = new decimal[24]
                };
                Assemble(new[] { badInput });
            }
            catch (ArgumentException) { threwShortArray = true; }
            Assert.IsTrue(threwShortArray, "Expected ArgumentException when array length != 24");
        }
    }
}