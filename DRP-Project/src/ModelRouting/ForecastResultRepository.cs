using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Serilog;

namespace DRP_Project.src.ModelRouting
{

    // PBI-12: Forecast Result Repository (DB I/O เท่านั้น ไม่มี Business Logic | ไม่ catch Exception)
    // Save: ใน Transaction เดียว
    //   1) ลบของเดิมตาม warehouse_code ที่มีในชุดข้อมูล (Idempotency)
    //   2) SqlBulkCopy 72 คอลัมน์ ลง DRP_FCT_SKU_FORECAST_ROUTING_RESULT
    //   3) ซิงค์ outlier_status 13 ค่า x 24 งวด/SKU ลง DRP_FCT_SKU_OUTLIER_CLEANSING (Bulk ลง #temp แล้ว UPDATE JOIN)
    // เกิดข้อผิดพลาดใดๆ -> exception ลอยขึ้น Main | Transaction ถูก Dispose ก่อน Commit จึง ROLLBACK อัตโนมัติ
    public static class ForecastResultRepository
    {
        private const string ResultTable = "AxWebReport_UAT.dbo.DRP_FCT_SKU_FORECAST_ROUTING_RESULT";
        private const string CleansingTable = "AxWebReport_UAT.dbo.DRP_FCT_SKU_OUTLIER_CLEANSING";

