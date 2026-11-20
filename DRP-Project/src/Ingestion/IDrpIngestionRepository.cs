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
    IReadOnlyList<DrpStgDemand24MRecord> GetExistingStagingRecords();   // อ่านทั้งตาราง: ใช้ตรวจสอบ/ทดสอบเท่านั้น ห้ามใช้ใน Engine (Staging ~10M แถว)

    // MAX(raw_sales_qty) ต่อ (item_id, warehouse_code) ในช่วง startPeriod..endPeriod (ผ่านการ GROUP BY ที่ฐานข้อมูล)
    // ใช้คำนวณ max_hist_sales โหมด NonInit | คู่ (SKU, คลัง) ที่ไม่มีข้อมูลในช่วงนั้นจะไม่อยู่ใน Dictionary
    IReadOnlyDictionary<(string ItemId, string WarehouseCode), decimal> GetHistoricalMaxSales(string startPeriod, string endPeriod);

    // 3. Transaction & Target Writes
    void BeginTransaction();
    void UpsertMasterItems(IEnumerable<DrpMasItemRecord> items);
    // ลบเฉพาะ period_year_month BETWEEN startPeriod AND endPeriod (Init = 25 งวด, NonInit = งวดเดียว start == end)
    // warehouseCodes = null -> ทุกคลัง | ไม่ null -> เฉพาะคลังในชุดนี้ (ต้องเป็นชุดเดียวกับที่ Engine จะ insert)
    void DeleteStagingScope(string startPeriod, string endPeriod, IReadOnlyCollection<string>? warehouseCodes);
    void BulkInsertStaging(IEnumerable<DrpStgDemand24MRecord> records);
    void CommitTransaction();
    void RollbackTransaction();
}