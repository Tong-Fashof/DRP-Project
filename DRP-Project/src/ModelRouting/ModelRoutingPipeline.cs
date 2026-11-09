using DRP_Project.src.Cleansing;
using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.ModelRouting
{

    // Model Routing Pipeline (Stage 1.2): Orchestrator ของ PBI-09 และ PBI-10 (ต่อด้วย PBI-11 ถึง PBI-12)
    // รูปแบบเดียวกับ CleansingPipeline: Program.cs เรียกทีละบรรทัด | ไม่มี Business Logic (สูตรอยู่ใน Engine)
    // PBI-09 ไม่รับ SqlConnection เพราะไม่อ่าน M_LOV | PBI-10 รับ SqlConnection เพื่ออ่าน ADI_THRESHOLD จาก M_LOV เท่านั้น
    // ทั้งสอง Step ไม่เขียน DB (PBI-12 เป็นผู้บันทึก)
    // ไม่ catch Exception: ปล่อยให้ Main ใน Program.cs ดักและ log ที่เดียว
    public static class ModelRoutingPipeline
    {
        // ======================================================
        // PBI-09: Model Priority Chain Step 1 (No Sales -> 1.7) และ Step 2 (No Sales L3M -> 1.6)
        // คืน State ครบทุก SKU: ที่ถูกดัก (IsShortCircuited = true) และที่ผ่านต่อให้ PBI-10 (โมเดลเป็น null)
        // ======================================================
        public static List<SkuModelRoutingState> RunPriorityChainStep12(List<CleansedSkuDemandDto> dtos)
        {
            List<SkuModelRoutingState> states = PriorityChainStep12Engine.EvaluateAll(dtos);

            int noSales = states.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesModelId);
            int noSalesL3M = states.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesL3MModelId);
            int passThrough = states.Count(s => !s.IsShortCircuited);

            Console.WriteLine();
            Console.WriteLine("--- Model Priority Chain Step 1 & 2 ---");
            Console.WriteLine($"Input (SKU ทั้งหมด)           : {dtos.Count}");
            Console.WriteLine($"Step 1  1.7 No Sales        : {noSales}");
            Console.WriteLine($"Step 2  1.6 No Sales L3M    : {noSalesL3M}");
            Console.WriteLine($"ส่งต่อ Step 3 & 4             : {passThrough}");

            Console.WriteLine();
            Console.WriteLine("--- Model Priority Chain Step 1 & 2 : แยกตามคลัง ---");
            foreach (var g in states.GroupBy(s => s.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | 1.7 {g.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesModelId),7:N0} | 1.6 {g.Count(s => s.AssignedModelId == PriorityChainStep12Engine.NoSalesL3MModelId),7:N0} | ส่งต่อ {g.Count(s => !s.IsShortCircuited),7:N0}");
            }

            Log.Information("PBI-09 เสร็จสิ้น SKU {Total}: 1.7={NoSales} 1.6={NoSalesL3M} ส่งต่อ={PassThrough}", states.Count, noSales, noSalesL3M, passThrough);

            return states;
        }

        // ======================================================
        // PBI-10: Model Priority Chain Step 3 (ประวัติสั้น < 12 เดือน -> 1.4 SMA) และ Step 4 (ยานานๆ ขายที ADI > เกณฑ์ -> 1.5 Croston)
        // โหลดเกณฑ์ ADI_THRESHOLD จาก M_LOV (ไม่พบหรือแปลงไม่ได้ -> Default 1.32 และมี Warning ใน log)
        // รับ State ครบทุก SKU จาก PBI-09 แล้วคืน State ครบทุก SKU ให้ PBI-11 (SKU ที่ได้ 1.7 / 1.6 แล้วถูกข้าม ไม่เปลี่ยนค่า)
        // ======================================================
        public static List<SkuModelRoutingState> RunPriorityChainStep34(SqlConnection connection, List<SkuModelRoutingState> states)
        {
            decimal adiThreshold = OutlierCleansingRepository.LoadLovDecimal(connection, "ADI_THRESHOLD", PriorityChainStep34Engine.DefaultAdiThreshold);

            List<SkuModelRoutingState> evaluated = PriorityChainStep34Engine.EvaluateAll(states, adiThreshold);

            int sma = evaluated.Count(s => s.AssignedModelId == PriorityChainStep34Engine.ShortHistoryModelId);
            int croston = evaluated.Count(s => s.AssignedModelId == PriorityChainStep34Engine.IntermittentModelId);
            int passThrough = evaluated.Count(s => !s.IsShortCircuited);
            int skipped = evaluated.Count - sma - croston - passThrough;   // ได้ 1.7 / 1.6

            Console.WriteLine();
            Console.WriteLine("--- Model Priority Chain Step 3 & 4 ---");
            Console.WriteLine($"เกณฑ์: ADI_THRESHOLD = {adiThreshold}");
            Console.WriteLine($"Input (SKU ทั้งหมด)       : {evaluated.Count}");
            Console.WriteLine($"ข้าม (ได้ 1.7/1.6)        : {skipped}");
            Console.WriteLine($"Step 3  1.4 SMA (<12M)  : {sma}");
            Console.WriteLine($"Step 4  1.5 Croston     : {croston}");
            Console.WriteLine($"ส่งต่อ Step 5 & 6         : {passThrough}");

            Console.WriteLine();
            Console.WriteLine("--- Model Priority Chain Step 3 & 4 : แยกตามคลัง ---");
            foreach (var g in evaluated.GroupBy(s => s.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{g.Key,-12} รวม {g.Count(),7:N0} | 1.4 {g.Count(s => s.AssignedModelId == PriorityChainStep34Engine.ShortHistoryModelId),7:N0} | 1.5 {g.Count(s => s.AssignedModelId == PriorityChainStep34Engine.IntermittentModelId),7:N0} | ส่งต่อ {g.Count(s => !s.IsShortCircuited),7:N0}");
            }

            Log.Information("Step 3 & 4 เสร็จสิ้น SKU {Total}: ข้าม={Skipped} 1.4={Sma} 1.5={Croston} ส่งต่อ={PassThrough} ADI_THRESHOLD={AdiThreshold}", evaluated.Count, skipped, sma, croston, passThrough, adiThreshold);

            return evaluated;
        }
    }

}