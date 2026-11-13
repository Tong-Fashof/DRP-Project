using DRP_Project.src.Cleansing;

namespace DRP_Project.src.ModelRouting
{

    // ======================================================================
    // Model Engine (Stage 1.2): กฎเลือกโมเดลพยากรณ์ทั้งหมดอยู่ที่ไฟล์นี้ (รวมจาก PBI-09, PBI-10, PBI-11)
    // ลำดับความสำคัญ (Priority Chain) ตามเลขโมเดล: 1.7 -> 1.6 -> 1.4 -> 1.5 -> 1.1 -> 1.2 (-> 1.3 SES Fallback เป็นงาน PBI-12)
    // เลขโมเดลสวนทางกับลำดับการประเมิน ระวังสลับ
    //
    // วิธีใช้ (Pipeline เรียกทีละโมเดลตามลำดับ):  ToStates(dtos) -> ApplyModel(states, ApplyModel17) -> ApplyModel(states, ApplyModel16) -> ...
    // ApplyModel เป็นตัวควบคุมเดียว: ข้าม SKU ที่ได้โมเดลแล้ว (IsShortCircuited = true) แล้วเรียกกฎของโมเดลนั้นกับ SKU ที่เหลือ
    // ApplyModelXX แต่ละตัวคือ 1 เงื่อนไขใน flow (1 diamond): เข้าเงื่อนไข -> ติดป้ายโมเดลแล้วคืน true | ไม่เข้า -> ไม่แตะ State คืน false
    // ไม่ยุ่งกับฐานข้อมูล (เกณฑ์จาก M_LOV ส่งเข้ามาทางพารามิเตอร์) | ใช้ค่าที่ PBI-08 คำนวณไว้แล้ว ไม่คำนวณสถิติซ้ำ
    // ======================================================================

    // ผลลัพธ์ต่อ 1 SKU (Outbound Contract ของ PBI-09 -> PBI-10 / PBI-12), 19 พร็อพเพอร์ตี้ = 15 จาก DTO + 4 ฟิลด์ Routing
    public class SkuModelRoutingState
    {
        // ----- 15 พร็อพเพอร์ตี้จาก CleansedSkuDemandDto (ส่งผ่านไม่แก้ค่า) -----
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";
        public decimal[] CleansedDemand24M { get; set; } = new decimal[24];

        public decimal CleanMean { get; set; }
        public decimal CleanSd { get; set; }
        public decimal CleanCv { get; set; }
        public int CleanActiveMonths { get; set; }
        public decimal CleanAdi { get; set; }
        public decimal CleanTrendR { get; set; }
        public decimal CleanSeasonalR { get; set; }

        public int HistoryMonths { get; set; }
        public string OutlierMethod { get; set; } = "";

        public decimal? Skewness { get; set; }
        public decimal? Kurtosis { get; set; }
        public decimal? JbPValue { get; set; }

        // ----- 4 ฟิลด์ Routing: ไม่เข้าเงื่อนไขทั้งสองขั้น = null / false ให้ PBI-10 ตัดสินต่อ -----
        public string? AssignedModelId { get; set; }
        public string? AssignedModelName { get; set; }
        public string? SelectionReason { get; set; }
        public bool IsShortCircuited { get; set; }
    }

    public static class ModelEngine
    {
        // ----- ID และชื่อโมเดล (Pipeline ใช้แสดงสรุป) -----
        public const string Model17Id = "1.7"; public const string Model17Name = "No Sales";
        public const string Model16Id = "1.6"; public const string Model16Name = "No Sales Last 3 Months (No Sales L3M)";
        public const string Model14Id = "1.4"; public const string Model14Name = "Simple Moving Average (<12M SMA)";
        public const string Model15Id = "1.5"; public const string Model15Name = "Croston Method";
        public const string Model11Id = "1.1"; public const string Model11Name = "Seasonal TSD (Decomposition)";
        public const string Model12Id = "1.2"; public const string Model12Name = "Trend DES (Holt Linear)";

