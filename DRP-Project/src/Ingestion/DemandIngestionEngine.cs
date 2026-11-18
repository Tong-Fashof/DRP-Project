using System.Globalization;

namespace DRP_Project.src.Ingestion;

public class DemandIngestionEngine
{
    private readonly IDrpIngestionRepository _repository;

    public static readonly IReadOnlyList<string> AllTargetWarehouses = new List<string>
    {
        "MDC (B2B)",
        "RDC-CM (B2B)",
        "RDC-PT (B2B)",
        "MTW (B2C)",
        "MTW (B2B)",
        "ONLINE WH",
        "RDC-PT (B2C)",
        "RDC-CM (B2C)",
        "RDC-SR (B2C)",
        "MDC (B2CXSR)"
    };

    private static readonly HashSet<string> RegionalPharmacyCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        "1000001",
        "2003323",
        "5006299"
    };

    public DemandIngestionEngine(IDrpIngestionRepository repository)
    {
        _repository = repository;
    }

    public void ExecuteIngestion(IngestionJobRequest request)
    {
        var executionTimestamp = request.ExecutionTimestamp ?? DateTime.UtcNow;
        var executionMode = request.Mode.Trim();
        var targetPeriodYearMonth = request.TargetPeriod.Trim();

        // 1. Calculate Period Range (25 months for Init: M-24 to M0; 1 month for NonInit: targetPeriodYearMonth)
        List<string> periodMonths = new();
        if (executionMode.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            DateTime baseDate = DateTime.ParseExact(targetPeriodYearMonth, "yyyyMM", CultureInfo.InvariantCulture);
            for (int monthOffset = 24; monthOffset >= 0; monthOffset--)
            {
                periodMonths.Add(baseDate.AddMonths(-monthOffset).ToString("yyyyMM"));
            }
        }
        else
        {
            periodMonths.Add(targetPeriodYearMonth);
        }

        string startPeriodYearMonth = periodMonths.First();
        string endPeriodYearMonth = periodMonths.Last();

        // 2. Step 0: Sync Master Catalog from INVENTTABLE
        var rawInventTableRows = _repository.FetchSourceInventTable();
        var existingMasterItems = _repository.GetExistingMasterItems();
        var conversionFactorLookup = _repository.GetSourceConversionFactors();

        var masterUpsertList = new List<DrpMasItemRecord>();
        var activeMasterCatalog = new Dictionary<string, DrpMasItemRecord>(existingMasterItems);

        foreach (var inventRow in rawInventTableRows)
        {
            if (string.IsNullOrWhiteSpace(inventRow.ItemId)) continue;
            var cleanItemId = inventRow.ItemId.Trim();
            var cleanItemName = inventRow.ItemName ?? "";

            var cleanProductLine = (inventRow.ProductLine ?? "").Trim().ToUpperInvariant();
            var normalizedProductLine = cleanProductLine == "YP" ? "YP" : "HB";

            var itemRecord = new DrpMasItemRecord(cleanItemId, cleanItemName, normalizedProductLine, executionTimestamp);
            masterUpsertList.Add(itemRecord);
            activeMasterCatalog[cleanItemId] = itemRecord;
        }

        // 3. Step 1A: Process PMH & MTW Sales
        var rawSalesOrders = _repository.FetchSourceSalesOrders(startPeriodYearMonth, endPeriodYearMonth);
        var salesAggregation = new Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal>();

        foreach (var salesOrder in rawSalesOrders)
        {
            if (string.IsNullOrWhiteSpace(salesOrder.ItemId) || !salesOrder.DocDate.HasValue || string.IsNullOrWhiteSpace(salesOrder.DocumentType))
                continue;

            var cleanItemId = salesOrder.ItemId.Trim();
            if (!activeMasterCatalog.ContainsKey(cleanItemId))
                continue; // Only items present in DRP_MAS_ITEM

            var cleanDocumentType = salesOrder.DocumentType.Trim();
            if (!cleanDocumentType.Equals("Invoice", StringComparison.OrdinalIgnoreCase) &&
                !cleanDocumentType.Equals("CN", StringComparison.OrdinalIgnoreCase) &&
                !cleanDocumentType.Equals("Change", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Filter out Sample, Memo, etc.
            }

            var cleanCustomerId = (salesOrder.CustomerId ?? "").Trim();
            if (RegionalPharmacyCenters.Contains(cleanCustomerId))
                continue; // Exclude Regional Pharmacy Centers (1000001, 2003323, 5006299)

            var orderYearMonth = salesOrder.DocDate.Value.ToString("yyyyMM");
            if (string.Compare(orderYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(orderYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapPmhMtwWarehouse(salesOrder.Warehouse, salesOrder.CustGroup, salesOrder.PriceGroup);
            if (mappedWarehouse == null)
                continue; // Unmapped warehouse pair

            var conversionFactor = conversionFactorLookup.TryGetValue(cleanItemId, out var foundFactor) && foundFactor > 0 ? foundFactor : 1.0m;
            var rawQuantity = salesOrder.Qty ?? 0.0000m;
            var normalizedQuantity = rawQuantity * conversionFactor;

            var aggregationKey = (cleanItemId, mappedWarehouse, orderYearMonth);
            salesAggregation[aggregationKey] = salesAggregation.GetValueOrDefault(aggregationKey, 0.0000m) + normalizedQuantity;
        }

        // 4. Step 1B: Process POS Sales
        var rawPosSales = _repository.FetchSourcePosSales(startPeriodYearMonth, endPeriodYearMonth);
        foreach (var posSale in rawPosSales)
        {
            if (string.IsNullOrWhiteSpace(posSale.ItemId) || !posSale.DocDate.HasValue)
                continue; // ITEMID / DOCDATE IS NULL filtering

            var cleanItemId = posSale.ItemId.Trim();
            if (!activeMasterCatalog.ContainsKey(cleanItemId))
                continue;

            var orderYearMonth = posSale.DocDate.Value.ToString("yyyyMM");
            if (string.Compare(orderYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(orderYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapPosWarehouse(posSale.BranchId, posSale.VendorPa);
            if (mappedWarehouse == null)
                continue; // Unmapped branch

            var conversionFactor = conversionFactorLookup.TryGetValue(cleanItemId, out var foundFactor) && foundFactor > 0 ? foundFactor : 1.0m;
            var rawQuantity = posSale.Qty ?? 0.0000m;
            var normalizedQuantity = rawQuantity * conversionFactor;

            var aggregationKey = (cleanItemId, mappedWarehouse, orderYearMonth);
            salesAggregation[aggregationKey] = salesAggregation.GetValueOrDefault(aggregationKey, 0.0000m) + normalizedQuantity;
        }

        // 5. Step 1C: Process MySQL OOS
        var rawMysqlOos = _repository.FetchSourceMysqlOos(startPeriodYearMonth, endPeriodYearMonth);
        var oosAggregation = new Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal>();

        foreach (var wishlistOos in rawMysqlOos)
        {
            if (string.IsNullOrWhiteSpace(wishlistOos.ProductCode) || !wishlistOos.CreDate.HasValue)
                continue;

            var cleanItemId = wishlistOos.ProductCode.Trim();
            if (!activeMasterCatalog.ContainsKey(cleanItemId))
                continue; // Exclude items not in DRP_MAS_ITEM

            var cleanCustomerId = (wishlistOos.CustomerId ?? "").Trim();
            if (RegionalPharmacyCenters.Contains(cleanCustomerId))
                continue; // Exclude Regional Pharmacy Centers (1000001, 2003323, 5006299)

            var wishlistYearMonth = wishlistOos.CreDate.Value.ToString("yyyyMM");
            if (string.Compare(wishlistYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(wishlistYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapMysqlOosWarehouse(wishlistOos.InventLocation, wishlistOos.CustGroup);
            if (mappedWarehouse == null)
                continue;

            var rawQuantity = wishlistOos.Qty ?? 0.0000m;
            if (rawQuantity < 0) rawQuantity = 0.0000m;

            var conversionFactor = conversionFactorLookup.TryGetValue(cleanItemId, out var foundFactor) && foundFactor > 0 ? foundFactor : 1.0m;
            var normalizedQuantity = rawQuantity * conversionFactor;

            var aggregationKey = (cleanItemId, mappedWarehouse, wishlistYearMonth);
            oosAggregation[aggregationKey] = oosAggregation.GetValueOrDefault(aggregationKey, 0.0000m) + normalizedQuantity;
        }

        // 6. Build Staging Grid & Stage 0 OOS Capping
        var targetWarehouses = request.ActiveWarehouses != null && request.ActiveWarehouses.Count > 0
            ? request.ActiveWarehouses
            : (!string.IsNullOrEmpty(request.TargetWarehouseCode) ? new[] { request.TargetWarehouseCode } : AllTargetWarehouses);

        var existingStagingRecords = _repository.GetExistingStagingRecords();
        var stagingRecordsToInsert = new List<DrpStgDemand24MRecord>();

        if (executionMode.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            // Init mode: 25 months dense grid for all activeMasterCatalog x targetWarehouses x periodMonths
            foreach (var masterEntry in activeMasterCatalog)
            {
                var itemId = masterEntry.Key;
                foreach (var warehouseCode in targetWarehouses)
                {
                    // Compute max historical sales across the 25 months window for this (itemId, warehouseCode)
                    decimal maxHistoricalSales = 0.0000m;
                    foreach (var monthPeriod in periodMonths)
                    {
                        var salesAmount = salesAggregation.GetValueOrDefault((itemId, warehouseCode, monthPeriod), 0.0000m);
                        if (salesAmount > maxHistoricalSales) maxHistoricalSales = salesAmount;
                    }

                    foreach (var monthPeriod in periodMonths)
                    {
                        var rawSalesQuantity = salesAggregation.GetValueOrDefault((itemId, warehouseCode, monthPeriod), 0.0000m);
                        var rawOosQuantity = oosAggregation.GetValueOrDefault((itemId, warehouseCode, monthPeriod), 0.0000m);

                        // Stage 0 OOS Capping formula: MIN(MAX(0, raw_oos), max_hist_sales)
                        var cappedOosQuantity = Math.Min(Math.Max(0.0000m, rawOosQuantity), maxHistoricalSales);

                        stagingRecordsToInsert.Add(new DrpStgDemand24MRecord(
                            itemId,
                            warehouseCode,
                            monthPeriod,
                            rawSalesQuantity,
                            cappedOosQuantity,
                            executionTimestamp
                        ));
                    }
                }
            }
        }
        else
        {
            // NonInit mode: Process targetPeriodYearMonth (M-1)
            // Historical window for maxHistoricalSales is 23 months from Staging + current M-1 sales
            DateTime baseDate = DateTime.ParseExact(targetPeriodYearMonth, "yyyyMM", CultureInfo.InvariantCulture);
            var historical23Months = new HashSet<string>();
            for (int monthOffset = 24; monthOffset >= 2; monthOffset--)
            {
                historical23Months.Add(baseDate.AddMonths(-monthOffset).ToString("yyyyMM"));
            }

            foreach (var masterEntry in activeMasterCatalog)
            {
                var itemId = masterEntry.Key;
                foreach (var warehouseCode in targetWarehouses)
                {
                    // Find max sales in Staging for previous 23 months (M-24 to M-2)
                    decimal maxStagingSales = existingStagingRecords
                        .Where(record => record.ItemId == itemId && record.WarehouseCode == warehouseCode && historical23Months.Contains(record.YearMonth))
                        .Select(record => record.RawSalesQty)
                        .DefaultIfEmpty(0.0000m)
                        .Max();

                    var currentSales = salesAggregation.GetValueOrDefault((itemId, warehouseCode, targetPeriodYearMonth), 0.0000m);
                    var maxHistoricalSales = Math.Max(0.0000m, Math.Max(maxStagingSales, currentSales));

                    var rawOosQuantity = oosAggregation.GetValueOrDefault((itemId, warehouseCode, targetPeriodYearMonth), 0.0000m);
                    var cappedOosQuantity = Math.Min(Math.Max(0.0000m, rawOosQuantity), maxHistoricalSales);

                    stagingRecordsToInsert.Add(new DrpStgDemand24MRecord(
                        itemId,
                        warehouseCode,
                        targetPeriodYearMonth,
                        currentSales,
                        cappedOosQuantity,
                        executionTimestamp
                    ));
                }
            }
        }

        // 7. Atomic Transactional Write
        _repository.BeginTransaction();
        try
        {
            if (masterUpsertList.Count > 0)
            {
                _repository.UpsertMasterItems(masterUpsertList);
            }

            _repository.DeleteStagingScope(executionMode, targetPeriodYearMonth, request.TargetWarehouseCode);
            _repository.BulkInsertStaging(stagingRecordsToInsert);

            _repository.CommitTransaction();
        }
        catch
        {
            _repository.RollbackTransaction();
            throw;
        }
    }

    /// <summary>
    /// PMH & MTW 5-Warehouse Mapping (Section 2.1 & FM-00 Step 2.1 in PBI-01)
    /// CUSTTABLE.WAREHOUSE + CUSTTABLE.CUSTGROUP / CUSTTABLE.PRICEGROUP
    /// </summary>
    public static string? MapPmhMtwWarehouse(string? warehouse, string? custGroup, string? priceGroup)
    {
        var cleanWarehouse = (warehouse ?? "").Trim().ToUpperInvariant();
        var cleanCustGroup = (custGroup ?? "").Trim().ToUpperInvariant();
        var cleanPriceGroup = (priceGroup ?? "").Trim().ToUpperInvariant();

        if (cleanWarehouse == "WH01" && cleanCustGroup == "DOM") return "MDC (B2B)";
        if (cleanWarehouse == "WHCM01" && cleanCustGroup == "DOMCM") return "RDC-CM (B2B)";
        if (cleanWarehouse == "WHPT01" && cleanCustGroup == "DOMPT") return "RDC-PT (B2B)";

        if (cleanWarehouse == "MW01")
        {
            if (cleanPriceGroup == "ONLINE") return "MTW (B2C)";
            return "MTW (B2B)"; // PRICEGROUP <> 'ONLINE', including empty string and NULL
        }

        return null;
    }

    /// <summary>
    /// POS 5-Warehouse Mapping (DEC-PBI01-09, DEC-PBI01-10, DEC-PBI01-13 in PBI-01)
    /// fashof_data.dbo.BRANCH.BRANCHID + fashof_data.dbo.BRANCH.VENDORPA
    /// </summary>
    public static string? MapPosWarehouse(string? branchId, string? vendorPa)
    {
        var cleanBranchId = (branchId ?? "").Trim().ToUpperInvariant();
        var cleanVendorPa = (vendorPa ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(cleanBranchId)) return null;

        // 1. ONLINE WH: BRANCHID = '1111111'
        if (cleanBranchId == "1111111") return "ONLINE WH";

        // 2. RDC-PT (B2C): BRANCHID <> '1111111' AND VENDORPA = '90003'
        if (cleanVendorPa == "90003") return "RDC-PT (B2C)";

        // 3. RDC-CM (B2C): BRANCHID <> '1111111' AND VENDORPA = '90002'
        if (cleanVendorPa == "90002") return "RDC-CM (B2C)";

        // 4. RDC-SR (B2C): BRANCHID = '1000001' AND VENDORPA = '10746'
        if (cleanBranchId == "1000001" && cleanVendorPa == "10746") return "RDC-SR (B2C)";

        // 5. MDC (B2CXSR): BRANCHID NOT IN ('1000001', '1111111') AND VENDORPA = '10746'
        if (cleanVendorPa == "10746") return "MDC (B2CXSR)";

        return null;
    }

    /// <summary>
    /// MySQL OOS 3-Warehouse Mapping (DEC-MVP05-01 in PBI-01)
    /// admin_db.customers.inventlocation + admin_db.customers.custgroup
    /// </summary>
    public static string? MapMysqlOosWarehouse(string? inventLocation, string? custGroup)
    {
        var cleanInventLocation = (inventLocation ?? "").Trim().ToUpperInvariant();
        var cleanCustGroup = (custGroup ?? "").Trim().ToUpperInvariant();

        if (cleanInventLocation == "WH01" && cleanCustGroup == "DOM") return "MDC (B2B)";
        if (cleanInventLocation == "WHCM01" && cleanCustGroup == "DOMCM") return "RDC-CM (B2B)";
        if (cleanInventLocation == "WHPT01" && cleanCustGroup == "DOMPT") return "RDC-PT (B2B)";

        return null;
    }
}
