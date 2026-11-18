using System.Data;
using Microsoft.Data.SqlClient;

namespace DRP_Project.src.Ingestion;

/// <summary>
/// Production database repository for PBI-01 (Direct SQL Demand View & 24M Staging Sync).
/// Implements DRP Internal DB operations (Target DB: DRP_MAS_ITEM & DRP_STG_DRP_DEMAND_24M)
/// using the exact same architectural pattern as OutlierCleansingRepository & NetDemandPreprocessor:
/// (SqlConnection, SqlTransaction, SqlCommand, and SqlBulkCopy).
/// External DB source extraction methods remain as stubs with Full SQL queries for Dev handover.
/// </summary>
public class SqlDrpIngestionRepository : IDrpIngestionRepository
{
    private readonly SqlConnection _targetConnection;
    private SqlTransaction? _activeTransaction;

    public SqlDrpIngestionRepository(SqlConnection targetConnection)
    {
        _targetConnection = targetConnection;
    }

    // ====================================================================================================
    // 1. EXTERNAL DB SOURCE EXTRACTION (Dev Handover Stubs with Full SQL in XML Documentation)
    // ====================================================================================================

    /// <summary>
    /// Fetches Master Items from INVENTTABLE (External DB).
    ///
    /// [FULL SQL QUERY: Source DB INVENTTABLE Extraction]
    /// SELECT 
    ///     ITEMID AS ItemId,
    ///     ISNULL(ITEMNAME, N'') AS ItemName,
    ///     CASE 
    ///         WHEN UPPER(LTRIM(RTRIM(PRODUCTLINE))) = 'YP' THEN 'YP' 
    ///         ELSE 'HB' 
    ///     END AS ProductLine,
    ///     ISNULL(NULLIF(FACTOR_SALESTOBASEUNIT, 0), 1.0) AS FactorSalesToBaseUnit
    /// FROM INVENTTABLE;
    /// </summary>
    public IReadOnlyList<RawInventTableRow> FetchSourceInventTable()
    {
        throw new NotImplementedException("Dev Handover: Implement external SqlConnection to INVENTTABLE using the query in comments.");
    }

    /// <summary>
    /// Fetches PMH and MTW sales transaction rows from SALESORDER joined with CUSTTABLE (External DB).
    ///
    /// [FULL SQL QUERY OPTION 1: Raw Unaggregated Rows (Matching RawSalesOrderRow DTO for In-Memory Engine)]
    /// SELECT 
    ///     so.ITEMID AS ItemId,
    ///     so.INVOICEDATE AS DocDate,
    ///     so.DOCUMENTTYPE AS DocumentType,
    ///     c.WAREHOUSE AS Warehouse,
    ///     c.CUSTOMERID AS CustomerId,
    ///     c.CUSTGROUP AS CustGroup,
    ///     c.PRICEGROUP AS PriceGroup,
    ///     so.QTY AS Qty
    /// FROM SALESORDER so
    /// INNER JOIN CUSTTABLE c
    ///     ON so.CUSTOMERID = c.CUSTOMERID
    /// WHERE (
    ///         (c.WAREHOUSE = 'WH01' AND c.CUSTGROUP = 'DOM')
    ///         OR (c.WAREHOUSE = 'WHCM01' AND c.CUSTGROUP = 'DOMCM')
    ///         OR (c.WAREHOUSE = 'WHPT01' AND c.CUSTGROUP = 'DOMPT')
    ///         OR (c.WAREHOUSE = 'MW01' AND c.PRICEGROUP = 'ONLINE')
    ///         OR (c.WAREHOUSE = 'MW01' AND c.PRICEGROUP <> 'ONLINE')
    ///         OR (c.WAREHOUSE = 'MW01' AND (c.PRICEGROUP IS NULL OR c.PRICEGROUP = ''))
    ///     )
    ///   AND c.CUSTOMERID NOT IN ('1000001', '2003323', '5006299') -- Exclude Pharmacy Centers
    ///   AND so.DOCUMENTTYPE IN ('Invoice', 'CN', 'Change')
    ///   AND FORMAT(so.INVOICEDATE, 'yyyyMM') >= @startYearMonth
    ///   AND FORMAT(so.INVOICEDATE, 'yyyyMM') <= @endYearMonth;
    /// </summary>
    public IReadOnlyList<RawSalesOrderRow> FetchSourceSalesOrders(string startYearMonth, string endYearMonth)
    {
        throw new NotImplementedException("Dev Handover: Implement external PMH/MTW Sales extraction query using the query in comments.");
    }