        // 72 คอลัมน์ครบถ้วนตามโครงสร้างจริงของตาราง DRP_FCT_SKU_FORECAST_ROUTING_RESULT
        private static readonly (string Name, Type Type, Func<SkuForecastResult, object?> Get)[] Columns =
        {
            // กลุ่ม 1: Identity (3 คอลัมน์)
            ("warehouse_code", typeof(string), r => r.WarehouseCode),
            ("item_id", typeof(string), r => r.ItemId),
            ("as_of_period", typeof(string), r => r.AsOfPeriod),

            // กลุ่ม 2: Totals (2 คอลัมน์)
            ("raw_total_24m", typeof(decimal), r => r.RawTotal24M),
            ("oos_total_24m", typeof(decimal), r => r.OosTotal24M),

            // กลุ่ม 3: Basic & Distribution Stats (14 คอลัมน์)
            ("raw_avg", typeof(decimal), r => r.RawAvg),
            ("raw_sd", typeof(decimal), r => r.RawSd),
            ("raw_cv", typeof(decimal), r => r.RawCv),
            ("sigma_used", typeof(int), r => (object?)r.SigmaUsed),   // null -> DBNull (CK_ROUTING_SIGMA_USED ไม่ยอมรับ 0)
            ("raw_ttl_mth", typeof(int), r => r.RawTtlMth),
            ("raw_active_mth", typeof(int), r => r.RawActiveMth),
            ("raw_adi", typeof(decimal), r => (object?)r.RawAdi),
            ("raw_skewness", typeof(decimal), r => r.RawSkewness),
            ("raw_kurtosis", typeof(decimal), r => r.RawKurtosis),
            ("jb_statistic", typeof(decimal), r => r.JbStatistic),
            ("jb_p_value", typeof(decimal), r => r.JbPValue),
            ("demand_distribution", typeof(string), r => r.DemandDistribution),
            ("outlier_method", typeof(string), r => r.OutlierMethod),
            ("selected_outlier_tool", typeof(string), r => r.SelectedOutlierTool),

            // กลุ่ม 4: Caps (11 คอลัมน์)
            ("lower_2sigma", typeof(decimal), r => r.Lower2Sigma),
            ("upper_2sigma", typeof(decimal), r => r.Upper2Sigma),
            ("lower_3sigma", typeof(decimal), r => r.Lower3Sigma),
            ("upper_3sigma", typeof(decimal), r => r.Upper3Sigma),
            ("q1", typeof(decimal), r => r.Q1),
            ("q3", typeof(decimal), r => r.Q3),
            ("iqr", typeof(decimal), r => r.Iqr),
            ("lower_15iqr", typeof(decimal), r => r.Lower15Iqr),
            ("upper_15iqr", typeof(decimal), r => r.Upper15Iqr),
            ("lower_3iqr", typeof(decimal), r => r.Lower3Iqr),
            ("upper_3iqr", typeof(decimal), r => r.Upper3Iqr),

            // กลุ่ม 5: Outlier Summary & Action (6 คอลัมน์)
            ("warning_count", typeof(int), r => r.WarningCount),
            ("extreme_count", typeof(int), r => r.ExtremeCount),
            ("high_outlier_count", typeof(int), r => r.HighOutlierCount),
            ("low_outlier_count", typeof(int), r => r.LowOutlierCount),
            ("outlier_review_status", typeof(string), r => r.OutlierReviewStatus),
            ("action", typeof(string), r => r.Action),

            // กลุ่ม 6: Checks & Ceiling (4 คอลัมน์)
            ("sales_row", typeof(int), r => r.SalesRow),
            ("oos_row", typeof(int), r => r.OosRow),
            ("source_check", typeof(string), r => r.SourceCheck),
            ("selected_upper_cap", typeof(decimal), r => r.SelectedUpperCap),

            // กลุ่ม 7: Trend & Seasonality (8 คอลัมน์)
            ("raw_trend_r", typeof(decimal), r => r.RawTrendR),
            ("raw_lag12_r", typeof(decimal), r => r.RawLag12R),
            ("raw_year1_trend_r", typeof(decimal), r => r.RawYear1TrendR),
            ("raw_year2_trend_r", typeof(decimal), r => r.RawYear2TrendR),
            ("raw_detrended_lag12_r", typeof(decimal), r => r.RawDetrendedLag12R),
            ("negative_months_count", typeof(int), r => r.NegativeMonthsCount),
            ("outlier_selection_reason", typeof(string), r => r.OutlierSelectionReason),
            ("capped_high_months_count", typeof(int), r => r.CappedHighMonthsCount),

            // กลุ่ม 8: Clean Stats & Behavior (11 คอลัมน์)
            ("clean_ttl_mth", typeof(int), r => r.CleanTtlMth),
            ("clean_active_mth", typeof(int), r => r.CleanActiveMth),
            ("clean_adi", typeof(decimal), r => (object?)r.CleanAdi),
            ("clean_mean", typeof(decimal), r => r.CleanMean),
            ("clean_sd", typeof(decimal), r => r.CleanSd),
            ("clean_cv", typeof(decimal), r => r.CleanCv),
            ("clean_cv2", typeof(decimal), r => r.CleanCv2),
            ("detect_trend", typeof(decimal), r => (object?)r.DetectTrend),   // DB = NUMERIC(18,4) NULL
            ("clean_detrended_seasonal_r", typeof(decimal), r => r.CleanDetrendedSeasonalR),
            ("demand_group", typeof(string), r => r.DemandGroup),
            ("demand_pattern", typeof(string), r => r.DemandPattern),

            // กลุ่ม 9: Model Routing & XYZ (7 คอลัมน์)
            ("assigned_model_id", typeof(string), r => r.AssignedModelId),
            ("xyz_class", typeof(string), r => r.XyzClass),
            ("cv_eligible_xyz", typeof(decimal), r => r.CvEligibleXyz),
            ("cv_percentile", typeof(decimal), r => r.CvPercentile),
            ("raw_row_ref", typeof(int), r => r.RawRowRef),
            ("applied_outlier_tool", typeof(string), r => r.AppliedOutlierTool),
            ("qty_reduced_by_cap", typeof(decimal), r => r.QtyReducedByCap),

            // กลุ่ม 10: Reason & Post-Clean Correlation (4 คอลัมน์)
            ("selection_reason", typeof(string), r => r.SelectionReason),
            ("clean_lag12_r", typeof(decimal), r => r.CleanLag12R),
            ("clean_year1_trend_r", typeof(decimal), r => r.CleanYear1TrendR),
            ("clean_year2_trend_r", typeof(decimal), r => r.CleanYear2TrendR),

            // กลุ่ม 11: Audit (2 คอลัมน์)
            ("calculated_at", typeof(DateTime), r => r.CalculatedAt),
            ("created_at", typeof(DateTime), r => r.CreatedAt),
        };