        // ----- ค่าคงที่ตามสเปก (ไม่อ่านจาก M_LOV) -----
        public const int MinHistoryMonths = 12;            // Model 1.4: HistoryMonths < 12 = ประวัติสั้น
        public const int MinSeasonalHistoryMonths = 24;    // Model 1.1: ต้องมีประวัติครบ 2 รอบฤดูกาล
        private const int L3MStartIndex = 21;              // Model 1.6: 3 เดือนล่าสุด = M22..M24 = index 21..23

        // ----- ค่า Default เมื่อ M_LOV ไม่พบหรือแปลงค่าไม่ได้ -----
        public const decimal DefaultAdiThreshold = 1.320000m;        // ADI_THRESHOLD
        public const decimal DefaultSeasonalThreshold = 0.500000m;   // SEASONAL_CORRELATION
        public const decimal DefaultTrendThreshold = 0.500000m;      // TREND_CORRELATION

        // แปลง DTO จาก PBI-08 เป็น State ครบทุก SKU (ยังไม่ติดป้ายโมเดล: ฟิลด์ Routing เป็น null / false)
        // คัดลอกอาเรย์ กัน aliasing กับ DTO ต้นทาง
        public static List<SkuModelRoutingState> ToStates(List<CleansedSkuDemandDto> dtos)
        {
            if (dtos == null)
                throw new ArgumentNullException(nameof(dtos), "DTO collection cannot be null.");

            var states = new List<SkuModelRoutingState>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (dto.CleansedDemand24M == null || dto.CleansedDemand24M.Length != 24)
                    throw new ArgumentException($"CleansedDemand24M ต้องมี 24 งวด ({dto.WarehouseCode}|{dto.ItemId})");

                states.Add(new SkuModelRoutingState
                {
                    WarehouseCode = dto.WarehouseCode,
                    ItemId = dto.ItemId,
                    CleansedDemand24M = (decimal[])dto.CleansedDemand24M.Clone(),
                    CleanMean = dto.CleanMean,
                    CleanSd = dto.CleanSd,
                    CleanCv = dto.CleanCv,
                    CleanActiveMonths = dto.CleanActiveMonths,
                    CleanAdi = dto.CleanAdi,
                    CleanTrendR = dto.CleanTrendR,
                    CleanSeasonalR = dto.CleanSeasonalR,
                    HistoryMonths = dto.HistoryMonths,
                    OutlierMethod = dto.OutlierMethod,
                    Skewness = dto.Skewness,
                    Kurtosis = dto.Kurtosis,
                    JbPValue = dto.JbPValue
                });
            }
            return states;
        }

        // รัน 1 โมเดลกับทุก SKU: แก้ State ในที่ (in-place) ไม่เพิ่ม/ลบรายการ (Full-List Pass-Through, DEC-PBI09-01)
        // SKU ที่ได้โมเดลจากโมเดลก่อนหน้าแล้ว (IsShortCircuited = true) ข้ามไปโดยคงค่าเดิมทุกประการ
        public static void ApplyModel(List<SkuModelRoutingState> candidateStates, Func<SkuModelRoutingState, bool> rule)
        {
            if (candidateStates == null)
            {
                throw new ArgumentNullException(nameof(candidateStates), "Candidate state collection cannot be null.");
            }

            foreach (var state in candidateStates)
            {
                if (state == null)
                    throw new ArgumentException("Candidate state item cannot be null.");

                if (state.IsShortCircuited) continue;

                rule(state);

                // State Invariant: short-circuit <=> มีโมเดลครบ 3 ฟิลด์ | ไม่ short-circuit <=> ทั้ง 3 ฟิลด์เป็น null
                bool hasModel = state.AssignedModelId != null && state.AssignedModelName != null && state.SelectionReason != null;
                bool noModel = state.AssignedModelId == null && state.AssignedModelName == null && state.SelectionReason == null;
                if (state.IsShortCircuited ? !hasModel : !noModel)
                    throw new InvalidOperationException($"State Invariant ผิดพลาด ({state.WarehouseCode}|{state.ItemId})");
            }
        }

        // ===================== PBI-09 =====================