    /// <summary>
    /// Fetches POS sales transaction rows from SALESPFN joined with fashof_data.dbo.BRANCH (External DB).
    ///
    /// [FULL SQL QUERY OPTION 1: Raw Unaggregated Rows (Matching RawPosSalesRow DTO for In-Memory Engine)]
    /// SELECT 
    ///     pfn.ITEMID AS ItemId,
    ///     pfn.DOCDATE AS DocDate,
    ///     b.BRANCHID AS BranchId,
    ///     b.VENDORPA AS VendorPa,
    ///     ISNULL(pfn.QTY, 0.0000) AS Qty
    /// FROM SALESPFN pfn
    /// INNER JOIN fashof_data.dbo.BRANCH b
    ///     ON pfn.BRANCHCODE = b.BRANCHID
    /// WHERE (
    ///         b.BRANCHID = N'1111111'
    ///         OR b.VENDORPA IN (N'90003', N'90002')
    ///         OR b.VENDORPA = N'10746'
    ///     )
    ///   AND pfn.BRANCHCODE IS NOT NULL
    ///   AND pfn.ITEMID IS NOT NULL
    ///   AND pfn.DOCDATE IS NOT NULL
    ///   AND FORMAT(pfn.DOCDATE, 'yyyyMM') >= @startYearMonth
    ///   AND FORMAT(pfn.DOCDATE, 'yyyyMM') <= @endYearMonth;
    /// </summary>
    public IReadOnlyList<RawPosSalesRow> FetchSourcePosSales(string startYearMonth, string endYearMonth)
    {
        throw new NotImplementedException("Dev Handover: Implement external POS Sales extraction query using the query in comments.");
    }

    /// <summary>
    /// Fetches MySQL Out-of-Stock (OOS) wishlist records from admin_db.md_wishlist joined with admin_db.customers (External MySQL DB).
    ///
    /// [FULL SQL QUERY: MySQL / MariaDB OOS Extraction (PMH B2B 3 Warehouses)]
    /// SELECT
    ///     w.productcode AS ProductCode,
    ///     w.credate AS CreDate,
    ///     c.accountnum AS CustomerId,
    ///     c.inventlocation AS InventLocation,
    ///     c.custgroup AS CustGroup,
    ///     GREATEST(0, IFNULL(w.qty, 0)) AS Qty
    /// FROM admin_db.md_wishlist w
    /// INNER JOIN admin_db.customers c
    ///     ON w.mid = c.id
    /// WHERE (
    ///         (c.inventlocation = 'WH01' AND c.custgroup = 'DOM')
    ///         OR (c.inventlocation = 'WHCM01' AND c.custgroup = 'DOMCM')
    ///         OR (c.inventlocation = 'WHPT01' AND c.custgroup = 'DOMPT')
    ///     )
    ///   AND c.accountnum NOT IN ('1000001', '2003323', '5006299') -- Exclude Pharmacy Centers
    ///   AND w.productcode IS NOT NULL
    ///   AND w.credate IS NOT NULL
    ///   AND DATE_FORMAT(w.credate, '%Y%m') >= @startYearMonth
    ///   AND DATE_FORMAT(w.credate, '%Y%m') <= @endYearMonth;
    /// </summary>
    public IReadOnlyList<RawMysqlOosRow> FetchSourceMysqlOos(string startYearMonth, string endYearMonth)
    {
        throw new NotImplementedException("Dev Handover: Implement external MySqlConnection to admin_db.md_wishlist using the query in comments.");
    }

