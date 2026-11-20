using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace DRP_Project.src.Ingestion;

/// <summary>
/// Production database repository for PBI-01 (Stage 0 Ingestion).
///   - Source (read-only, เปิด Connection แยกต่อการอ่านแต่ละครั้ง ไม่อยู่ใน Transaction):
///       SQL Server : INVENTTABLE, SALESORDER, CUSTTABLE, SALESPFN, fashof_data.dbo.BRANCH
///       MariaDB    : md_wishlist, customers (ผ่าน MySqlConnector ตรง ไม่ใช้ Linked Server)
///   - Target (DRP DB: DRP_MAS_ITEM & DRP_STG_DRP_DEMAND_24M): SqlConnection + SqlTransaction + SqlBulkCopy
/// ยอดขาย/OOS ถูกรวม (GROUP BY) ที่ฝั่ง Source เป็นรายเดือนก่อนส่งเข้า Memory: ผลลัพธ์เท่าเดิมกับการคำนวณทีละแถว
/// เพราะ Engine คูณ Factor แบบเชิงเส้น (SUM(qty) x factor = SUM(qty x factor)) และลดจำนวนแถวจากหลายสิบล้านเหลือหลักแสน
/// </summary>
public class SqlDrpIngestionRepository : IDrpIngestionRepository
{
    private const int SourceCommandTimeoutSeconds = 900;    // Query ฝั่ง Source (รวมยอด 25 เดือน)
    private const int CommandTimeoutSeconds = 1800;         // DELETE / MERGE / GROUP BY / BulkCopy ฝั่ง Target (Init ~10.5M แถว)
    private const int BulkChunkSize = 100_000;              // จำนวนแถวต่อก้อนที่ส่งเข้า SqlBulkCopy (จำกัด Memory ของ DataTable)

    private readonly SqlConnection _targetConnection;
    private readonly string? _sourceSqlConnectionString;
    private readonly string? _sourceMariaDbConnectionString;
    private SqlTransaction? _activeTransaction;
    private List<RawInventTableRow>? _inventTableCache;     // อ่าน INVENTTABLE ครั้งเดียวต่อการรัน (ใช้ทั้ง Master และ Factor)

    // ใช้กับ Target เท่านั้น (Source ไม่ถูกตั้งค่า: เมธอดฝั่ง Source จะ throw ข้อความชัดเจน)
    public SqlDrpIngestionRepository(SqlConnection targetConnection)
        : this(targetConnection, null, null)
    {
    }

    public SqlDrpIngestionRepository(SqlConnection targetConnection, IngestionSourceOptions sourceOptions)
        : this(targetConnection, sourceOptions.SqlServerConnectionString, sourceOptions.MariaDbConnectionString)
    {
    }

    private SqlDrpIngestionRepository(SqlConnection targetConnection, string? sourceSqlConnectionString, string? sourceMariaDbConnectionString)
    {
        _targetConnection = targetConnection;
        _sourceSqlConnectionString = sourceSqlConnectionString;
        _sourceMariaDbConnectionString = sourceMariaDbConnectionString;
    }

    // ====================================================================================================
    // 1. EXTERNAL SOURCE EXTRACTION (SQL Server: ตารางขาย + Master | MariaDB: OOS)
    // ====================================================================================================