        // ตรวจสอบ Schema ของ DataTable กับตารางจริงใน DB ก่อนบันทึก (ไม่ตรงกัน = throw)
        public static void ValidateSchema(SqlConnection connection, DataTable resultData)
        {
            var dbColumns = new Dictionary<string, (string DataType, int MaxLength)>(StringComparer.OrdinalIgnoreCase);

            string sql = @"
                SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
                FROM AxWebReport_UAT.INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'DRP_FCT_SKU_FORECAST_ROUTING_RESULT';";

            using (var cmd = new SqlCommand(sql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string colName = reader.GetString(0);
                    string dataType = reader.GetString(1);
                    int maxLen = reader.IsDBNull(2) ? -1 : reader.GetInt32(2);
                    dbColumns[colName] = (dataType, maxLen);
                }
            }

            if (dbColumns.Count == 0)
                throw new InvalidOperationException($"ไม่พบข้อมูล Schema ของตาราง {ResultTable}");

            var errors = new List<string>();

            foreach (DataColumn col in resultData.Columns)
            {
                if (!dbColumns.ContainsKey(col.ColumnName))
                {
                    errors.Add($"- ไม่พบคอลัมน์ '{col.ColumnName}' ในตารางปลายทาง");
                    continue;
                }

                var (dbType, maxLen) = dbColumns[col.ColumnName];
                if (col.DataType == typeof(string) && maxLen > 0)
                {
                    int maxDataLen = 0;
                    foreach (DataRow row in resultData.Rows)
                    {
                        if (row[col] != DBNull.Value)
                        {
                            int len = row[col].ToString()?.Length ?? 0;
                            if (len > maxDataLen) maxDataLen = len;
                        }
                    }

                    if (maxDataLen > maxLen)
                    {
                        errors.Add($"- คอลัมน์ '{col.ColumnName}' ข้อมูลจริงยาว {maxDataLen} ตัวอักษร เกินขนาดที่ DB กำหนด ({dbType}({maxLen}))");
                    }
                }
            }

            if (errors.Count > 0)
            {
                string detail = string.Join(Environment.NewLine, errors);
                throw new InvalidOperationException($"พบความไม่สอดคล้องของ Schema ก่อนการบันทึก:{Environment.NewLine}{detail}");
            }
        }

        public static void Save(SqlConnection connection, List<SkuForecastResult> results)
        {
            if (Columns.Length != 72)
            {
                throw new InvalidOperationException($"จำนวนคอลัมน์ต้องเป็น 72 (พบ {Columns.Length})");
            }

            // เตรียมข้อมูลก่อนเปิด Transaction (ไม่ถือ Transaction นานโดยไม่จำเป็น)
            DataTable resultData = BuildResultTable(results);
            DataTable statusData = BuildStatusTable(results);

            ValidateSchema(connection, resultData);

            string[] warehouses = results.Select(r => r.WarehouseCode).Distinct().ToArray();

            using SqlTransaction transaction = connection.BeginTransaction();

            // 1) ลบของเดิมตามคลังที่มีในชุดข้อมูล (รันซ้ำกี่ครั้งผลก็เหมือนเดิม ไม่ใช้ TRUNCATE)
            foreach (string warehouse in warehouses)
            {
                using SqlCommand delete = new SqlCommand($"DELETE FROM {ResultTable} WHERE warehouse_code = @pWarehouse;", connection, transaction);
                delete.Parameters.AddWithValue("@pWarehouse", warehouse);
                delete.ExecuteNonQuery();
            }

            // 2) Bulk Insert 72 คอลัมน์
            using (SqlBulkCopy bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers, transaction))
            {
                bulk.DestinationTableName = ResultTable;
                bulk.BulkCopyTimeout = 0;
                foreach (var col in Columns) bulk.ColumnMappings.Add(col.Name, col.Name);
                bulk.WriteToServer(resultData);
            }

