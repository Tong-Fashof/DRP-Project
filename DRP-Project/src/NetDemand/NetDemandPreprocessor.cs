using System.Data;
using Microsoft.Data.SqlClient;

namespace DRP_Project.src.NetDemand
{
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
            decimal cleanRawSales = rawSalesQty ?? 0.0000m;
            decimal adjustedSalesQty = Math.Max(0.0000m, cleanRawSales);

            decimal cleanOosLost = oosLostQty ?? 0.0000m;
            decimal oosUpliftQty = Math.Max(0.0000m, cleanOosLost);

            decimal netDemandQty = adjustedSalesQty + oosUpliftQty;

            return new DemandResult(adjustedSalesQty, oosUpliftQty, netDemandQty);
        }

        // Entry point ที่ Program.cs เรียก: รับ connection ที่เปิดอยู่แล้วจากข้างนอก
        public static void Run(SqlConnection connection, string? targetPeriod, string? targetWarehouseCode)
        {
            DataTable data = GetRawData(connection, targetPeriod, targetWarehouseCode);
            Console.WriteLine($"[NetDemand] ดึงข้อมูลสำเร็จ: {data.Rows.Count} แถว");

            ApplyCalculations(data);
            AddCalculatedAtColumn(data);
            SaveToTarget(connection, data, targetWarehouseCode);

            Console.WriteLine("[NetDemand] บันทึก Snapshot สำเร็จ");
        }

        private static DataTable GetRawData(SqlConnection connection, string? targetPeriod, string? targetWarehouseCode)
        {
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
                row["raw_sales_qty"] = rawSales ?? 0.0000m;
            }
        }

        private static void AddCalculatedAtColumn(DataTable table)
        {
            table.Columns.Add("calculated_at", typeof(DateTime));
            DateTime now = DateTime.Now;
            foreach (DataRow row in table.Rows)
            {
                row["calculated_at"] = now;
            }
        }

        private static void SaveToTarget(SqlConnection connection, DataTable data, string? targetWarehouseCode)
        {
            const string targetTable = "DRP_FCT_SKU_MONTHLY_DEMAND_PREP";

            using SqlTransaction transaction = connection.BeginTransaction();
            try
            {
                string deleteSql = targetWarehouseCode is null
                    ? $"TRUNCATE TABLE {targetTable};"
                    : $"DELETE FROM {targetTable} WHERE warehouse_code = @pWarehouse;";

                using (SqlCommand deleteCommand = new SqlCommand(deleteSql, connection, transaction))
                {
                    if (targetWarehouseCode is not null)
                        deleteCommand.Parameters.AddWithValue("@pWarehouse", targetWarehouseCode);
                    deleteCommand.ExecuteNonQuery();
                }

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

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

}