    /// <summary>
    /// Reads conversion factors from INVENTTABLE (External DB).
    ///
    /// [FULL SQL QUERY: Conversion Factors Lookup]
    /// SELECT 
    ///     ITEMID AS ItemId,
    ///     ISNULL(NULLIF(FACTOR_SALESTOBASEUNIT, 0), 1.0) AS ConversionFactor
    /// FROM INVENTTABLE
    /// WHERE ITEMID IS NOT NULL;
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetSourceConversionFactors()
    {
        throw new NotImplementedException("Dev Handover: Implement external conversion factors lookup from INVENTTABLE using the query in comments.");
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
                item_id VARCHAR(50) NOT NULL PRIMARY KEY,
                item_name NVARCHAR(200) NOT NULL,
                product_line VARCHAR(2) NOT NULL,
                updated_at DATETIME NOT NULL
            );";

        using (SqlCommand createCmd = new SqlCommand(createTempTableSql, _targetConnection, _activeTransaction))
        {
            createCmd.ExecuteNonQuery();
        }

        // 2. Bulk copy memory items into #TempMasterItems
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
            mergeCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes staging records for the specified period and warehouse scope before bulk inserting new records.
    /// Mode = 'Init': Deletes 25-month window (or TRUNCATE if scope is full).
    /// Mode = 'NonInit': Deletes targetPeriod (and scoped warehouse if specified).
    /// </summary>
    public void DeleteStagingScope(string mode, string targetPeriod, string? targetWarehouseCode)
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("Cannot delete staging scope without an active transaction.");

        string deleteSql;
        if (mode.Equals("Init", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(targetWarehouseCode))
        {
            // Full Init mode: TRUNCATE TABLE is transactional in SQL Server
            deleteSql = "TRUNCATE TABLE DRP_STG_DRP_DEMAND_24M;";
            using SqlCommand cmd = new SqlCommand(deleteSql, _targetConnection, _activeTransaction);
            cmd.ExecuteNonQuery();
        }
        else
        {
            deleteSql = @"
                DELETE FROM DRP_STG_DRP_DEMAND_24M
                WHERE period_year_month = @pTargetPeriod
                  AND (@pWarehouseCode IS NULL OR warehouse_code = @pWarehouseCode);";

            using SqlCommand cmd = new SqlCommand(deleteSql, _targetConnection, _activeTransaction);
            cmd.Parameters.AddWithValue("@pTargetPeriod", targetPeriod);
            cmd.Parameters.AddWithValue("@pWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Executes high-performance SqlBulkCopy into DRP_STG_DRP_DEMAND_24M
    /// within the active transaction.
    /// </summary>
    public void BulkInsertStaging(IEnumerable<DrpStgDemand24MRecord> records)
    {
        if (_activeTransaction == null)
            throw new InvalidOperationException("Cannot bulk insert staging without an active transaction.");

        var recordList = records.ToList();
        if (recordList.Count == 0) return;

        DataTable dataTable = new DataTable();
        dataTable.Columns.Add("warehouse_code", typeof(string));
        dataTable.Columns.Add("item_id", typeof(string));
        dataTable.Columns.Add("period_year_month", typeof(string));
        dataTable.Columns.Add("raw_sales_qty", typeof(decimal));
        dataTable.Columns.Add("oos_lost_qty", typeof(decimal));
        dataTable.Columns.Add("ingested_at", typeof(DateTime));

        foreach (var record in recordList)
        {
            dataTable.Rows.Add(
                record.WarehouseCode,
                record.ItemId,
                record.YearMonth,
                record.RawSalesQty,
                record.OosLostQty,
                record.ImportedAt
            );
        }

        using SqlBulkCopy bulkCopy = new SqlBulkCopy(_targetConnection, SqlBulkCopyOptions.Default, _activeTransaction);
        bulkCopy.DestinationTableName = "DRP_STG_DRP_DEMAND_24M";
        bulkCopy.ColumnMappings.Add("warehouse_code", "warehouse_code");
        bulkCopy.ColumnMappings.Add("item_id", "item_id");
        bulkCopy.ColumnMappings.Add("period_year_month", "period_year_month");
        bulkCopy.ColumnMappings.Add("raw_sales_qty", "raw_sales_qty");
        bulkCopy.ColumnMappings.Add("oos_lost_qty", "oos_lost_qty");
        bulkCopy.ColumnMappings.Add("ingested_at", "ingested_at");
        bulkCopy.WriteToServer(dataTable);
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
