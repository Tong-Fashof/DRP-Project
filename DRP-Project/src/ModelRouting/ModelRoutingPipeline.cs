using DRP_Project.src.Cleansing;
using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.ModelRouting
{

    // Model Routing Pipeline (Stage 1.2): Orchestrator ของ PBI-09 ถึง PBI-11 รันทีละ Model ตามลำดับความสำคัญ
    // ลำดับ: Model 1.7 -> 1.6 -> 1.4 -> 1.5 -> 1.1 -> 1.2 (เลขโมเดลสวนทางกับลำดับการประเมิน)
    //   PBI-09: RunModel17, RunModel16 | PBI-10: RunModel14, RunModel15 | PBI-11: RunModel11, RunModel12
    // Model 1.3 SES (Fallback) เป็นงาน PBI-12 | SKU ที่ไม่เข้า Model 1.1 และ 1.2 ยังไม่มีโมเดล (null) ส่งต่อ PBI-11B / PBI-12
    // รูปแบบเดียวกับ CleansingPipeline: Program.cs เรียกทีละบรรทัด | ไม่มี Business Logic (กฎของแต่ละโมเดลอยู่ใน ModelEngine.cs)
    // แต่ละ Model รับ State ครบทุก SKU แล้วคืน State ครบทุก SKU (Full-List Pass-Through, DEC-PBI09-01) | SKU ที่ได้โมเดลแล้วถูกข้าม
    // RunModel15, RunModel11 และ RunModel12 รับ SqlConnection เพื่ออ่านเกณฑ์จาก M_LOV (ADI_THRESHOLD, SEASONAL_CORRELATION, TREND_CORRELATION)
    // ทุก Model ไม่เขียน DB (PBI-12 เป็นผู้บันทึก) | ไม่ catch Exception: ปล่อยให้ Main ใน Program.cs ดักและ log ที่เดียว
    public static class ModelRoutingPipeline
    {
        // Model 1.7 No Sales (PBI-09): แปลง DTO จาก PBI-08 เป็น State ครบทุก SKU แล้วติดป้ายเฉพาะที่เข้าเงื่อนไข
        public static List<SkuModelRoutingState> RunModel17(List<CleansedSkuDemandDto> dtos)
        {
            List<SkuModelRoutingState> states = ModelEngine.ToStates(dtos);
            return Run(states, ModelEngine.Model17Id, ModelEngine.Model17Name, "No sales 24 เดือน (Active Months = 0)", ModelEngine.ApplyModel17);
        }

        // Model 1.6 No Sales L3M (PBI-09): ไม่มียอดขาย 3 เดือนล่าสุด
        public static List<SkuModelRoutingState> RunModel16(List<SkuModelRoutingState> states)
        {
            return Run(states, ModelEngine.Model16Id, ModelEngine.Model16Name, "ไม่มียอดขาย 3 เดือนล่าสุด", ModelEngine.ApplyModel16);
        }

        // Model 1.4 SMA (PBI-10): ประวัติขายจริงสั้นกว่า 12 เดือน
        public static List<SkuModelRoutingState> RunModel14(List<SkuModelRoutingState> states)
        {
            return Run(states, ModelEngine.Model14Id, ModelEngine.Model14Name, $"HistoryMonths < {ModelEngine.MinHistoryMonths}", ModelEngine.ApplyModel14);
        }

        // Model 1.5 Croston (PBI-10): ยานานๆ ขายที | โหลด ADI_THRESHOLD จาก M_LOV (ไม่พบหรือแปลงไม่ได้ -> Default 1.32 และมี Warning ใน log)
        public static List<SkuModelRoutingState> RunModel15(SqlConnection connection, List<SkuModelRoutingState> states)
        {
            decimal adiThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "ADI_THRESHOLD", ModelEngine.DefaultAdiThreshold);
            return Run(states, ModelEngine.Model15Id, ModelEngine.Model15Name, $"CleanAdi > ADI_THRESHOLD ({adiThreshold})",
                state => ModelEngine.ApplyModel15(state, adiThreshold));
        }

        // Model 1.1 Seasonal TSD (PBI-11): ประวัติ >= 24 เดือน และ Lag-12 r >= SEASONAL_CORRELATION (Default 0.5)
        public static List<SkuModelRoutingState> RunModel11(SqlConnection connection, List<SkuModelRoutingState> states)
        {
            decimal seasonalThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "SEASONAL_CORRELATION", ModelEngine.DefaultSeasonalThreshold);
            return Run(states, ModelEngine.Model11Id, ModelEngine.Model11Name,
                $"HistoryMonths >= {ModelEngine.MinSeasonalHistoryMonths} และ CleanSeasonalR >= SEASONAL_CORRELATION ({seasonalThreshold})",
                state => ModelEngine.ApplyModel11(state, seasonalThreshold));
        }

        // Model 1.2 Trend DES (PBI-11): |Trend r| >= TREND_CORRELATION (Default 0.5) | SKU ที่เหลือยังไม่มีโมเดล ส่งต่อ PBI-11B / PBI-12
        public static List<SkuModelRoutingState> RunModel12(SqlConnection connection, List<SkuModelRoutingState> states)
        {
            decimal trendThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "TREND_CORRELATION", ModelEngine.DefaultTrendThreshold);
            return Run(states, ModelEngine.Model12Id, ModelEngine.Model12Name, $"|CleanTrendR| >= TREND_CORRELATION ({trendThreshold})",
                state => ModelEngine.ApplyModel12(state, trendThreshold));
        }

        // ตัวช่วยร่วมของทุก Model: รัน 1 โมเดลผ่าน ModelEngine แล้วแสดงสรุป (รวมและแยกตามคลัง) + log | ไม่มีสูตร
        private static List<SkuModelRoutingState> Run(List<SkuModelRoutingState> states, string modelId, string modelName, string criteria, Func<SkuModelRoutingState, bool> rule)
        {
            ModelEngine.ApplyModel(states, rule);

            int assigned = states.Count(s => s.AssignedModelId == modelId);
            int passThrough = states.Count(s => !s.IsShortCircuited);
            int skipped = states.Count - assigned - passThrough;   // ได้โมเดลจากโมเดลก่อนหน้าแล้ว

            Console.WriteLine();
            Console.WriteLine($"--- Model {modelId} {modelName} ---");
            Console.WriteLine($"เกณฑ์: {criteria}");
            Console.WriteLine($"Input (SKU ทั้งหมด)   : {states.Count}");
            Console.WriteLine($"ข้าม (ได้โมเดลแล้ว)    : {skipped}");
            Console.WriteLine($"ได้ Model {modelId,-4}     : {assigned}");
            Console.WriteLine($"ส่งต่อโมเดลถัดไป       : {passThrough}");

            Console.WriteLine();
            Console.WriteLine($"--- Model {modelId} : แยกตามคลัง ---");
            foreach (var g in states.GroupBy(s => s.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | {modelId} {g.Count(s => s.AssignedModelId == modelId),7:N0} | ส่งต่อ {g.Count(s => !s.IsShortCircuited),7:N0}");
            }

            Log.Information("Model {ModelId} เสร็จสิ้น SKU {Total}: ข้าม={Skipped} ได้={Assigned} ส่งต่อ={PassThrough}", modelId, states.Count, skipped, assigned, passThrough);

            return states;
        }
    }

}