        // Model 1.7 (ลำดับ 1): ไม่เคยมียอดขายเลยตลอด 24 เดือน (ADI/Active Months = 0)
        public static bool ApplyModel17(SkuModelRoutingState state)
        {
            if (state.CleanActiveMonths != 0 && state.CleansedDemand24M.Sum() > 0m) return false;

            return Assign(state, Model17Id, Model17Name, "No sales in the past 24 months (Active Months = 0)");
        }

        // Model 1.6 (ลำดับ 2): 3 เดือนล่าสุด (M22..M24) ไม่มียอดขายเลย
        public static bool ApplyModel16(SkuModelRoutingState state)
        {
            decimal[] last3 = state.CleansedDemand24M.Skip(L3MStartIndex).ToArray();
            if (last3.Any(d => d > 0m) && last3.Sum() > 0m) return false;

            return Assign(state, Model16Id, Model16Name, "No sales in the last 3 consecutive months");
        }

        // ===================== PBI-10 =====================

        // Model 1.4 (ลำดับ 3): ประวัติขายจริงสั้นกว่า 12 เดือน | ใช้ < เท่านั้น (HistoryMonths = 12 พอดีไม่เข้า)
        public static bool ApplyModel14(SkuModelRoutingState state)
        {
            if (state.HistoryMonths >= MinHistoryMonths) return false;

            return Assign(state, Model14Id, Model14Name, "Sales history is less than 12 months");
        }

        // Model 1.5 (ลำดับ 4): ยานานๆ ขายที CleanAdi > ADI_THRESHOLD | ใช้ > เท่านั้น (เท่าเกณฑ์พอดีไม่เข้า)
        // Reason hardcode "1.32" ตามสเปก แม้ M_LOV จะถูกเปลี่ยนเป็นค่าอื่น
        public static bool ApplyModel15(SkuModelRoutingState state, decimal adiThreshold)
        {
            if (state.CleanAdi <= adiThreshold) return false;

            return Assign(state, Model15Id, Model15Name, "Intermittent demand pattern with ADI > 1.32");
        }

        // ===================== PBI-11 =====================

        // Model 1.1 (ลำดับ 5): ประวัติครบ 24 เดือน และ Lag-12 r >= SEASONAL_CORRELATION | ใช้ >= (ไม่ใช้ abs: ฤดูกาลต้องสัมพันธ์เชิงบวก)
        // ฤดูกาลมาก่อนแนวโน้มเสมอ (DEC-PBI11): เข้า 1.1 แล้วหยุด แม้ |Trend r| จะเกินเกณฑ์ก็ไม่ถูกตรวจ 1.2
        public static bool ApplyModel11(SkuModelRoutingState state, decimal seasonalThreshold)
        {
            if (state.HistoryMonths < MinSeasonalHistoryMonths || state.CleanSeasonalR < seasonalThreshold) return false;

            return Assign(state, Model11Id, Model11Name, "Full 24M history with strong seasonal pattern (Lag-12 r >= 0.5)");
        }

        // Model 1.2 (ลำดับ 6): แนวโน้มชัดเจน |Trend r| >= TREND_CORRELATION (ขึ้นหรือลง) | ใช้ >= กับค่าสัมบูรณ์
        // ไม่เข้า -> ไม่มีโมเดล (null) ส่งต่อ PBI-11B / PBI-12 (Model 1.3 SES)
        public static bool ApplyModel12(SkuModelRoutingState state, decimal trendThreshold)
        {
            if (Math.Abs(state.CleanTrendR) < trendThreshold) return false;

            return Assign(state, Model12Id, Model12Name, "Significant growth/decline sales trend (|Trend r| >= 0.5)");
        }

        // ติดป้ายโมเดลทั้ง 4 ฟิลด์ (ตัดจบ Priority Chain) แล้วคืน true เพื่อให้กฎเขียนเป็นบรรทัดเดียว
        private static bool Assign(SkuModelRoutingState state, string modelId, string modelName, string reason)
        {
            state.AssignedModelId = modelId;
            state.AssignedModelName = modelName;
            state.SelectionReason = reason;
            state.IsShortCircuited = true;
            return true;
        }
    }

}