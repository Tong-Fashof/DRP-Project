using System.Data;
using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.NetDemand
{
    // Step 1: เตรียมยอดความต้องการสุทธิรายเดือนต่อ SKU จากตาราง Staging ลงตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP
    // Net Demand = ยอดขายที่ปรับแล้ว + ยอดที่เสียไปจากสินค้าหมด (OOS Uplift) | ช่วงข้อมูล 24 เดือนล่าสุด
    public static class NetDemandPreprocessor
    {
        public record DemandResult(
            decimal AdjustedSalesQty,
            decimal OosUpliftQty,
            decimal NetDemandQty
        );

        // Pure Function (FM-01 + FM-02) — ตัวเดียวกับที่ NetDemandTests.cs เรียกใช้
        public static DemandResult Calculate(decimal? rawSalesQty, decimal? oosLostQty)
        {
            // ยอดขายดิบ: NULL -> 0 แล้วค่าติดลบปรับเป็น 0 (adjusted_sales_qty)
            decimal cleanRawSales = rawSalesQty ?? 0.0000m;
            decimal adjustedSalesQty = Math.Max(0.0000m, cleanRawSales);

            // ยอดที่เสียไปจากสินค้าหมด (OOS): NULL -> 0 แล้วค่าติดลบปรับเป็น 0 (oos_uplift_qty)
            decimal cleanOosLost = oosLostQty ?? 0.0000m;
            decimal oosUpliftQty = Math.Max(0.0000m, cleanOosLost);

            // ความต้องการสุทธิ = ยอดขายที่ปรับแล้ว + OOS Uplift
            decimal netDemandQty = adjustedSalesQty + oosUpliftQty;

            return new DemandResult(adjustedSalesQty, oosUpliftQty, netDemandQty);
        }

        // Entry point ที่ Program.cs เรียก: รับ connection ที่เปิดอยู่แล้วจากข้างนอก
        public static void Run(SqlConnection connection, string? targetPeriod, string? targetWarehouseCode)
        {
            // 1) ดึงข้อมูลดิบ 24 เดือนจาก Staging
            DataTable data = GetRawData(connection, targetPeriod, targetWarehouseCode);
            Log.Information("ดึงข้อมูลจาก DRP_STG_DRP_DEMAND_24M สำเร็จ {Rows} แถว", data.Rows.Count);

            // Guard: SaveToTarget ลบของเดิมแล้วเขียนใหม่ (ทุกคลัง = TRUNCATE) ถ้าไม่มีแถวเลยจะล้างตารางปลายทางจนว่าง
            if (data.Rows.Count == 0)
            {
                throw new InvalidOperationException($"ไม่พบข้อมูลใน DRP_STG_DRP_DEMAND_24M (Period={targetPeriod ?? "DEFAULT"}, Warehouse={targetWarehouseCode ?? "ALL"}) ยกเลิกการบันทึกเพื่อป้องกันการล้างตารางปลายทาง");
            }

            // 2) คำนวณ Net Demand รายแถว แล้วเติมเวลาคำนวณ (ทำในหน่วยความจำ ยังไม่แตะตารางปลายทาง)
            ApplyCalculations(data);
            AddCalculatedAtColumn(data);
            // 3) บันทึกลงตารางปลายทางแบบลบแล้วเขียนใหม่ใน Transaction เดียว
            SaveToTarget(connection, data, targetWarehouseCode);

            Log.Information("บันทึก Snapshot ลง DRP_FCT_SKU_MONTHLY_DEMAND_PREP สำเร็จ {Rows} แถว", data.Rows.Count);
        }

        private static DataTable GetRawData(SqlConnection connection, string? targetPeriod, string? targetWarehouseCode)
        {
            // @pTargetPeriod เป็น NULL -> ใช้งวดล่าสุดใน Staging | ช่วงข้อมูล = งวดนั้นย้อนหลัง 23 เดือน (รวม 24 งวด)
            // month_no = ลำดับเดือนต่อ SKU เรียงจากเก่าสุด (1) ถึงใหม่สุด (24)
            // หมายเหตุ: ROW_NUMBER นับจากแถวที่มีอยู่จริง ถ้า SKU ใดไม่มีข้อมูลบางเดือน เลข month_no จะไม่ตรงกับปฏิทิน (ถ้า Staging มีครบ 24 แถวต่อ SKU จะไม่มีปัญหานี้)
            string query = @"
            DECLARE @TargetPeriod VARCHAR(6) = @pTargetPeriod;
            IF @TargetPeriod IS NULL
                SELECT @TargetPeriod = MAX(period_year_month) FROM dbo.DRP_STG_DRP_DEMAND_24M;

            DECLARE @StartPeriod VARCHAR(6) =
                CONVERT(VARCHAR(6), DATEADD(MONTH, -23, CONVERT(DATE, @TargetPeriod + '01', 112)), 112);

            SELECT
                warehouse_code,
                item_id,
                period_year_month,
                ROW_NUMBER() OVER (
                    PARTITION BY warehouse_code, item_id
                    ORDER BY period_year_month ASC
                ) AS month_no,
                raw_sales_qty,
                oos_lost_qty
            FROM dbo.DRP_STG_DRP_DEMAND_24M
            WHERE period_year_month BETWEEN @StartPeriod AND @TargetPeriod
              AND (@pTargetWarehouseCode IS NULL OR warehouse_code = @pTargetWarehouseCode)
            ORDER BY warehouse_code, item_id, period_year_month;
        ";

            DataTable table = new DataTable();
            using SqlCommand command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@pTargetPeriod", (object?)targetPeriod ?? DBNull.Value);
            command.Parameters.AddWithValue("@pTargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

            using SqlDataReader reader = command.ExecuteReader();
            table.Load(reader);
            return table;
        }

        private static void ApplyCalculations(DataTable table)
        {
            // เพิ่มคอลัมน์ผลคำนวณ 3 ตัวลงใน DataTable
            table.Columns.Add("adjusted_sales_qty", typeof(decimal));
            table.Columns.Add("oos_uplift_qty", typeof(decimal));
            table.Columns.Add("net_demand_qty", typeof(decimal));

            foreach (DataRow row in table.Rows)
            {
                decimal? rawSales = row["raw_sales_qty"] is DBNull ? null : Convert.ToDecimal(row["raw_sales_qty"]);
                decimal? oosLost = row["oos_lost_qty"] is DBNull ? null : Convert.ToDecimal(row["oos_lost_qty"]);

                DemandResult result = Calculate(rawSales, oosLost);

                row["adjusted_sales_qty"] = result.AdjustedSalesQty;
                row["oos_uplift_qty"] = result.OosUpliftQty;
                row["net_demand_qty"] = result.NetDemandQty;
                // เขียน raw_sales_qty กลับเป็น 0 เมื่อเดิมเป็น NULL ไม่ให้ NULL หลุดไปตารางปลายทาง
                row["raw_sales_qty"] = rawSales ?? 0.0000m;
            }
        }

        private static void AddCalculatedAtColumn(DataTable table)
        {
            table.Columns.Add("calculated_at", typeof(DateTime));
            // ทุกแถวใช้เวลาเดียวกัน (เวลาที่รันครั้งนี้) เพื่อระบุได้ว่าเป็น Snapshot เดียวกัน
            DateTime now = DateTime.Now;
            foreach (DataRow row in table.Rows)
            {
                row["calculated_at"] = now;
            }
        }

        private static void SaveToTarget(SqlConnection connection, DataTable data, string? targetWarehouseCode)
        {
            const string targetTable = "DRP_FCT_SKU_MONTHLY_DEMAND_PREP";

            // Atomic: exception ลอยขึ้นไปที่ Main | Transaction ถูก Dispose ก่อน Commit จึง ROLLBACK อัตโนมัติ ป้องกัน Partial Writes
            using SqlTransaction transaction = connection.BeginTransaction();

            // ลบของเดิม: ทุกคลัง = TRUNCATE | ระบุคลัง = DELETE เฉพาะคลังนั้น (รันซ้ำกี่ครั้งผลก็เหมือนเดิม)
            string deleteSql = targetWarehouseCode is null
                ? $"TRUNCATE TABLE {targetTable};"
                : $"DELETE FROM {targetTable} WHERE warehouse_code = @pWarehouse;";

            using (SqlCommand deleteCommand = new SqlCommand(deleteSql, connection, transaction))
            {
                if (targetWarehouseCode is not null)
                    deleteCommand.Parameters.AddWithValue("@pWarehouse", targetWarehouseCode);
                deleteCommand.ExecuteNonQuery();
            }

            // เขียนข้อมูลใหม่ทั้งหมดแบบ Bulk (ชื่อคอลัมน์ต้นทาง = ปลายทาง)
            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = targetTable;
                bulkCopy.ColumnMappings.Add("warehouse_code", "warehouse_code");
                bulkCopy.ColumnMappings.Add("item_id", "item_id");
                bulkCopy.ColumnMappings.Add("month_no", "month_no");
                bulkCopy.ColumnMappings.Add("period_year_month", "period_year_month");
                bulkCopy.ColumnMappings.Add("raw_sales_qty", "raw_sales_qty");
                bulkCopy.ColumnMappings.Add("adjusted_sales_qty", "adjusted_sales_qty");
                bulkCopy.ColumnMappings.Add("oos_uplift_qty", "oos_uplift_qty");
                bulkCopy.ColumnMappings.Add("net_demand_qty", "net_demand_qty");
                bulkCopy.ColumnMappings.Add("calculated_at", "calculated_at");
                bulkCopy.WriteToServer(data);
            }

            // มาถึงบรรทัดนี้ = ลบและเขียนสำเร็จครบ จึงยืนยัน Transaction
            transaction.Commit();
        }
    }

}