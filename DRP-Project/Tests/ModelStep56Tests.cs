using DRP_Project.src.Cleansing;
using DRP_Project.src.ModelRouting;

namespace DRP_Project.Tests
{
	// Unit Test ของ ModelEngine (PBI-11 Step 5 & Step 6) ตรงตาม Jira PBI-11 (TC-11-01 ถึง TC-11-11)
	// ตรวจตรรกะคัดกรองสินค้าตามฤดูกาล (1.1 Seasonal TSD) และสินค้าแนวโน้มชัดเจน (1.2 Trend DES)
	// รันด้วยแฟล็ก --test-model-56 (ไม่ต่อฐานข้อมูล)
	class ModelStep56Tests
	{
		// ข้อความโมเดลตามข้อกำหนด PBI-11
		private const string SeasonalName = "Seasonal TSD (Decomposition)";
		private const string SeasonalReason = "Full 24M history with strong seasonal pattern (Lag-12 r >= 0.5)";
		private const string TrendName = "Trend DES (Holt Linear)";
		private const string TrendReason = "Significant growth/decline sales trend (|Trend r| >= 0.5)";

		// ข้อความโมเดลที่ PBI-09 / PBI-10 ติดป้ายไว้แล้ว (ใช้ใน TC-11-10)
		private const string NoSalesName = "No Sales";
		private const string NoSalesReason = "No sales in the past 24 months (Active Months = 0)";
		private const string L3MName = "No Sales Last 3 Months (No Sales L3M)";
		private const string L3MReason = "No sales in the last 3 consecutive months";
		private const string SmaName = "Simple Moving Average (<12M SMA)";
		private const string SmaReason = "Sales history is less than 12 months";
		private const string CrostonName = "Croston Method";
		private const string CrostonReason = "Intermittent demand pattern with ADI > 1.32";

		// เกณฑ์ M_LOV ค่ามาตรฐาน (SEASONAL_CORRELATION / TREND_CORRELATION)
		private const decimal DefaultSeasonal = 0.500000m;
		private const decimal DefaultTrend = 0.500000m;

		// โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-11-01 ถึง TC-11-09)
		private record TestCase(
			string Id,                      // ตำแหน่งที่ 1
			string Description,             // ตำแหน่งที่ 2
			decimal[] CleansedDemand24M,    // ตำแหน่งที่ 3  <- Input (ยอดขายสะอาด 24 งวด Index 0..23)
			int HistoryMonths,              // ตำแหน่งที่ 4  <- Input (Metadata FM-03)
			int CleanActiveMonths,          // ตำแหน่งที่ 5  <- Input (Metadata FM-04)
			decimal CleanSeasonalR,         // ตำแหน่งที่ 6  <- Input (Metadata FM-09)
			decimal CleanTrendR,            // ตำแหน่งที่ 7  <- Input (Metadata FM-08)
			string? ExpectedModelId,        // ตำแหน่งที่ 8  <- ผลที่ควรได้ ("1.1" / "1.2" / null = ส่งต่อ PBI-11B)
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

			Console.WriteLine("===== Unit Test: ModelEngine (PBI-11 Step 5 & 6) =====\n");

			// 1. ตรรกะเลือกโมเดลหลัก TC-11-01 ถึง TC-11-09
			RunMainRoutingTests();

			// 2. Pass-Through จาก Step 1-4 (TC-11-10) และ Fallback ค่าเกณฑ์ M_LOV (TC-11-11)
			RunPassThroughAndFallbackTests();

			// 3. Guard Clause ตาม Inbound Contract (นอกเหนือจาก TC-11-01..11)
			RunGuardTests();

			Console.WriteLine("=========================================");
			Console.WriteLine($"สรุปผล: PASS {_passCount} / FAIL {_failCount} (ทั้งหมด {_passCount + _failCount} เคส)");
			Console.WriteLine("=========================================");
		}

