namespace DRP_Project.src.ModelRouting
{

    // PBI-12 (Step 7 Fallback): กำหนด Model 1.3 Single Exponential Smoothing (SES) ให้ SKU ที่ไม่เข้า Step 1-6
    // ทำงานบน List<SkuForecastResult> จาก PBI-11B (ไม่ยุ่ง DB | DB I/O อยู่ใน ForecastResultRepository.cs)
    // ลำดับ: ตรวจ Inconsistent State ทุก SKU ก่อน (AC 12.5) -> กำหนด SES เฉพาะ IsShortCircuited == false -> ตรวจว่าไม่มี SKU ตกค้าง
    public static class SesFallbackEngine
    {
        public const string Model13Id = "1.3";
        public const string Model13Name = "Single Exponential Smoothing (SES)";   // ใช้แสดงสรุปเท่านั้น (ไม่อยู่ใน 72 คอลัมน์)
        public const string Model13Reason = "NO STRONG TREND / SEASONAL SCREEN";   // ตรงตามสูตร COL:AR
        public const string Model13DemandGroup = "G3";                              // ตรงตามสูตร COL:AI

        // คืนจำนวน SKU ที่ได้ SES | แก้ค่าในที่ (in-place) ไม่เพิ่ม/ลบรายการ
        public static int ApplyAll(List<SkuForecastResult> results)
        {
            if (results == null)
                throw new ArgumentNullException(nameof(results), "Forecast result collection cannot be null.");

            // 1) Guard ทุก SKU ก่อนแก้ค่าใดๆ: พบความขัดแย้งแม้ 1 ตัว = หยุดทั้งชุด กันข้อมูลปนเปื้อน
            foreach (var r in results)
            {
                // กรณี A: มีโมเดลแล้ว แต่ธง = false | กรณี B: ยังไม่มีโมเดล แต่ธง = true
                if ((r.AssignedModelId != null && !r.IsShortCircuited) ||
                    (r.AssignedModelId == null && r.IsShortCircuited))
                {
                    throw new InvalidOperationException(
                        $"Inconsistent State ({r.WarehouseCode}|{r.ItemId}): AssignedModelId={r.AssignedModelId ?? "null"}, IsShortCircuited={r.IsShortCircuited}");
                }
            }

            // 2) กำหนด SES เฉพาะที่ยังไม่ได้โมเดล | ที่ได้โมเดลจาก Step 1-6 แล้วคงค่าเดิม ไม่ประเมินซ้ำ
            int assigned = 0;
            foreach (var r in results)
            {
                if (r.IsShortCircuited) continue;

                r.AssignedModelId = Model13Id;
                r.SelectionReason = Model13Reason;
                r.DemandGroup = Model13DemandGroup;
                r.IsShortCircuited = true;
                assigned++;
            }

            // 3) รับประกันไม่มี SKU ตกค้างโดยไม่มีโมเดล
            if (results.Any(r => r.AssignedModelId == null || !r.IsShortCircuited))
                throw new InvalidOperationException("พบ SKU ที่ยังไม่ได้รับโมเดลหลัง Step 7 (SES Fallback)");

            return assigned;
        }
    }

}