            // 3) ซิงค์ outlier_status: Bulk ลง #temp แล้ว UPDATE JOIN (COLLATE DATABASE_DEFAULT กัน Collation Conflict กับ tempdb)
            // #temp อยู่ถึงสิ้นสุด Session (Connection เดียวกัน) ไม่ใช่สิ้นสุด Transaction/การเรียก Save
            // ถ้าเรียก Save ซ้ำบน Connection เดียว (เช่น ชุดทดสอบ SIT ที่วนเรียกทีละคลัง) -> ต้อง DROP ก่อนสร้างทุกครั้ง ไม่งั้น error "There is already an object named '#OutlierStatusStage'"
            // ใช้ OBJECT_ID แทน DROP TABLE IF EXISTS เพื่อรองรับ SQL Server ทุกเวอร์ชัน
            using (SqlCommand create = new SqlCommand(@"
                IF OBJECT_ID('tempdb..#OutlierStatusStage') IS NOT NULL DROP TABLE #OutlierStatusStage;
                CREATE TABLE #OutlierStatusStage (
                    warehouse_code VARCHAR(20) COLLATE DATABASE_DEFAULT NOT NULL,
                    item_id VARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL,
                    month_no INT NOT NULL,
                    outlier_status VARCHAR(30) COLLATE DATABASE_DEFAULT NOT NULL);", connection, transaction))
            {
                create.ExecuteNonQuery();
            }

            using (SqlBulkCopy bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulk.DestinationTableName = "#OutlierStatusStage";
                bulk.BulkCopyTimeout = 0;
                foreach (DataColumn c in statusData.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                bulk.WriteToServer(statusData);
            }

            int updated;
            using (SqlCommand update = new SqlCommand($@"
                UPDATE t SET t.outlier_status = s.outlier_status
                FROM {CleansingTable} t
                JOIN #OutlierStatusStage s
                  ON s.warehouse_code = t.warehouse_code AND s.item_id = t.item_id AND s.month_no = t.month_no;", connection, transaction))
            {
                update.CommandTimeout = 0;
                updated = update.ExecuteNonQuery();
            }

            // ทิ้ง #temp ทันทีที่ใช้เสร็จ (อยู่ใน Transaction เดียวกัน: ถ้า ROLLBACK การ DROP ก็ถูกย้อนด้วย และ DROP ก่อนสร้างด้านบนกันซ้ำอยู่แล้ว)
            using (SqlCommand drop = new SqlCommand("DROP TABLE #OutlierStatusStage;", connection, transaction))
            {
                drop.ExecuteNonQuery();
            }

            // ต้องอัปเดตครบ 24 งวดทุก SKU: ขาด/เกิน = ข้อมูลสองตารางไม่ตรงกัน -> throw (ROLLBACK ทั้งหมด)
            if (updated != statusData.Rows.Count)
                throw new InvalidOperationException($"ซิงค์ outlier_status ไม่ครบ: อัปเดต {updated} แถว จากที่ต้องการ {statusData.Rows.Count} แถว");

            transaction.Commit();
        }

        // 1 SKU = 1 แถว (72 คอลัมน์) | null -> DBNull.Value
        private static DataTable BuildResultTable(List<SkuForecastResult> results)
        {
            DataTable table = new DataTable();
            foreach (var col in Columns) table.Columns.Add(col.Name, col.Type);

            foreach (SkuForecastResult r in results)
            {
                object[] values = new object[Columns.Length];
                for (int i = 0; i < Columns.Length; i++)
                    values[i] = Columns[i].Get(r) ?? DBNull.Value;
                table.Rows.Add(values);
            }
            return table;
        }

        // 1 SKU = 24 แถว (month_no 1..24 = index 0..23 ของ OutlierStatus24M)
        private static DataTable BuildStatusTable(List<SkuForecastResult> results)
        {
            DataTable table = new DataTable();
            table.Columns.Add("warehouse_code", typeof(string));
            table.Columns.Add("item_id", typeof(string));
            table.Columns.Add("month_no", typeof(int));
            table.Columns.Add("outlier_status", typeof(string));

            foreach (SkuForecastResult r in results)
            {
                if (r.OutlierStatus24M == null || r.OutlierStatus24M.Length != 24 || r.OutlierStatus24M.Any(string.IsNullOrEmpty))
                    throw new InvalidOperationException($"OutlierStatus24M ต้องมี 24 งวดและไม่ว่าง ({r.WarehouseCode}|{r.ItemId})");

                for (int i = 0; i < 24; i++)
                    table.Rows.Add(r.WarehouseCode, r.ItemId, i + 1, r.OutlierStatus24M[i]);
            }
            return table;
        }

        // ส่งออกข้อมูลจริงทั้ง 72 คอลัมน์เป็นไฟล์ CSV เพื่อตรวจสอบก่อนบันทึกลง DB จริง
        public static string ExportPreviewCsv(List<SkuForecastResult> results)
        {
            DataTable resultData = BuildResultTable(results);
            string previewDir = Path.Combine(AppContext.BaseDirectory, "previews");
            Directory.CreateDirectory(previewDir);

            string previewPath = Path.Combine(previewDir, $"preview_routing_result_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using (var writer = new StreamWriter(previewPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(string.Join(",", resultData.Columns.Cast<DataColumn>().Select(c => c.ColumnName)));

                foreach (DataRow row in resultData.Rows)
                {
                    var csvValues = resultData.Columns.Cast<DataColumn>().Select(col =>
                    {
                        string str = row[col] == DBNull.Value ? "NULL" : row[col].ToString() ?? "";
                        return $"\"{str.Replace("\"", "\"\"")}\"";
                    });
                    writer.WriteLine(string.Join(",", csvValues));
                }
            }
            return previewPath;
        }
    }
}