		// ---------- TC-11-01 ถึง TC-11-09: ตรรกะเลือกโมเดล (ตรวจ 4 ฟิลด์ Routing) ----------
		private static void RunMainRoutingTests()
		{
			List<TestCase> cases = new()
			{
				new("TC-11-01", "Standard Seasonal Model (1.1 Seasonal TSD)",
					D(40, 45, 50, 45, 55, 180, 210, 200, 170, 50, 45, 50, 45, 50, 45, 55, 50, 190, 220, 200, 190, 60, 50, 45),
					24, 24, 0.9953m, 0.1375m,
					"1.1", SeasonalName, SeasonalReason, true),

				new("TC-11-02", "Seasonal Exact Threshold Boundary (CleanSeasonalR = 0.5000)",
					D(80, 90, 100, 100, 110, 120, 120, 110, 100, 100, 90, 80, 80, 110, 90, 100, 120, 100, 100, 120, 100, 90, 110, 80),
					24, 24, 0.5000m, 0.0000m,
					"1.1", SeasonalName, SeasonalReason, true),

				new("TC-11-03", "Seasonal Below Threshold Boundary (CleanSeasonalR < 0.5 -> ส่งต่อ PBI-11B)",
					D(95, 105, 92, 108, 98, 102, 106, 94, 101, 99, 93, 107, 98, 104, 96, 102, 105, 93, 101, 99, 94, 106, 97, 103),
					24, 24, 0.3525m, 0.0112m,
					null, null, null, false),

				new("TC-11-04", "Strict Seasonal Priority over Trend (Step 5 over Step 6 Conflict Case)",
					D(30, 35, 40, 45, 50, 120, 140, 130, 110, 60, 65, 70, 90, 95, 100, 105, 110, 200, 230, 215, 190, 120, 125, 130),
					24, 24, 0.9995m, 0.6974m,
					"1.1", SeasonalName, SeasonalReason, true),

				new("TC-11-05", "Upward Growth Trend Model (1.2 Trend DES)",
					D(20, 35, 30, 45, 55, 50, 70, 65, 80, 95, 90, 105, 115, 130, 125, 145, 140, 160, 155, 175, 170, 185, 200, 195),
					24, 24, -0.2936m, 0.9944m,
					"1.2", TrendName, TrendReason, true),

				new("TC-11-06", "Downward Decline Trend Model (1.2 Trend DES)",
					D(200, 185, 190, 175, 165, 170, 150, 155, 140, 125, 130, 115, 105, 90, 95, 75, 80, 60, 65, 45, 50, 35, 20, 25),
					24, 24, -0.2936m, -0.9944m,
					"1.2", TrendName, TrendReason, true),

				new("TC-11-07", "Trend Exact Threshold Boundary (|CleanTrendR| = 0.5000)",
					D(117, 39, 106, 58, 110, 62, 109, 71, 103, 85, 107, 89, 91, 113, 95, 117, 89, 131, 88, 140, 92, 144, 81, 163),
					24, 24, -0.6609m, 0.5000m,
					"1.2", TrendName, TrendReason, true),

				new("TC-11-08", "Trend Below Threshold Boundary (|CleanTrendR| < 0.5 -> ส่งต่อ PBI-11B)",
					D(117, 39, 106, 58, 110, 62, 109, 71, 103, 85, 107, 89, 91, 113, 95, 117, 89, 131, 88, 140, 92, 144, 81, 160),
					24, 24, -0.6688m, 0.4975m,
					null, null, null, false),

				new("TC-11-09", "Strict 24M History Guard for Seasonality (HistoryMonths = 23 < 24)",
					D(0, 45, 50, 45, 55, 180, 210, 200, 170, 50, 45, 50, 45, 50, 45, 55, 50, 190, 220, 200, 190, 60, 50, 45),
					23, 23, 0.0000m, 0.0842m,
					null, null, null, false),
			};

			foreach (TestCase tc in cases)
			{
				// สร้าง State จำลองที่ PBI-10 ส่งต่อมา (ยังไม่ถูกตัดจบ) แล้วเรียก Engine ผ่านทางเข้าจริงที่ Pipeline ใช้
				SkuModelRoutingState state = CreateTestState(tc.Id, tc.CleansedDemand24M, tc.HistoryMonths, tc.CleanActiveMonths, tc.CleanSeasonalR, tc.CleanTrendR);
				SkuModelRoutingState result = RunModel1112(new List<SkuModelRoutingState> { state }, DefaultSeasonal, DefaultTrend)[0];

				// เทียบผลลัพธ์ทีละฟิลด์ + ตรวจว่า Metadata ในตาราง Jira สะท้อนเวกเตอร์ยอดขายจริง (กันพิมพ์เลขผิด)
				bool metadataMatchesVector = MetadataMatchesVector(tc.CleansedDemand24M, tc.HistoryMonths, tc.CleanActiveMonths, tc.CleanSeasonalR, tc.CleanTrendR);
				bool isPass = HasRouting(result, tc.ExpectedModelId, tc.ExpectedModelName, tc.ExpectedReason, tc.ExpectedShortCircuited) &&
							  metadataMatchesVector;

				RecordResult(isPass, tc.Id, tc.Description,
					$"demand24M=[{string.Join(", ", tc.CleansedDemand24M)}], history={tc.HistoryMonths}, activeMonths={tc.CleanActiveMonths}, seasonalR={tc.CleanSeasonalR}, trendR={tc.CleanTrendR}, thresholds={DefaultSeasonal}/{DefaultTrend}",
					$"id={tc.ExpectedModelId ?? "null"}, name={tc.ExpectedModelName ?? "null"}, reason={tc.ExpectedReason ?? "null"}, shortCircuited={tc.ExpectedShortCircuited}, metadataMatchesVector=True",
					$"id={result.AssignedModelId ?? "null"}, name={result.AssignedModelName ?? "null"}, reason={result.SelectionReason ?? "null"}, shortCircuited={result.IsShortCircuited}, metadataMatchesVector={metadataMatchesVector}");
			}
		}

