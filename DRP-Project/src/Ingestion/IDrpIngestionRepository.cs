namespace DRP_Project.src.Ingestion;

public interface IDrpIngestionRepository
{
    // 1. Source Data Extraction
    IReadOnlyList<RawInventTableRow> FetchSourceInventTable();
    IReadOnlyList<RawSalesOrderRow> FetchSourceSalesOrders(string startYearMonth, string endYearMonth);
    IReadOnlyList<RawPosSalesRow> FetchSourcePosSales(string startYearMonth, string endYearMonth);
    IReadOnlyList<RawMysqlOosRow> FetchSourceMysqlOos(string startYearMonth, string endYearMonth);

    // 2. Target Reads (For Catalog Lookup, Conversion Factor & NonInit Historical Max Sales)
    IReadOnlyDictionary<string, DrpMasItemRecord> GetExistingMasterItems();
    IReadOnlyDictionary<string, decimal> GetSourceConversionFactors();
    IReadOnlyList<DrpStgDemand24MRecord> GetExistingStagingRecords();

    // 3. Transaction & Target Writes
    void BeginTransaction();
    void UpsertMasterItems(IEnumerable<DrpMasItemRecord> items);
    void DeleteStagingScope(string mode, string targetPeriod, string? targetWarehouseCode);
    void BulkInsertStaging(IEnumerable<DrpStgDemand24MRecord> records);
    void CommitTransaction();
    void RollbackTransaction();
}
