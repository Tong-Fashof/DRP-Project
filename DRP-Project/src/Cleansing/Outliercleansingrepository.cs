using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.Cleansing
{
    // PBI-08 (SCRUM-19): Outlier Cleansing Repository
    // DB I/O เท่านั้น ไม่มี Business Logic (การคำนวณอยู่ใน Engine) | log เฉพาะ Warning ของข้อมูล ไม่ catch Exception
    public static class OutlierCleansingRepository
    {
        // อ่านค่าตัวเลขจาก M_LOV (LOV_CODE_1 = 'DRP_CONFIG', LOV_CODE_2 = lovCode2, REC_STATUS = 'A' หรือ NULL)
        // ไม่พบแถวหรือแปลงค่าไม่ได้ -> บันทึก Warning ลง log แล้วใช้ defaultValue
        public static decimal LoadLovDecimal(SqlConnection connection, string lovCode2, decimal defaultValue)
        {
            string sql = @"
				SELECT TOP (1) LOV_VALUE
				FROM AxWebReport_UAT.dbo.M_LOV
				WHERE LOV_CODE_1 = 'DRP_CONFIG'
				  AND LOV_CODE_2 = @LovCode2
				  AND (REC_STATUS = 'A' OR REC_STATUS IS NULL);";

            using SqlCommand cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@LovCode2", lovCode2);
            object? result = cmd.ExecuteScalar();

            if (result == null || result == DBNull.Value)
            {
                Log.Warning("ไม่พบ {LovCode} ใน M_LOV ใช้ค่า Default = {DefaultValue}", lovCode2, defaultValue);
                return defaultValue;
            }

            // แปลงด้วย InvariantCulture เพื่อให้ "0.05" อ่านถูกต้องไม่ขึ้นกับภาษาของเครื่อง
            string text = Convert.ToString(result, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            {
                Log.Warning("แปลงค่า {LovCode} = '{Text}' ไม่ได้ ใช้ค่า Default = {DefaultValue}", lovCode2, text, defaultValue);
                return defaultValue;
            }

            return value;
        }


        // ฟังก์ชันสำหรับโหลดข้อมูล SKU และปริมาณความต้องการ 24 เดือน
        public static List<SkuCleansingCandidate> LoadCandidates(SqlConnection connection, string? targetWarehouseCode)
        {
            string sql = @"
			SELECT 
				warehouse_code,
				item_id,
				month_no,
				net_demand_qty
			FROM DRP_FCT_SKU_MONTHLY_DEMAND_PREP
			WHERE (@TargetWarehouseCode IS NULL OR warehouse_code = @TargetWarehouseCode)";

            // สร้าง Dictionary เพื่อเก็บข้อมูล SKU และปริมาณความต้องการ 24 เดือน จาก Query
            var map = new Dictionary<string, SkuCleansingCandidate>();

            using SqlCommand cmd = new SqlCommand(sql, connection);

            // กำหนดค่า Parameter สำหรับ TargetWarehouseCode
            cmd.Parameters.AddWithValue("@TargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

            int skippedMonthOutOfRange = 0;   // แถวที่ month_no นอกช่วง 1-24 (ถูกข้าม)
            int nullDemandRows = 0;           // แถวที่ net_demand_qty เป็น NULL (นับเป็น 0)

            using SqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string warehouse_code = reader["warehouse_code"].ToString()!;
                string item_id = reader["item_id"].ToString()!;
                int month_no = Convert.ToInt32(reader["month_no"]);
                bool isNullDemand = reader["net_demand_qty"] == DBNull.Value;
                decimal net_demand_qty = isNullDemand ? 0m : Convert.ToDecimal(reader["net_demand_qty"]);

                if (month_no < 1 || month_no > 24)                  // ตรวจสอบ monthNo อยู่ในช่วง 1-24
                {
                    skippedMonthOutOfRange++;
                    continue;
                }
                if (isNullDemand) nullDemandRows++;
                string key = warehouse_code + "|" + item_id;        // ใช้ WarehouseCode และ ItemId เป็น Key
                if (!map.ContainsKey(key))                          // ตรวจสอบว่ามี Key อยู่ใน Dictionary (ตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP) หรือไม่ ถ้าไม่มีให้สร้าง SkuCleansingCandidate ใหม่
                {
                    map[key] = new SkuCleansingCandidate { warehouse_code = warehouse_code, item_id = item_id };
                }

                // ยอดความต้องการแท้จริง 24 เดือนและการประกอบอาเรย์(Array Population): ดึงชุดข้อมูล net_demand_qty เรียงตาม month_no(1..24) รายคลังและราย SKU จากตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP 
                // โดยสร้างอาเรย์ decimal[24] กำหนดค่าเริ่มต้นเป็น 0.0000m ทั้งหมด แล้วแมปข้อมูลตามตำแหน่ง Demand24M[month_no - 1] = net_demand_qty เพื่อให้มั่นใจว่าอาเรย์มีขนาด 24 งวดสมบูรณ์ 100 % แม้บางงวดจะไม่มีข้อมูลในฐานข้อมูล

                // เพิ่มค่า net_demand_qty ลงใน Demand24M ของ SkuCleansingCandidate ตาม month_no
                map[key].Demand24M[month_no - 1] += net_demand_qty;
            }

            // สรุปครั้งเดียวต่อการโหลด (ไม่ log ทีละแถว)
            Log.Information("โหลด SKU Candidates {Count} รายการ", map.Count);
            if (map.Count == 0)
                Log.Warning("ไม่พบข้อมูลใน DRP_FCT_SKU_MONTHLY_DEMAND_PREP ตามเงื่อนไข Warehouse={Warehouse}", targetWarehouseCode ?? "ALL");
            if (skippedMonthOutOfRange > 0)
                Log.Warning("ข้ามแถวที่ month_no นอกช่วง 1-24 จำนวน {Count} แถว", skippedMonthOutOfRange);
            if (nullDemandRows > 0)
                Log.Warning("พบ net_demand_qty เป็น NULL (นับเป็น 0) จำนวน {Count} แถว", nullDemandRows);

            return map.Values.ToList();
        }

        // โหลดรหัสงวดปฏิทิน 24 งวด (month_no -> period_year_month) จาก Sliding 24M เดียวกับ Demand24M
        // ใช้ตารางต้นทางเดียวกับ PBI-02 (DRP_FCT_SKU_MONTHLY_DEMAND_PREP) เพื่อไม่ต้องคำนวณปฏิทินซ้ำและเสี่ยงคลาดเคลื่อน
        public static Dictionary<int, string> LoadPeriodMap(SqlConnection connection, string? targetWarehouseCode)
        {
            // ผลลัพธ์ควรมี 24 แถว (month_no 1..24) | DISTINCT เพราะตารางต้นทางมีหลายแถวต่อ SKU ที่ month_no เดียวกัน
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
        // เกิดข้อผิดพลาดใดๆ -> exception ลอยขึ้นไปที่ Main | Transaction ถูก Dispose ก่อน Commit จึง ROLLBACK อัตโนมัติ ป้องกัน Partial Writes
        public static void Save(SqlConnection connection, List<SkuOutlierCleansingRow> rows, string? targetWarehouseCode)
        {
            const string targetTable = "AxWebReport_UAT.dbo.DRP_FCT_SKU_OUTLIER_CLEANSING";

            // 1) แปลงเป็น DataTable ก่อนเปิด Transaction (ไม่ถือ Transaction นานโดยไม่จำเป็น)
            DataTable data = BuildDataTable(rows);

            // 2) เปิด Transaction: ถ้าเกิด exception ก่อนถึง Commit() การ Dispose ของ using จะ ROLLBACK ให้อัตโนมัติ (จึงไม่ต้องมี try-catch)
            // TRUNCATE ใน SQL Server ย้อนกลับได้เมื่ออยู่ใน Transaction
            using SqlTransaction transaction = connection.BeginTransaction();

            // 3) ลบของเดิม: ทุกคลัง = TRUNCATE | ระบุคลัง = DELETE เฉพาะคลังนั้น (รันซ้ำกี่ครั้งผลก็เหมือนเดิม)
            string deleteSql = targetWarehouseCode is null
                ? $"TRUNCATE TABLE {targetTable};"
                : $"DELETE FROM {targetTable} WHERE warehouse_code = @pWarehouse;";

            using (SqlCommand deleteCommand = new SqlCommand(deleteSql, connection, transaction))
            {
                if (targetWarehouseCode is not null)
                    deleteCommand.Parameters.AddWithValue("@pWarehouse", targetWarehouseCode);
                deleteCommand.ExecuteNonQuery();
            }

            // 4) เขียนข้อมูลใหม่ทั้งหมดแบบ Bulk (ชื่อคอลัมน์ต้นทาง = ปลายทาง)
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

            // 5) มาถึงบรรทัดนี้ = ลบและเขียนสำเร็จครบ จึงยืนยัน Transaction
            transaction.Commit();
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