		// ---------- TC-11-10 และ TC-11-11: เคสพิเศษ (ตรวจต่างจากตารางหลัก) ----------
		private static void RunPassThroughAndFallbackTests()
		{
			// ===== TC-11-10: Short-Circuit Pass-Through from Step 1 to 4 =====
			// SKU A (1.7), B (1.6), C (1.4), D (1.5) ถูกติดป้ายแล้ว ต้องคงค่าเดิม 100% ไม่ถูกประเมินซ้ำและยังอยู่ในผลลัพธ์ครบ
			var skuA = CreateTestState("TC-11-10-A", D(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 0, 0, 0.0000m, 0.0000m);
			Assign(skuA, "1.7", NoSalesName, NoSalesReason);

			var skuB = CreateTestState("TC-11-10-B", D(50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 0, 0, 0), 24, 21, 0.0000m, 0.0000m);
			Assign(skuB, "1.6", L3MName, L3MReason);

			var skuC = CreateTestState("TC-11-10-C", D(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 50, 50, 50, 50, 50, 50, 50, 50, 50), 9, 9, 0.0000m, 0.0000m);
			Assign(skuC, "1.4", SmaName, SmaReason);

			var skuD = CreateTestState("TC-11-10-D", D(40, 0, 0, 50, 0, 0, 40, 0, 0, 60, 0, 0, 45, 0, 0, 55, 0, 0, 40, 0, 0, 50, 0, 0), 24, 8, 0.0000m, 0.0000m);
			skuD.CleanAdi = 3.0000m;
			Assign(skuD, "1.5", CrostonName, CrostonReason);

			// ตั้งค่า r สูงให้ทุก SKU: ถ้า Engine เผลอประเมินซ้ำ จะกลายเป็น 1.1 -> ต้องคงค่าเดิม
			foreach (var s in new[] { skuA, skuB, skuC, skuD })
			{
				s.CleanSeasonalR = 0.9000m;
				s.CleanTrendR = 0.9000m;
			}

			var batch = new List<SkuModelRoutingState> { skuA, skuB, skuC, skuD };
			List<SkuModelRoutingState> passThrough = RunModel1112(batch, DefaultSeasonal, DefaultTrend);

			bool passThroughPass = passThrough.Count == 4 &&
								   HasRouting(passThrough[0], "1.7", NoSalesName, NoSalesReason, true) &&
								   HasRouting(passThrough[1], "1.6", L3MName, L3MReason, true) &&
								   HasRouting(passThrough[2], "1.4", SmaName, SmaReason, true) &&
								   HasRouting(passThrough[3], "1.5", CrostonName, CrostonReason, true) &&
								   passThrough[0].HistoryMonths == 0 && passThrough[0].CleanActiveMonths == 0 &&
								   passThrough[1].HistoryMonths == 24 && passThrough[1].CleanActiveMonths == 21 &&
								   passThrough[2].HistoryMonths == 9 && passThrough[2].CleanActiveMonths == 9 &&
								   passThrough[3].HistoryMonths == 24 && passThrough[3].CleanActiveMonths == 8 && passThrough[3].CleanAdi == 3.0000m &&
								   MetadataMatchesVector(skuC.CleansedDemand24M, 9, 9) &&
								   MetadataMatchesVector(skuD.CleansedDemand24M, 24, 8);

			RecordResult(passThroughPass, "TC-11-10", "Short-Circuit Pass-Through from Step 1 to 4 (Preservation of Prior Decisions)",
				"SKU A ได้ 1.7 | B ได้ 1.6 | C ได้ 1.4 (history=9) | D ได้ 1.5 (active=8, adi=3.0000) โดยทุก SKU มี r = 0.9 (จะเข้า 1.1 ถ้าถูกประเมินซ้ำ)",
				"Count=4 ตามลำดับ [1.7, 1.6, 1.4, 1.5] | shortCircuited=true ทั้งหมด | Name/Reason/Metadata ไม่เปลี่ยน (ไม่ถูกประเมินซ้ำ)",
				$"Count={passThrough.Count} | " + string.Join(" | ", passThrough.Select(s =>
					$"{s.ItemId}: id={s.AssignedModelId ?? "null"}, name={s.AssignedModelName ?? "null"}, shortCircuited={s.IsShortCircuited}, history={s.HistoryMonths}, active={s.CleanActiveMonths}")));

			// ===== TC-11-11: M_LOV Configuration Fallback Guard =====
			// M_LOV ไม่พบพารามิเตอร์ -> Pipeline ส่งค่า Default ให้ Engine (การอ่านจาก DB อยู่ใน LoadLovDecimal ซึ่ง Unit Test นี้ไม่ต่อฐานข้อมูล)
			// จึงตรวจ 2 อย่าง: (1) ค่า Default ที่ Pipeline ใช้ = 0.500000 ทั้งคู่ (2) Engine ใช้ค่านี้ตัดสินได้ถูกต้องโดยไม่โยน Exception
			decimal fallbackSeasonal = ModelEngine.DefaultSeasonalThreshold;
			decimal fallbackTrend = ModelEngine.DefaultTrendThreshold;
			bool defaultOk = fallbackSeasonal == 0.500000m && fallbackTrend == 0.500000m;

			decimal[] fallbackDemand = D(40, 45, 50, 45, 55, 180, 210, 200, 170, 50, 45, 50, 45, 50, 45, 55, 50, 190, 220, 200, 190, 60, 50, 45);
			var fallbackState = CreateTestState("TC-11-11", fallbackDemand, 24, 24, 0.9953m, 0.1375m);

			bool noException = true;
			SkuModelRoutingState? fallbackResult = null;
			try
			{
				fallbackResult = RunModel1112(new List<SkuModelRoutingState> { fallbackState }, fallbackSeasonal, fallbackTrend)[0];
			}
			catch (Exception)
			{
				noException = false;
			}

			bool fallbackPass = defaultOk && noException && fallbackResult != null &&
								HasRouting(fallbackResult, "1.1", SeasonalName, SeasonalReason, true) &&
								MetadataMatchesVector(fallbackDemand, 24, 24, 0.9953m, 0.1375m);

			RecordResult(fallbackPass, "TC-11-11", "M_LOV Configuration Fallback Guard (Graceful Default Handling)",
				$"demand24M=[{string.Join(", ", fallbackDemand)}], seasonalR=0.9953, trendR=0.1375, M_LOV ไม่พบพารามิเตอร์ -> ใช้ค่า Default",
				"defaultSeasonal=0.500000, defaultTrend=0.500000, noException=True, id=1.1, name=Seasonal TSD (Decomposition), shortCircuited=true",
				$"defaultSeasonal={fallbackSeasonal}, defaultTrend={fallbackTrend}, noException={noException}, id={fallbackResult?.AssignedModelId ?? "null"}, name={fallbackResult?.AssignedModelName ?? "null"}, shortCircuited={fallbackResult?.IsShortCircuited}");
		}

		// ---------- Guard Clause ตาม Inbound Contract (ไม่อยู่ในตาราง TC-11-01..11) ----------
		private static void RunGuardTests()
		{
			bool nullCollection = false;
			string? paramName = null;
			try { RunModel1112(null!, DefaultSeasonal, DefaultTrend); }
			catch (ArgumentNullException ex) { nullCollection = true; paramName = ex.ParamName; }

			bool nullItem = false;
			try { RunModel1112(new List<SkuModelRoutingState> { null! }, DefaultSeasonal, DefaultTrend); }
			catch (ArgumentException ex) when (ex is not ArgumentNullException) { nullItem = ex.Message.Contains("Candidate state item cannot be null."); }

			RecordResult(nullCollection && paramName == "candidateStates" && nullItem, "GUARD-11", "Inbound Validation Guard Clauses",
				"Sub-case A: candidateStates = null | Sub-case B: รายการย่อยเป็น null",
				"A: ArgumentNullException(candidateStates) | B: ArgumentException(Candidate state item cannot be null.)",
				$"A: thrown={nullCollection}, param={paramName ?? "null"} | B: thrown={nullItem}");
		}

		// รัน Model 1.1 แล้ว Model 1.2 ตามลำดับเดียวกับ Pipeline (ผ่าน ModelEngine ตัวเดียวกัน) แล้วคืน List เดิม
		private static List<SkuModelRoutingState> RunModel1112(List<SkuModelRoutingState> states, decimal seasonalThreshold, decimal trendThreshold)
		{
			ModelEngine.ApplyModel(states, state => ModelEngine.ApplyModel11(state, seasonalThreshold));
			ModelEngine.ApplyModel(states, state => ModelEngine.ApplyModel12(state, trendThreshold));
			return states;
		}

		// พิมพ์ผล 1 เคส (รูปแบบเดียวกับ ModelStep12Tests / ModelStep34Tests: Input -> Expected -> Actual) และนับ PASS / FAIL
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

		// สร้าง State จำลองจาก PBI-10 (ฟิลด์ที่ไม่เกี่ยวกับเคสใช้ค่ากลางๆ ฟิลด์ Routing เริ่มเป็น null / false = ยังไม่ถูกตัดจบ)
		private static SkuModelRoutingState CreateTestState(string id, decimal[] demand, int historyMonths, int activeMonths, decimal seasonalR, decimal trendR)
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
				CleanAdi = 1.0000m,
				CleanTrendR = trendR,
				CleanSeasonalR = seasonalR,
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

		// ติดป้ายโมเดลให้ State จำลอง (เหมือนที่ PBI-09 / PBI-10 ทำ)
		private static void Assign(SkuModelRoutingState s, string id, string name, string reason)
		{
			s.AssignedModelId = id;
			s.AssignedModelName = name;
			s.SelectionReason = reason;
			s.IsShortCircuited = true;
		}

		// เทียบ 4 ฟิลด์ Routing ของ State กับค่าที่คาดหวัง
		private static bool HasRouting(SkuModelRoutingState s, string? id, string? name, string? reason, bool shortCircuited)
		{
			return s.AssignedModelId == id &&
				   s.AssignedModelName == name &&
				   s.SelectionReason == reason &&
				   s.IsShortCircuited == shortCircuited;
		}

		// ตรวจว่า Metadata (FM-03, FM-04) ในตารางทดสอบตรงกับค่าที่คำนวณจากเวกเตอร์ยอดขายจริง (ใช้สูตรเดียวกับ PBI-03/04/08)
		private static bool MetadataMatchesVector(decimal[] demand, int historyMonths, int activeMonths)
		{
			return CleansingEngine.CalculateHistoryMonths(demand) == historyMonths &&
				   CleansingEngine.CalculateActiveMonths(demand) == activeMonths;
		}

		// ตรวจ FM-03, FM-04 รวม FM-09 (Lag-12 r) และ FM-08 (Trend r) ที่คำนวณจากเวกเตอร์จริง ต้องตรงตัวเลขในตาราง Jira
		private static bool MetadataMatchesVector(decimal[] demand, int historyMonths, int activeMonths, decimal seasonalR, decimal trendR)
		{
			decimal sd = CleansingEngine.CalculateSd(demand);
			return MetadataMatchesVector(demand, historyMonths, activeMonths) &&
				   CleansingEngine.CalculateLag12R(demand, historyMonths) == seasonalR &&
				   CleansingEngine.CalculateTrendR(demand, historyMonths, sd) == trendR;
		}

		// ----- ตัวช่วยสร้างอาเรย์ 24 งวด -----

		// สร้างเวกเตอร์ยอดขาย 24 งวดจากเลขจำนวนเต็ม (ไม่ครบ 24 จะโยน Exception ทันที กันพิมพ์เวกเตอร์ผิด)
		private static decimal[] D(params int[] values)
		{
			if (values.Length != 24)
				throw new InvalidOperationException($"เวกเตอร์ทดสอบต้องมี 24 งวด (ได้ {values.Length})");

			return values.Select(v => (decimal)v).ToArray();
		}
	}
}