    /// <summary>
    /// INVENTTABLE ทั้งหมดไม่มีเงื่อนไขกรองสถานะ (DEC-PBI01-07 Zero Discard) คืนค่า Factor "ดิบ" (NULL/0 ไม่ถูกแปลง)
    /// เพื่อให้ Engine บันทึก Warning Log ได้ | การ Fallback เป็น 1.0 ทำที่ Engine (ไม่ใช่ที่นี่)
    /// INVENTTABLE เป็น Multi-Company (Ax): ITEMID เดียวกันมีได้หลายแถวคนละ COMPANY (พบจริง: PMH, PFN)
    /// พร้อม FACTOR_SALESTOBASEUNIT คนละค่า -> ดึง COMPANY มาด้วยเสมอ ไม่กรองทิ้งที่นี่
    /// (Engine เป็นผู้ตัดสินว่าจะใช้ Factor ของ Company ไหนตามแหล่งที่มาของยอดขาย)
    /// </summary>
    public IReadOnlyList<RawInventTableRow> FetchSourceInventTable()
    {
        if (_inventTableCache != null) return _inventTableCache;

        const string query = @"
            SELECT ITEMID, ITEMNAME, PRODUCTLINE, FACTOR_SALESTOBASEUNIT, COMPANY
            FROM dbo.INVENTTABLE;";

        var rows = new List<RawInventTableRow>();
        using SqlConnection connection = OpenSourceSqlConnection();
        using SqlCommand command = new SqlCommand(query, connection) { CommandTimeout = SourceCommandTimeoutSeconds };
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new RawInventTableRow(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadDecimal(reader, 3),
                ReadString(reader, 4)));
        }

        _inventTableCache = rows;
        return rows;
    }

    /// <summary>
    /// ตัวคูณแปลงหน่วย SKU -> Factor (รวมทุก Company เป็น Dictionary เดียว, Company = 'PMH' ชนะถ้าซ้ำ)
    /// NULL / 0 / ติดลบ -> 1.0
    /// หมายเหตุ: Engine ไม่เรียกใช้เมธอดนี้แล้ว (คำนวณ Factor แยกตาม Company เองจาก FetchSourceInventTable โดยตรง
    /// เพื่อรองรับกรณี SALESORDER ใช้ Factor ฝั่ง PMH ส่วน SALESPFN ใช้ฝั่ง PFN) คงไว้เพื่อความเข้ากันได้ของ Interface/เทสต์เดิมเท่านั้น
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetSourceConversionFactors()
    {
        var factors = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in FetchSourceInventTable())
        {
            if (string.IsNullOrWhiteSpace(row.ItemId)) continue;
            var cleanItemId = row.ItemId.Trim();
            var cleanCompany = (row.Company ?? "").Trim().ToUpperInvariant();

            var factor = (!row.FactorSalesToBaseUnit.HasValue || row.FactorSalesToBaseUnit.Value <= 0m)
                ? 1.0m
                : row.FactorSalesToBaseUnit.Value;

            if (!factors.ContainsKey(cleanItemId) || cleanCompany == "PMH")
                factors[cleanItemId] = factor;
        }
        return factors;
    }

    /// <summary>
    /// ยอดขาย PMH/MTW: SALESORDER INNER JOIN CUSTTABLE รวม SUM(QTY) รายเดือนต่อ (SKU, ประเภทเอกสาร, WAREHOUSE, CUSTGROUP, PRICEGROUP)
    /// กรองใน SQL: White-list เอกสาร (Invoice/CN/Change), ตัดศูนย์ยา 3 สาขา (DEC-PBI01-05 ต้องตัดที่นี่ เพราะรวมยอดข้ามลูกค้า
    /// จึงไม่มี CustomerId เหลือให้ Engine ตัด), และเฉพาะคลัง WH01/WHCM01/WHPT01/MW01 | การจับคู่ 5 คลังยังอยู่ที่ Engine (MapPmhMtwWarehouse)
    /// DocDate ที่คืน = วันที่ 1 ของเดือน | ช่วงวันที่เป็น >= และ < (ใช้ Index ได้ ไม่ใช้ FORMAT)
    /// </summary>
    public IReadOnlyList<RawSalesOrderRow> FetchSourceSalesOrders(string startYearMonth, string endYearMonth)
    {
        const string query = @"
            SELECT so.ITEMID,
                   DATEFROMPARTS(YEAR(so.INVOICEDATE), MONTH(so.INVOICEDATE), 1) AS DocMonth,
                   so.DOCUMENTTYPE,
                   c.WAREHOUSE,
                   c.CUSTGROUP,
                   c.PRICEGROUP,
                   SUM(so.QTY) AS Qty
            FROM dbo.SALESORDER AS so
            INNER JOIN dbo.CUSTTABLE AS c
                ON so.CUSTOMERID = c.CUSTOMERID
            WHERE so.INVOICEDATE >= @startDate
              AND so.INVOICEDATE <  @endExclusive
              AND so.ITEMID IS NOT NULL
              AND so.DOCUMENTTYPE IN ('Invoice', 'CN', 'Change')
              AND so.CUSTOMERID NOT IN ('1000001', '2003323', '5006299')
              AND c.WAREHOUSE IN ('WH01', 'WHCM01', 'WHPT01', 'MW01')
            GROUP BY so.ITEMID,
                     DATEFROMPARTS(YEAR(so.INVOICEDATE), MONTH(so.INVOICEDATE), 1),
                     so.DOCUMENTTYPE,
                     c.WAREHOUSE,
                     c.CUSTGROUP,
                     c.PRICEGROUP;";

        var (startDate, endExclusive) = ToDateRange(startYearMonth, endYearMonth);
        var rows = new List<RawSalesOrderRow>();

        using SqlConnection connection = OpenSourceSqlConnection();
        using SqlCommand command = new SqlCommand(query, connection) { CommandTimeout = SourceCommandTimeoutSeconds };
        command.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
        command.Parameters.Add("@endExclusive", SqlDbType.Date).Value = endExclusive;
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new RawSalesOrderRow(
                ItemId: ReadString(reader, 0),
                DocDate: reader.GetDateTime(1),
                DocumentType: ReadString(reader, 2),
                Warehouse: ReadString(reader, 3),
                CustomerId: null,                       // รวมยอดข้ามลูกค้าแล้ว (ตัดศูนย์ยาใน SQL)
                CustGroup: ReadString(reader, 4),
                PriceGroup: ReadString(reader, 5),
                Qty: ReadDecimal(reader, 6)));
        }

        return rows;
    }

    /// <summary>
    /// ยอดขาย POS: SALESPFN INNER JOIN fashof_data.dbo.BRANCH (BRANCHCODE = BRANCHID) รวม SUM(QTY) รายเดือน
    /// ตัดแถว ITEMID/DOCDATE NULL | QTY NULL = 0.0000 | เฉพาะสาขาที่เข้าเงื่อนไข 5 คลัง POS
    /// BranchId ที่คืนถูกย่อเหลือ '1111111' / '1000001' / '*' (สาขาอื่น): ตรงกับกฎใน MapPosWarehouse ทุกกรณี
    /// (ใช้เฉพาะ 2 รหัสนี้ตัดสินคลัง ที่เหลือใช้ VENDORPA) จึงไม่ต้องรวมแยกรายสาขาซึ่งทำให้แถวมหาศาล
    /// </summary>
    public IReadOnlyList<RawPosSalesRow> FetchSourcePosSales(string startYearMonth, string endYearMonth)
    {
        const string query = @"
            SELECT p.ITEMID,
                   DATEFROMPARTS(YEAR(p.DOCDATE), MONTH(p.DOCDATE), 1) AS DocMonth,
                   CASE WHEN b.BRANCHID = '1111111' THEN '1111111'
                        WHEN b.BRANCHID = '1000001' THEN '1000001'
                        ELSE '*' END AS BranchGroup,
                   b.VENDORPA,
                   SUM(ISNULL(CAST(p.QTY AS NUMERIC(38,4)), 0.0000)) AS Qty
            FROM dbo.SALESPFN AS p
            INNER JOIN fashof_data.dbo.BRANCH AS b
                ON p.BRANCHCODE = b.BRANCHID
            WHERE p.ITEMID IS NOT NULL
              AND p.DOCDATE IS NOT NULL
              AND p.DOCDATE >= @startDate
              AND p.DOCDATE <  @endExclusive
              AND (b.BRANCHID = '1111111' OR b.VENDORPA IN ('90003', '90002', '10746'))
            GROUP BY p.ITEMID,
                     DATEFROMPARTS(YEAR(p.DOCDATE), MONTH(p.DOCDATE), 1),
                     CASE WHEN b.BRANCHID = '1111111' THEN '1111111'
                          WHEN b.BRANCHID = '1000001' THEN '1000001'
                          ELSE '*' END,
                     b.VENDORPA;";

        var (startDate, endExclusive) = ToDateRange(startYearMonth, endYearMonth);
        var rows = new List<RawPosSalesRow>();

        using SqlConnection connection = OpenSourceSqlConnection();
        using SqlCommand command = new SqlCommand(query, connection) { CommandTimeout = SourceCommandTimeoutSeconds };
        command.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
        command.Parameters.Add("@endExclusive", SqlDbType.Date).Value = endExclusive;
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new RawPosSalesRow(
                ItemId: ReadString(reader, 0),
                DocDate: reader.GetDateTime(1),
                BranchId: ReadString(reader, 2),
                VendorPa: ReadString(reader, 3),
                Qty: ReadDecimal(reader, 4)));
        }

        return rows;
    }

    /// <summary>
    /// OOS จาก MariaDB: md_wishlist INNER JOIN customers (mid = id) เฉพาะ 3 คลัง PMH B2B ไม่กรอง status
    /// ตัดศูนย์ยา 3 สาขา (accountnum NULL ยังนับ) | qty NULL/ติดลบ -> 0 ก่อนรวม | รวม SUM รายเดือนต่อ (SKU, inventlocation, custgroup)
    /// ตารางอ้างอิงแบบไม่ใส่ชื่อ schema: ใช้ Database ที่ตั้งใน config.xml (&lt;SourceMariaDb&gt;)
    /// </summary>
    public IReadOnlyList<RawMysqlOosRow> FetchSourceMysqlOos(string startYearMonth, string endYearMonth)
    {
        const string query = @"
            SELECT w.productcode,
                   CAST(DATE_FORMAT(w.credate, '%Y-%m-01') AS DATE) AS cre_month,
                   c.inventlocation,
                   c.custgroup,
                   SUM(GREATEST(0, IFNULL(w.qty, 0))) AS qty
            FROM md_wishlist AS w
            INNER JOIN customers AS c
                ON w.mid = c.id
            WHERE w.productcode IS NOT NULL
              AND w.credate IS NOT NULL
              AND w.credate >= @startDate
              AND w.credate <  @endExclusive
              AND (c.accountnum IS NULL OR c.accountnum NOT IN ('1000001', '2003323', '5006299'))
              AND (   (c.inventlocation = 'WH01'   AND c.custgroup = 'DOM')
                   OR (c.inventlocation = 'WHCM01' AND c.custgroup = 'DOMCM')
                   OR (c.inventlocation = 'WHPT01' AND c.custgroup = 'DOMPT'))
            GROUP BY w.productcode, cre_month, c.inventlocation, c.custgroup;";

        var (startDate, endExclusive) = ToDateRange(startYearMonth, endYearMonth);
        var rows = new List<RawMysqlOosRow>();

        using MySqlConnection connection = OpenSourceMariaDbConnection();
        using MySqlCommand command = new MySqlCommand(query, connection) { CommandTimeout = SourceCommandTimeoutSeconds };
        command.Parameters.AddWithValue("@startDate", startDate);
        command.Parameters.AddWithValue("@endExclusive", endExclusive);
        using DbDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new RawMysqlOosRow(
                ProductCode: ReadString(reader, 0),
                CreDate: Convert.ToDateTime(reader.GetValue(1), CultureInfo.InvariantCulture),
                CustomerId: null,                       // ตัดศูนย์ยาใน SQL แล้ว (accountnum)
                InventLocation: ReadString(reader, 2),
                CustGroup: ReadString(reader, 3),
                Qty: ReadDecimal(reader, 4)));
        }

        return rows;
    }

    // ---------- ตัวช่วยฝั่ง Source ----------

    private SqlConnection OpenSourceSqlConnection()
    {
        if (string.IsNullOrWhiteSpace(_sourceSqlConnectionString))
            throw new InvalidOperationException("ไม่ได้ตั้งค่า Source SQL Server (config.xml: <SourceSql>)");

        var connection = new SqlConnection(_sourceSqlConnectionString);
        connection.Open();
        return connection;
    }

    private MySqlConnection OpenSourceMariaDbConnection()
    {
        if (string.IsNullOrWhiteSpace(_sourceMariaDbConnectionString))
            throw new InvalidOperationException("ไม่ได้ตั้งค่า Source MariaDB (config.xml: <SourceMariaDb>)");

        var connection = new MySqlConnection(_sourceMariaDbConnectionString);
        connection.Open();
        return connection;
    }

    // yyyyMM..yyyyMM -> [วันที่ 1 ของเดือนเริ่ม, วันที่ 1 ของเดือนถัดจากเดือนสิ้นสุด)
    private static (DateTime Start, DateTime EndExclusive) ToDateRange(string startYearMonth, string endYearMonth)
    {
        var start = DateTime.ParseExact(startYearMonth, "yyyyMM", CultureInfo.InvariantCulture);
        var endExclusive = DateTime.ParseExact(endYearMonth, "yyyyMM", CultureInfo.InvariantCulture).AddMonths(1);
        return (start, endExclusive);
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    // Convert.ToDecimal รองรับทุกชนิดตัวเลขของต้นทาง (numeric / float / int) ไม่ผูกกับชนิดคอลัมน์
    private static decimal? ReadDecimal(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    // ====================================================================================================
    // 2. DRP INTERNAL DB OPERATIONS (Fully Implemented: Master Items, Staging, Transactions & Bulk Copy)
    // ====================================================================================================

    /// <summary>
    /// Reads current DRP Master Items from Target DB: DRP_MAS_ITEM.
    /// </summary>
    public IReadOnlyDictionary<string, DrpMasItemRecord> GetExistingMasterItems()
    {
        const string query = @"
            SELECT item_id, item_name, product_line, updated_at
            FROM DRP_MAS_ITEM;";

        var result = new Dictionary<string, DrpMasItemRecord>(StringComparer.OrdinalIgnoreCase);
        using SqlCommand command = new SqlCommand(query, _targetConnection, _activeTransaction);
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            var itemId = reader.GetString(0);
            var itemName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var productLine = reader.IsDBNull(2) ? "HB" : reader.GetString(2);
            var updatedAt = reader.GetDateTime(3);

            result[itemId] = new DrpMasItemRecord(itemId, itemName, productLine, updatedAt);
        }

        return result;
    }

    /// <summary>
    /// Reads existing staging records from Target DB: DRP_STG_DRP_DEMAND_24M.
    /// </summary>
    public IReadOnlyList<DrpStgDemand24MRecord> GetExistingStagingRecords()
    {
        const string query = @"
            SELECT warehouse_code, item_id, period_year_month, raw_sales_qty, oos_lost_qty, ingested_at
            FROM DRP_STG_DRP_DEMAND_24M;";

        var result = new List<DrpStgDemand24MRecord>();
        using SqlCommand command = new SqlCommand(query, _targetConnection, _activeTransaction);
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            var warehouseCode = reader.GetString(0);
            var itemId = reader.GetString(1);
            var yearMonth = reader.GetString(2);
            var rawSalesQty = reader.IsDBNull(3) ? 0.0000m : reader.GetDecimal(3);
            var oosLostQty = reader.IsDBNull(4) ? 0.0000m : reader.GetDecimal(4);
            var ingestedAt = reader.GetDateTime(5);

            result.Add(new DrpStgDemand24MRecord(itemId, warehouseCode, yearMonth, rawSalesQty, oosLostQty, ingestedAt));
        }

        return result;
    }

    /// <summary>
    /// MAX(raw_sales_qty) per (item_id, warehouse_code) within startPeriod..endPeriod, aggregated by SQL Server.
    /// Used by NonInit mode for max_hist_sales (avoids loading the whole ~10M-row staging table into memory).
    /// </summary>
    public IReadOnlyDictionary<(string ItemId, string WarehouseCode), decimal> GetHistoricalMaxSales(string startPeriod, string endPeriod)
    {
        const string query = @"
            SELECT item_id, warehouse_code, MAX(raw_sales_qty) AS max_sales
            FROM DRP_STG_DRP_DEMAND_24M
            WHERE period_year_month BETWEEN @pStart AND @pEnd
            GROUP BY item_id, warehouse_code;";

        var result = new Dictionary<(string ItemId, string WarehouseCode), decimal>();
        using SqlCommand command = new SqlCommand(query, _targetConnection, _activeTransaction) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add("@pStart", SqlDbType.VarChar, 6).Value = startPeriod;
        command.Parameters.Add("@pEnd", SqlDbType.VarChar, 6).Value = endPeriod;
        using SqlDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            var itemId = reader.GetString(0);
            var warehouseCode = reader.GetString(1);
            var maxSales = reader.IsDBNull(2) ? 0.0000m : reader.GetDecimal(2);
            result[(itemId, warehouseCode)] = maxSales;
        }

        return result;
    }

    /// <summary>
    /// Begins a SqlTransaction on the target DRP database connection.
    /// </summary>
    public void BeginTransaction()
    {
        if (_activeTransaction != null)
            throw new InvalidOperationException("An active transaction is already in progress.");

        _activeTransaction = _targetConnection.BeginTransaction();
    }

    /// <summary>
    /// Upserts master items into DRP_MAS_ITEM using a temporary staging table
    /// and MERGE statement within the active transaction (Preserves historical SKUs 100%).
    /// </summary>
    public void UpsertMasterItems(IEnumerable<DrpMasItemRecord> items)
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("Cannot upsert master items without an active transaction.");

        var itemList = items.ToList();
        if (itemList.Count == 0) return;

        // 1. Create #TempMasterItems table
        const string createTempTableSql = @"
            CREATE TABLE #TempMasterItems (
                item_id VARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                item_name NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
                product_line VARCHAR(2) COLLATE DATABASE_DEFAULT NOT NULL,
                updated_at DATETIME NOT NULL
            );";

        using (SqlCommand createCmd = new SqlCommand(createTempTableSql, _targetConnection, _activeTransaction))
        {
            createCmd.CommandTimeout = CommandTimeoutSeconds;
            createCmd.ExecuteNonQuery();
        }

        // 2. Bulk copy memory items into #TempMasterItems
        // หมายเหตุ: Engine การันตีมาแล้วว่า itemList ไม่มี item_id ซ้ำ (dedupe ตาม Company ที่ชั้น Engine)
        DataTable dataTable = new DataTable();
        dataTable.Columns.Add("item_id", typeof(string));
        dataTable.Columns.Add("item_name", typeof(string));
        dataTable.Columns.Add("product_line", typeof(string));
        dataTable.Columns.Add("updated_at", typeof(DateTime));

        foreach (var item in itemList)
        {
            dataTable.Rows.Add(item.ItemId, item.ItemName, item.ProductLine, item.UpdatedAt);
        }

        using (SqlBulkCopy bulkCopy = new SqlBulkCopy(_targetConnection, SqlBulkCopyOptions.Default, _activeTransaction))
        {
            bulkCopy.DestinationTableName = "#TempMasterItems";
            bulkCopy.BulkCopyTimeout = CommandTimeoutSeconds;
            bulkCopy.ColumnMappings.Add("item_id", "item_id");
            bulkCopy.ColumnMappings.Add("item_name", "item_name");
            bulkCopy.ColumnMappings.Add("product_line", "product_line");
            bulkCopy.ColumnMappings.Add("updated_at", "updated_at");
            bulkCopy.WriteToServer(dataTable);
        }

        // 3. MERGE into DRP_MAS_ITEM
        const string mergeSql = @"
            MERGE DRP_MAS_ITEM AS target
            USING #TempMasterItems AS source
                ON target.item_id = source.item_id
            WHEN MATCHED AND (target.item_name <> source.item_name OR target.product_line <> source.product_line)
                THEN UPDATE SET 
                    target.item_name = source.item_name,
                    target.product_line = source.product_line,
                    target.updated_at = source.updated_at
            WHEN NOT MATCHED BY TARGET
                THEN INSERT (item_id, item_name, product_line, updated_at)
                     VALUES (source.item_id, source.item_name, source.product_line, source.updated_at);
            
            DROP TABLE #TempMasterItems;";

        using (SqlCommand mergeCmd = new SqlCommand(mergeSql, _targetConnection, _activeTransaction))
        {
            mergeCmd.CommandTimeout = CommandTimeoutSeconds;
            mergeCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes staging rows inside the processing scope before bulk inserting the new grid (PBI-01 Idempotency Policy):
    ///   DELETE ... WHERE period_year_month BETWEEN @startPeriod AND @endPeriod [AND warehouse_code IN (...)]
    /// Init    : startPeriod..endPeriod = 25 months window (rows outside the window are kept: Accumulating Historical Store).
    /// NonInit : startPeriod == endPeriod == target period.
    /// warehouseCodes == null -> all warehouses. Never TRUNCATE (it would wipe accumulated history and user-edited oos_lost_qty).
    /// </summary>
    public void DeleteStagingScope(string startPeriod, string endPeriod, IReadOnlyCollection<string>? warehouseCodes)
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("Cannot delete staging scope without an active transaction.");

        if (warehouseCodes != null && warehouseCodes.Count == 0)
            throw new ArgumentException("warehouseCodes must be null (all warehouses) or contain at least one code.", nameof(warehouseCodes));

        var sql = new System.Text.StringBuilder(
            "DELETE FROM DRP_STG_DRP_DEMAND_24M WHERE period_year_month BETWEEN @pStart AND @pEnd");

        List<string>? warehouseList = warehouseCodes?.ToList();
        if (warehouseList != null)
        {
            var names = warehouseList.Select((_, index) => $"@pWh{index}");
            sql.Append($" AND warehouse_code IN ({string.Join(", ", names)})");
        }
        sql.Append(';');

        using SqlCommand cmd = new SqlCommand(sql.ToString(), _targetConnection, _activeTransaction) { CommandTimeout = CommandTimeoutSeconds };
        // ระบุ VarChar ให้ตรงชนิดคอลัมน์ (AddWithValue จะส่งเป็น nvarchar -> implicit conversion กระทบ Index)
        cmd.Parameters.Add("@pStart", SqlDbType.VarChar, 6).Value = startPeriod;
        cmd.Parameters.Add("@pEnd", SqlDbType.VarChar, 6).Value = endPeriod;
        if (warehouseList != null)
        {
            for (int index = 0; index < warehouseList.Count; index++)
                cmd.Parameters.Add($"@pWh{index}", SqlDbType.VarChar, 20).Value = warehouseList[index];
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// SqlBulkCopy into DRP_STG_DRP_DEMAND_24M within the active transaction.
    /// รับ IEnumerable แบบ Lazy (Engine สร้าง Grid ทีละแถว) แล้วส่งเข้า SqlBulkCopy เป็นก้อนละ BulkChunkSize แถว
    /// จึงถือใน Memory ไม่เกิน 1 ก้อน (Init ~10.5M แถวไม่ถูกสร้างเป็น List/DataTable ก้อนเดียว)
    /// CheckConstraints: ให้ FK (item_id -> DRP_MAS_ITEM) และ CHECK (oos_lost_qty >= 0) ทำงานระหว่างโหลด
    /// มิฉะนั้น Bulk Load จะข้ามการตรวจและ Constraint จะกลายเป็น not trusted
    /// </summary>
    public void BulkInsertStaging(IEnumerable<DrpStgDemand24MRecord> records)
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("Cannot bulk insert staging without an active transaction.");

        using DataTable chunk = CreateStagingDataTable();
        using SqlBulkCopy bulkCopy = new SqlBulkCopy(_targetConnection, SqlBulkCopyOptions.CheckConstraints, _activeTransaction)
        {
            DestinationTableName = "DRP_STG_DRP_DEMAND_24M",
            BatchSize = BulkChunkSize,
            BulkCopyTimeout = CommandTimeoutSeconds
        };
        bulkCopy.ColumnMappings.Add("warehouse_code", "warehouse_code");
        bulkCopy.ColumnMappings.Add("item_id", "item_id");
        bulkCopy.ColumnMappings.Add("period_year_month", "period_year_month");
        bulkCopy.ColumnMappings.Add("raw_sales_qty", "raw_sales_qty");
        bulkCopy.ColumnMappings.Add("oos_lost_qty", "oos_lost_qty");
        bulkCopy.ColumnMappings.Add("ingested_at", "ingested_at");

        foreach (var record in records)
        {
            chunk.Rows.Add(
                record.WarehouseCode,
                record.ItemId,
                record.YearMonth,
                record.RawSalesQty,
                record.OosLostQty,
                record.ImportedAt);

            if (chunk.Rows.Count >= BulkChunkSize)
            {
                bulkCopy.WriteToServer(chunk);
                chunk.Clear();
            }
        }

        if (chunk.Rows.Count > 0)
        {
            bulkCopy.WriteToServer(chunk);
            chunk.Clear();
        }
    }

    private static DataTable CreateStagingDataTable()
    {
        DataTable dataTable = new DataTable();
        dataTable.Columns.Add("warehouse_code", typeof(string));
        dataTable.Columns.Add("item_id", typeof(string));
        dataTable.Columns.Add("period_year_month", typeof(string));
        dataTable.Columns.Add("raw_sales_qty", typeof(decimal));
        dataTable.Columns.Add("oos_lost_qty", typeof(decimal));
        dataTable.Columns.Add("ingested_at", typeof(DateTime));
        return dataTable;
    }

    /// <summary>
    /// Commits the active SqlTransaction.
    /// </summary>
    public void CommitTransaction()
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("No active transaction to commit.");

        _activeTransaction.Commit();
        _activeTransaction.Dispose();
        _activeTransaction = null;
    }

    /// <summary>
    /// Rolls back the active SqlTransaction in case of any failure.
    /// </summary>
    public void RollbackTransaction()
    {
        if (_activeTransaction == null) return;

        _activeTransaction.Rollback();
        _activeTransaction.Dispose();
        _activeTransaction = null;
    }
}