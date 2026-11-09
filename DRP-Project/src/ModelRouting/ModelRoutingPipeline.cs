using DRP_Project.src.Cleansing;
using Serilog;

namespace DRP_Project.src.ModelRouting
{

    // Model Routing Pipeline (Stage 1.2): Orchestrator ของ PBI-09 (ต่อด้วย PBI-10 ถึง PBI-12)
    // รูปแบบเดียวกับ CleansingPipeline: Program.cs เรียกทีละบรรทัด | ไม่มี Business Logic (สูตรอยู่ใน Engine)
    // ไม่รับ SqlConnection เพราะ PBI-09 ไม่อ่าน M_LOV และไม่เขียน DB (PBI-12 เป็นผู้บันทึก)
    // ไม่ catch Exception: ปล่อยให้ Main ใน Program.cs ดักและ log ที่เดียว
    public static class ModelRoutingPipeline
    {
        // ======================================================
        // PBI-09: Priority Chain Step 1 (No Sales -> 1.7) และ Step 2 (No Sales L3M -> 1.6)
        // คืน State ครบทุก SKU: ที่ถูกดัก (IsShortCircuited = true) และที่ผ่านต่อให้ PBI-10 (โมเดลเป็น null)
        // ======================================================
        public static List<SkuModelRoutingState> RunPriorityChainStep12(List<CleansedSkuDemandDto> dtos)
        {
            List<SkuModelRoutingState> states = PriorityChainStep12Engine.EvaluateAll(dtos);

            int noSales = states.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesModelId);
            int noSalesL3M = states.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesL3MModelId);
            int passThrough = states.Count(s => !s.IsShortCircuited);

            Console.WriteLine();
            Console.WriteLine("--- Priority Chain Step 1 & 2 (PBI-09) ---");
            Console.WriteLine($"Input (SKU ทั้งหมด)             : {dtos.Count}");
            Console.WriteLine($"Step 1  1.7 No Sales            : {noSales}");
            Console.WriteLine($"Step 2  1.6 No Sales L3M        : {noSalesL3M}");
            Console.WriteLine($"ส่งต่อ PBI-10 (Step 3 & 4)       : {passThrough}");

            Console.WriteLine();
            Console.WriteLine("--- Priority Chain Step 1 & 2 : แยกตามคลัง ---");
            foreach (var g in states.GroupBy(s => s.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | 1.7 {g.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesModelId),7:N0} | 1.6 {g.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesL3MModelId),7:N0} | ส่งต่อ {g.Count(s => !s.IsShortCircuited),7:N0}");
            }

            Log.Information("PBI-09 เสร็จสิ้น SKU {Total}: 1.7={NoSales} 1.6={NoSalesL3M} ส่งต่อ={PassThrough}", states.Count, noSales, noSalesL3M, passThrough);

            return states;
        }
    }

}