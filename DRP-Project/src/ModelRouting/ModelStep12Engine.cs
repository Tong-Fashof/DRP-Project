using DRP_Project.src.Cleansing;

namespace DRP_Project.src.ModelRouting
{

    // PBI-09 (SCRUM-20):  Model Priority Chain Step 1 & 2 - No Sales (1.7) / No Sales L3M (1.6)
    // ดักสินค้าที่ไม่มีการเคลื่อนไหวทางธุรกิจ เพื่อระงับการพยากรณ์ (ป้องกันสั่งซื้อ Dead Stock)
    // รับ CleansedSkuDemandDto จาก PBI-08 แล้วส่งต่อ SkuModelRoutingState ครบ 19 พร็อพเพอร์ตี้ทุก SKU (Full-List Pass-Through, DEC-PBI09-01)
    // ส่งต่อให้ PBI-10 (Step 3 & 4) และ PBI-12 (บันทึกผล) | ไม่ยุ่งกับฐานข้อมูลและไม่อ่าน M_LOV (เกณฑ์เป็นค่าคงที่ 0.0000m)

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

    public static class PriorityChainStep12Engine
    {
        // Step 1 -> โมเดล 1.7 | Step 2 -> โมเดล 1.6 (เลขโมเดลสวนทางกับลำดับขั้น ระวังสลับ)
        public const string NoSalesModelId = "1.7";
        public const string NoSalesModelName = "No Sales";
        public const string NoSalesReason = "No sales in the past 24 months (Active Months = 0)";

        public const string NoSalesL3MModelId = "1.6";
        public const string NoSalesL3MModelName = "No Sales Last 3 Months (No Sales L3M)";
        public const string NoSalesL3MReason = "No sales in the last 3 consecutive months";

        // 3 เดือนล่าสุด = M22, M23, M24 = C# index 21..23
        private const int L3MStartIndex = 21;

        // ประเมินทุก SKU: คืน State 1 รายการต่อ 1 SKU เรียงตามอินพุต (Full-List Pass-Through)
        public static List<SkuModelRoutingState> EvaluateAll(List<CleansedSkuDemandDto> dtos)
        {
            var results = new List<SkuModelRoutingState>(dtos.Count);
            foreach (var dto in dtos)
            {
                results.Add(Evaluate(dto));
            }

            if (results.Count != dtos.Count)
                throw new InvalidOperationException($"PBI-09 ผิดพลาด: จำนวน SKU ไม่ครบ (เข้า {dtos.Count} ออก {results.Count})");

            return results;
        }

        // ประเมิน 1 SKU: map DTO -> State -> Step 1 -> (ถ้าไม่เข้า) Step 2 -> ตรวจ Invariant
        public static SkuModelRoutingState Evaluate(CleansedSkuDemandDto dto)
        {
            if (dto.CleansedDemand24M == null || dto.CleansedDemand24M.Length != 24)
                throw new ArgumentException($"CleansedDemand24M ต้องมี 24 งวด ({dto.WarehouseCode}|{dto.ItemId})");

            SkuModelRoutingState state = ToState(dto);

            // ลำดับสำคัญ: Step 1 ก่อนเสมอ (SKU ที่ไม่เคยขายเลยก็เข้าเงื่อนไข L3M ด้วย) เข้าแล้วหยุด ไม่ส่งต่อ Step 2
            if (!ApplyStep1(state))
                ApplyStep2(state);

            // State Invariant: short-circuit <=> มีโมเดลครบ 3 ฟิลด์ | ไม่ short-circuit <=> ทั้ง 3 ฟิลด์เป็น null
            bool hasModel = state.AssignedModelId != null && state.AssignedModelName != null && state.SelectionReason != null;
            bool noModel = state.AssignedModelId == null && state.AssignedModelName == null && state.SelectionReason == null;
            if ((state.IsShortCircuited && !hasModel) || (!state.IsShortCircuited && !noModel))
                throw new InvalidOperationException($"State Invariant ผิดพลาด ({dto.WarehouseCode}|{dto.ItemId})");

            return state;
        }

        // Step 1: ไม่เคยมียอดขายเลยตลอด 24 เดือน -> 1.7 | คืน true เมื่อเข้าเงื่อนไข
        // clean_active_months อ่านจาก DTO (PBI-08 คำนวณไว้แล้ว) | sum_demand_24m รวมจาก index 0..23
        public static bool ApplyStep1(SkuModelRoutingState state)
        {
            decimal sum24 = 0m;
            for (int i = 0; i < 24; i++) sum24 += state.CleansedDemand24M[i];

            if (state.CleanActiveMonths == 0 || sum24 <= 0.0000m)
            {
                Assign(state, NoSalesModelId, NoSalesModelName, NoSalesReason);
                return true;
            }
            return false;
        }

        // Step 2: 3 เดือนล่าสุด (index 21..23) ไม่มียอดขายเลย -> 1.6 | คืน true เมื่อเข้าเงื่อนไข
        public static bool ApplyStep2(SkuModelRoutingState state)
        {
            decimal sumL3M = 0m;
            int activeL3M = 0;
            for (int i = L3MStartIndex; i < 24; i++)
            {
                sumL3M += state.CleansedDemand24M[i];
                if (state.CleansedDemand24M[i] > 0.0000m) activeL3M++;
            }

            if (activeL3M == 0 || sumL3M <= 0.0000m)
            {
                Assign(state, NoSalesL3MModelId, NoSalesL3MModelName, NoSalesL3MReason);
                return true;
            }
            return false;
        }

        private static void Assign(SkuModelRoutingState state, string modelId, string modelName, string reason)
        {
            state.AssignedModelId = modelId;
            state.AssignedModelName = modelName;
            state.SelectionReason = reason;
            state.IsShortCircuited = true;
        }

        // DTO 15 พร็อพเพอร์ตี้ -> State (คัดลอกอาเรย์ กัน aliasing กับ DTO ต้นทาง) | ฟิลด์ Routing เริ่มต้น null / false
        private static SkuModelRoutingState ToState(CleansedSkuDemandDto dto)
        {
            return new SkuModelRoutingState
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
                JbPValue = dto.JbPValue,
                AssignedModelId = null,
                AssignedModelName = null,
                SelectionReason = null,
                IsShortCircuited = false
            };
        }
    }

}