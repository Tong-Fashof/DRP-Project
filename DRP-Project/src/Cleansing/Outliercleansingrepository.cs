using System.Data;
using Microsoft.Data.SqlClient;

namespace DRP_Project.src.Cleansing
{
	// PBI-08 (SCRUM-19): Outlier Cleansing Repository
	// DB I/O เท่านั้น ไม่มี Business Logic (การคำนวณอยู่ใน CleansedDatasetFinalizeEngine.cs)
	public static class OutlierCleansingRepository
	{
		// โหลดรหัสงวดปฏิทิน 24 งวด (month_no -> period_year_month) จาก Sliding 24M เดียวกับ Demand24M
		// ใช้ตารางต้นทางเดียวกับ PBI-02 (DRP_FCT_SKU_MONTHLY_DEMAND_PREP) เพื่อไม่ต้องคำนวณปฏิทินซ้ำและเสี่ยงคลาดเคลื่อน
		public static Dictionary<int, string> LoadPeriodMap(SqlConnection connection, string? targetWarehouseCode)
		{
			string query = @"
                SELECT DISTINCT month_no, period_year_month
                FROM DRP_FCT_SKU_MONTHLY_DEMAND_PREP
                WHERE (@pTargetWarehouseCode IS NULL OR warehouse_code = @pTargetWarehouseCode);
            ";

			var periodMap = new Dictionary<int, string>();

			using SqlCommand command = new SqlCommand(query, connection);
			command.Parameters.AddWithValue("@pTargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

			using SqlDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				int monthNo = Convert.ToInt32(reader["month_no"]);
				periodMap[monthNo] = reader["period_year_month"].ToString()!;   // ทุกคลังควรมีรหัสงวดตรงกันในแต่ละ month_no
			}

			return periodMap;
		}

		// Entry point ที่ RunFinalize เรียก: บันทึกประวัติการคลีนซิ่งรายเดือน (24 แถว/SKU) ลงตารางปลายทาง
		// DEC-PBI08-01 (Idempotency & Atomic Transaction Policy):
		// ลบของเดิมตามคลังเป้าหมาย (NULL = ทุกคลัง) -> SqlBulkCopy -> COMMIT ภายใน Transaction เดียว
		// เกิดข้อผิดพลาดใดๆ -> ROLLBACK ทันที ป้องกัน Partial Writes
		public static void Save(SqlConnection connection, List<SkuOutlierCleansingRow> rows, string? targetWarehouseCode)
		{
			const string targetTable = "AxWebReport_UAT.dbo.DRP_FCT_SKU_OUTLIER_CLEANSING";

			DataTable data = BuildDataTable(rows);

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
					bulkCopy.ColumnMappings.Add("outlier_method", "outlier_method");
					bulkCopy.ColumnMappings.Add("outlier_status", "outlier_status");
					bulkCopy.ColumnMappings.Add("original_demand_qty", "original_demand_qty");
					bulkCopy.ColumnMappings.Add("cleansed_demand_qty", "cleansed_demand_qty");
					bulkCopy.ColumnMappings.Add("qty_reduced", "qty_reduced");
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

		// แปลง List<SkuOutlierCleansingRow> เป็น DataTable
		// ตั้งชื่อคอลัมน์ตรงกับตารางปลายทางเลย เพื่อให้ ColumnMappings ด้านบนเป็นแค่ชื่อเดียวกันซ้ำสองฝั่ง
		private static DataTable BuildDataTable(List<SkuOutlierCleansingRow> rows)
		{
			DataTable table = new DataTable();
			table.Columns.Add("warehouse_code", typeof(string));
			table.Columns.Add("item_id", typeof(string));
			table.Columns.Add("month_no", typeof(int));
			table.Columns.Add("period_year_month", typeof(string));
			table.Columns.Add("outlier_method", typeof(string));
			table.Columns.Add("outlier_status", typeof(string));
			table.Columns.Add("original_demand_qty", typeof(decimal));
			table.Columns.Add("cleansed_demand_qty", typeof(decimal));
			table.Columns.Add("qty_reduced", typeof(decimal));
			table.Columns.Add("calculated_at", typeof(DateTime));

			foreach (SkuOutlierCleansingRow row in rows)
			{
				table.Rows.Add(
					row.WarehouseCode, row.ItemId, row.MonthNo, row.PeriodYearMonth,
					row.OutlierMethod, row.OutlierStatus, row.OriginalDemandQty,
					row.CleansedDemandQty, row.QtyReduced, row.CalculatedAt);
			}

			return table;
		}
	}
}