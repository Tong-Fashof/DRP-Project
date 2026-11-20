using System.Globalization;

namespace DRP_Project.src.Ingestion;

public class MockDrpIngestionRepository : IDrpIngestionRepository
{
    // Source data stores
    public List<RawInventTableRow> SourceInventTable { get; } = new();
    public List<RawSalesOrderRow> SourceSalesOrders { get; } = new();
    public List<RawPosSalesRow> SourcePosSales { get; } = new();
    public List<RawMysqlOosRow> SourceMysqlOos { get; } = new();

    // Target database stores
    public Dictionary<string, DrpMasItemRecord> MasterItemsTable { get; } = new();
    public List<DrpStgDemand24MRecord> StagingDemand24MTable { get; } = new();

    // Transaction snapshot stores
    private Dictionary<string, DrpMasItemRecord>? _masterItemsSnapshot;
    private List<DrpStgDemand24MRecord>? _stagingSnapshot;
    private bool _inTransaction;

    // Fault injection flag for TC-01-09
    public bool SimulateBulkInsertFailure { get; set; } = false;

    public IReadOnlyList<RawInventTableRow> FetchSourceInventTable() => SourceInventTable.ToList();

    public IReadOnlyList<RawSalesOrderRow> FetchSourceSalesOrders(string startYearMonth, string endYearMonth)
    {
        return SourceSalesOrders
            .Where(r => r.DocDate.HasValue &&
                        string.Compare(r.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), startYearMonth, StringComparison.Ordinal) >= 0 &&
                        string.Compare(r.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), endYearMonth, StringComparison.Ordinal) <= 0)
            .ToList();
    }

    public IReadOnlyList<RawPosSalesRow> FetchSourcePosSales(string startYearMonth, string endYearMonth)
    {
        return SourcePosSales
            .Where(r => r.DocDate.HasValue &&
                        string.Compare(r.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), startYearMonth, StringComparison.Ordinal) >= 0 &&
                        string.Compare(r.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), endYearMonth, StringComparison.Ordinal) <= 0)
            .ToList();
    }

    public IReadOnlyList<RawMysqlOosRow> FetchSourceMysqlOos(string startYearMonth, string endYearMonth)
    {
        return SourceMysqlOos
            .Where(r => r.CreDate.HasValue &&
                        string.Compare(r.CreDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), startYearMonth, StringComparison.Ordinal) >= 0 &&
                        string.Compare(r.CreDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture), endYearMonth, StringComparison.Ordinal) <= 0)
            .ToList();
    }

    public IReadOnlyDictionary<string, DrpMasItemRecord> GetExistingMasterItems()
    {
        return MasterItemsTable.ToDictionary(k => k.Key, v => v.Value);
    }

    /// <summary>
    /// รวม Factor ทุก Company เป็น Dictionary เดียว (Company = 'PMH' ชนะถ้าซ้ำ) ไม่สนตัวพิมพ์
    /// หมายเหตุ: Engine ไม่เรียกใช้เมธอดนี้แล้ว (คำนวณ Factor แยกตาม Company เองจาก FetchSourceInventTable)
    /// คงไว้เพื่อความเข้ากันได้ของ Interface/เทสต์เดิมเท่านั้น
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetSourceConversionFactors()
    {
        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in SourceInventTable)
        {
            if (string.IsNullOrWhiteSpace(row.ItemId)) continue;
            var cleanItemId = row.ItemId.Trim();
            var cleanCompany = (row.Company ?? "").Trim().ToUpperInvariant();

            var factor = (!row.FactorSalesToBaseUnit.HasValue || row.FactorSalesToBaseUnit.Value <= 0)
                ? 1.0m
                : row.FactorSalesToBaseUnit.Value;

            if (!dict.ContainsKey(cleanItemId) || cleanCompany == "PMH")
                dict[cleanItemId] = factor;
        }
        return dict;
    }

    public IReadOnlyList<DrpStgDemand24MRecord> GetExistingStagingRecords()
    {
        return StagingDemand24MTable.ToList();
    }

    public IReadOnlyDictionary<(string ItemId, string WarehouseCode), decimal> GetHistoricalMaxSales(string startPeriod, string endPeriod)
    {
        // เลียนแบบ: SELECT item_id, warehouse_code, MAX(raw_sales_qty) ... WHERE period BETWEEN @s AND @e GROUP BY item_id, warehouse_code
        return StagingDemand24MTable
            .Where(r => string.Compare(r.YearMonth, startPeriod, StringComparison.Ordinal) >= 0 &&
                        string.Compare(r.YearMonth, endPeriod, StringComparison.Ordinal) <= 0)
            .GroupBy(r => (r.ItemId, r.WarehouseCode))
            .ToDictionary(g => (ItemId: g.Key.ItemId, WarehouseCode: g.Key.WarehouseCode), g => g.Max(r => r.RawSalesQty));
    }

    public void BeginTransaction()
    {
        _inTransaction = true;
        _masterItemsSnapshot = MasterItemsTable.ToDictionary(k => k.Key, v => v.Value);
        _stagingSnapshot = StagingDemand24MTable.Select(r => r with { }).ToList();
    }

    public void UpsertMasterItems(IEnumerable<DrpMasItemRecord> items)
    {
        foreach (var item in items)
        {
            MasterItemsTable[item.ItemId] = item;
        }
    }

    public void DeleteStagingScope(string startPeriod, string endPeriod, IReadOnlyCollection<string>? warehouseCodes)
    {
        // เลียนแบบ: DELETE ... WHERE period_year_month BETWEEN @start AND @end AND (warehouse IN (...) หรือทุกคลัง)
        StagingDemand24MTable.RemoveAll(r =>
            string.Compare(r.YearMonth, startPeriod, StringComparison.Ordinal) >= 0 &&
            string.Compare(r.YearMonth, endPeriod, StringComparison.Ordinal) <= 0 &&
            (warehouseCodes == null || warehouseCodes.Contains(r.WarehouseCode)));
    }

    public void BulkInsertStaging(IEnumerable<DrpStgDemand24MRecord> records)
    {
        if (SimulateBulkInsertFailure)
        {
            throw new InvalidOperationException("Simulated SQL Bulk Copy Failure (Deadlock / Primary Key Collision / Network Drop).");
        }

        StagingDemand24MTable.AddRange(records);
    }

    public void CommitTransaction()
    {
        _masterItemsSnapshot = null;
        _stagingSnapshot = null;
        _inTransaction = false;
    }

    public void RollbackTransaction()
    {
        if (!_inTransaction) return;

        if (_masterItemsSnapshot != null)
        {
            MasterItemsTable.Clear();
            foreach (var kvp in _masterItemsSnapshot)
            {
                MasterItemsTable[kvp.Key] = kvp.Value;
            }
            _masterItemsSnapshot = null;
        }

        if (_stagingSnapshot != null)
        {
            StagingDemand24MTable.Clear();
            StagingDemand24MTable.AddRange(_stagingSnapshot);
            _stagingSnapshot = null;
        }

        _inTransaction = false;
    }
}