namespace DRP_Project.src.ModelRouting
{

    // PBI-10 (SCRUM-21): Model Priority Chain Step 3 & 4 - Short History SMA (1.4) / Intermittent Croston (1.5)
    // รับ List<SkuModelRoutingState> จาก PBI-09 แล้วติดป้ายโมเดลให้ SKU ที่ยังไม่ถูกตัดจบ (IsShortCircuited = false)
    // Step 3: ประวัติขายจริง < 12 เดือน -> 1.4 | Step 4: ประวัติ >= 12 เดือน และ ADI สะอาด > ADI_THRESHOLD -> 1.5
    // ส่งต่อ State ครบทุก SKU ให้ PBI-11 (Full-List Pass-Through, DEC-PBI09-01) | ไม่ยุ่งกับฐานข้อมูล
    // (ADI_THRESHOLD ที่อ่านจาก M_LOV ส่งเข้ามาทางพารามิเตอร์) | ใช้ SkuModelRoutingState ตัวเดิมจาก ModelStep12Engine.cs
    // HistoryMonths และ CleanAdi อ่านจาก State โดยตรง (PBI-08 คำนวณไว้แล้ว) ไม่คำนวณซ้ำ
    public static class PriorityChainStep34Engine
    {
        // Step 3 -> โมเดล 1.4 | Step 4 -> โมเดล 1.5
        public const string ShortHistoryModelId = "1.4";
        public const string ShortHistoryModelName = "Simple Moving Average (<12M SMA)";
        public const string ShortHistoryReason = "Sales history is less than 12 months";

        public const string IntermittentModelId = "1.5";
        public const string IntermittentModelName = "Croston Method";
        // ข้อความตามสเปก (hardcode "1.32" เหมือน Gate 1 ใน CleansingEngine) แม้ M_LOV จะถูกเปลี่ยนเป็นค่าอื่น
        public const string IntermittentReason = "Intermittent demand pattern with ADI > 1.32";

        // ประวัติขั้นต่ำ 1 รอบฤดูกาล: HistoryMonths < 12 -> ประวัติสั้น (ค่าคงที่ตามสเปก ไม่อ่านจาก M_LOV)
        public const int MinHistoryMonths = 12;

        // ค่า Default เมื่อ M_LOV ไม่พบหรือแปลงค่าไม่ได้ (ADI_THRESHOLD)
        public const decimal DefaultAdiThreshold = 1.320000m;

        // ประเมินทุก SKU: แก้ State ในที่ (in-place) แล้วคืน List เดิมครบทุกรายการ (Full-List Pass-Through)
        // State ที่ได้โมเดลจาก PBI-09 แล้ว (IsShortCircuited = true) ข้ามไปโดยคงค่าเดิมทุกประการ
        public static List<SkuModelRoutingState> EvaluateAll(List<SkuModelRoutingState> candidateStates, decimal adiThreshold)
        {
            if (candidateStates == null)
                throw new ArgumentNullException(nameof(candidateStates), "Candidate state collection cannot be null.");

            foreach (var state in candidateStates)
            {
                if (state == null)
                    throw new ArgumentException("Candidate state item cannot be null.");

                // Skip Short-Circuited Policy (DEC-PBI09-01): ได้ 1.7 / 1.6 มาแล้ว ห้ามแตะต้อง
                if (state.IsShortCircuited) continue;

                Evaluate(state, adiThreshold);
            }

            return candidateStates;
        }

        // ประเมิน 1 SKU (ผู้เรียกต้องกรอง IsShortCircuited ออกก่อน): Step 3 -> (ถ้าไม่เข้า) Step 4 -> ตรวจ Invariant
        public static SkuModelRoutingState Evaluate(SkuModelRoutingState state, decimal adiThreshold)
        {
            // Strict Step 3 Priority: เข้า Step 3 แล้วหยุดทันที แม้ ADI จะเกินเกณฑ์ก็ไม่ถูกส่งไปตรวจ Step 4
            if (!ApplyStep3(state))
                ApplyStep4(state, adiThreshold);

            // State Invariant: short-circuit <=> มีโมเดลครบ 3 ฟิลด์ | ไม่ short-circuit <=> ทั้ง 3 ฟิลด์เป็น null
            bool hasModel = state.AssignedModelId != null && state.AssignedModelName != null && state.SelectionReason != null;
            bool noModel = state.AssignedModelId == null && state.AssignedModelName == null && state.SelectionReason == null;
            if ((state.IsShortCircuited && !hasModel) || (!state.IsShortCircuited && !noModel))
                throw new InvalidOperationException($"State Invariant ผิดพลาด ({state.WarehouseCode}|{state.ItemId})");

            return state;
        }

        // Step 3: ประวัติขายจริงสั้นกว่า 12 เดือน -> 1.4 | คืน true เมื่อเข้าเงื่อนไข
        // ใช้ < เท่านั้น: HistoryMonths = 12 พอดีไม่เข้า Step 3
        public static bool ApplyStep3(SkuModelRoutingState state)
        {
            if (state.HistoryMonths < MinHistoryMonths)
            {
                Assign(state, ShortHistoryModelId, ShortHistoryModelName, ShortHistoryReason);
                return true;
            }
            return false;
        }

        // Step 4: ยานานๆ ขายที (ADI สะอาด > เกณฑ์) -> 1.5 | คืน true เมื่อเข้าเงื่อนไข
        // ใช้ > เท่านั้น: CleanAdi = เกณฑ์พอดีไม่เข้า Step 4 | CleanAdi เป็นค่าที่ปัด 4 ตำแหน่งมาแล้วจาก PBI-08
        public static bool ApplyStep4(SkuModelRoutingState state, decimal adiThreshold)
        {
            if (state.CleanAdi > adiThreshold)
            {
                Assign(state, IntermittentModelId, IntermittentModelName, IntermittentReason);
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
    }

}