using System.Globalization;
using DRP_Project.src.Ingestion;

namespace DRP_Project.Tests;

public static class IngestionTests
{
    public static void RunAllTests()
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine("🧪 RUNNING SUITE: PBI-01 Direct SQL Demand View (TC-01-01 to TC-01-25)");
        Console.WriteLine("========================================================");

        Test_TC01_01_MasterCatalogSyncAndProductLineNormalization();
        Test_TC01_02_InitMode25MonthFullDumpAndDenseZeroFill();
        Test_TC01_03_PmhAndMtw5WarehouseRoutingAndPharmacyCenterExclusion();
        Test_TC01_04_DocumentTypeFilteringNettingAndNegativeSalesPreservation();
        Test_TC01_05_Pos5WarehouseRoutingAndInvalidRowFiltering();
        Test_TC01_06_MysqlOosExtractionWarehouseMappingAndDirtyRowFiltering();
        Test_TC01_07_Stage0OosSanityCapInInitAndNonInitModes();
        Test_TC01_08_ScopedIdempotencyAndManualOosEditPreservation();
        Test_TC01_09_AtomicTransactionRollbackOnBulkCopyFailure();
        Test_TC01_10_PosSalesMustNotApplyConversionFactor();
        Test_TC01_11_NonInitHistoricalWindowBoundaries();
        Test_TC01_12_InitMustPreserveStagingOutsideWindow();
        Test_TC01_13_InitWithTargetWarehouseScopesDeleteToThatWarehouse();
        Test_TC01_14_ActiveWarehousesSubsetScopesDeleteToSubset();
        Test_TC01_15_UnknownWarehouseCodeIsRejectedBeforeAnyWrite();
        Test_TC01_16_InvalidModeIsRejected();
        Test_TC01_17_InvalidPeriodFormatIsRejected();
        Test_TC01_18_OpenOrFuturePeriodIsRejected();
        Test_TC01_19_DefaultTargetPeriodIsLatestClosedMonth();
        Test_TC01_20_EmptyGridMustNotWipeExistingStaging();
        Test_TC01_21_QuantitiesAreRoundedToFourDecimals();
        Test_TC01_22_HistoricalMaxIsScopedPerItemAndWarehouse();
        Test_TC01_23_DefaultPeriodUsesThaiTime();
        Test_TC01_24_LongItemNameIsTruncatedTo200();
        Test_TC01_25_SkuMissingFromInventTableIsKeptWithFactorOne();

        Console.WriteLine("========================================================");
        Console.WriteLine("✅ ALL PBI-01 UNIT TESTS PASSED (25/25)");
        Console.WriteLine("========================================================\n");
    }

    /// <summary>
    /// TC-01-01: Master Catalog Sync, Product Line Normalization & Historical Preservation
    /// </summary>
    public static void Test_TC01_01_MasterCatalogSyncAndProductLineNormalization()
    {
        var repository = new MockDrpIngestionRepository();

        // Existing catalog has SKU-YP-01 and SKU-OLD-99
        repository.MasterItemsTable["SKU-YP-01"] = new DrpMasItemRecord("SKU-YP-01", "Old Name 1", "HB", DateTime.Parse("2026-08-01T00:00:00Z"));
        repository.MasterItemsTable["SKU-OLD-99"] = new DrpMasItemRecord("SKU-OLD-99", "Historical Item", "HB", DateTime.Parse("2025-01-01T00:00:00Z"));

        // Source INVENTTABLE contains 3 items
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-YP-01", "Paracetamol 500mg", "YP", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-YP-02", "Amoxicillin 250mg", " yp ", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-HB-01", null, "HB", 1.0m));

        var fixedExecutionTime = DateTime.Parse("2026-10-01T02:00:00Z");
        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202609", ExecutionTimestamp: fixedExecutionTime));

        var masterItems = repository.GetExistingMasterItems();
        Assert(masterItems.Count == 4, $"TC-01-01 Expected 4 items in DRP_MAS_ITEM, got {masterItems.Count}");

        // SKU-YP-01 updated
        Assert(masterItems["SKU-YP-01"].ItemName == "Paracetamol 500mg", "TC-01-01 SKU-YP-01 name mismatch");
        Assert(masterItems["SKU-YP-01"].ProductLine == "YP", "TC-01-01 SKU-YP-01 product line mismatch");
        Assert(masterItems["SKU-YP-01"].UpdatedAt == fixedExecutionTime, "TC-01-01 SKU-YP-01 updated_at mismatch");

        // SKU-YP-02 inserted with trimmed/uppercased 'YP'
        Assert(masterItems["SKU-YP-02"].ItemName == "Amoxicillin 250mg", "TC-01-02 SKU-YP-02 name mismatch");
        Assert(masterItems["SKU-YP-02"].ProductLine == "YP", "TC-01-02 SKU-YP-02 product line mismatch");

        // SKU-HB-01 inserted with null coalesced to ""
        Assert(masterItems["SKU-HB-01"].ItemName == "", "TC-01-01 SKU-HB-01 name should be empty string");
        Assert(masterItems["SKU-HB-01"].ProductLine == "HB", "TC-01-01 SKU-HB-01 product line mismatch");

        // SKU-OLD-99 preserved untouched
        Assert(masterItems["SKU-OLD-99"].ItemName == "Historical Item", "TC-01-01 SKU-OLD-99 should be preserved");
        Assert(masterItems["SKU-OLD-99"].UpdatedAt == DateTime.Parse("2025-01-01T00:00:00Z"), "TC-01-01 SKU-OLD-99 timestamp should remain intact");

        Console.WriteLine("[PASS] TC-01-01: Master Catalog Sync, Product Line Normalization & Historical Preservation");
    }

    /// <summary>
    /// TC-01-02: Init Mode 25-Month Full-Dump, Unit Conversion & Dense Zero-Fill
    /// </summary>
    public static void Test_TC01_02_InitMode25MonthFullDumpAndDenseZeroFill()
    {
        var repository = new MockDrpIngestionRepository();

        repository.SourceInventTable.Add(new RawInventTableRow("SKU-A", "Item A (Pack of 12)", "YP", 12.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-B", "Item B (Unit)", "HB", 1.0m));

        // Sales for SKU-A at MDC (B2B): 3 months with sales, 22 months with no sales
        // WAREHOUSE = 'WH01', CUSTGROUP = 'DOM' -> MDC (B2B)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-A", DateTime.Parse("2024-09-15"), "Invoice", "WH01", "CUST-01", "DOM", null, 10.0m)); // 10 * 12 = 120
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-A", DateTime.Parse("2025-03-20"), "Invoice", "WH01", "CUST-01", "DOM", null, 5.0m));  // 5 * 12 = 60
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-A", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 2.0m));  // 2 * 12 = 24
        // SKU-B has 0 sales across all 25 months

        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest(
            Mode: "Init",
            TargetPeriod: "202609",
            TargetWarehouseCode: "MDC (B2B)",
            ActiveWarehouses: new[] { "MDC (B2B)" }
        ));

        var stagingRecords = repository.GetExistingStagingRecords();
        Assert(stagingRecords.Count == 50, $"TC-01-02 Expected 50 rows (2 SKUs x 25M x 1 WH), got {stagingRecords.Count}");

        var recordA202409 = stagingRecords.Single(record => record.ItemId == "SKU-A" && record.YearMonth == "202409");
        Assert(recordA202409.RawSalesQty == 120.0000m, $"TC-01-02 SKU-A 202409 expected 120.0000m, got {recordA202409.RawSalesQty}");

        var recordA202503 = stagingRecords.Single(record => record.ItemId == "SKU-A" && record.YearMonth == "202503");
        Assert(recordA202503.RawSalesQty == 60.0000m, $"TC-01-02 SKU-A 202503 expected 60.0000m, got {recordA202503.RawSalesQty}");

        var recordA202608 = stagingRecords.Single(record => record.ItemId == "SKU-A" && record.YearMonth == "202608");
        Assert(recordA202608.RawSalesQty == 24.0000m, $"TC-01-02 SKU-A 202608 expected 24.0000m, got {recordA202608.RawSalesQty}");

        // Dense Zero-Fill check: 22 months for SKU-A must be 0.0000m
        var zeroMonthsForSkuA = stagingRecords.Where(record => record.ItemId == "SKU-A" && record.YearMonth != "202409" && record.YearMonth != "202503" && record.YearMonth != "202608").ToList();
        Assert(zeroMonthsForSkuA.Count == 22, $"TC-01-02 Expected 22 zero-fill months for SKU-A, got {zeroMonthsForSkuA.Count}");
        Assert(zeroMonthsForSkuA.All(record => record.RawSalesQty == 0.0000m && record.OosLostQty == 0.0000m), "TC-01-02 All 22 zero-fill months must be 0.0000m");

        // SKU-B all 25 months must be 0.0000m
        var recordsForSkuB = stagingRecords.Where(record => record.ItemId == "SKU-B").ToList();
        Assert(recordsForSkuB.Count == 25, $"TC-01-02 Expected 25 rows for SKU-B, got {recordsForSkuB.Count}");
        Assert(recordsForSkuB.All(record => record.RawSalesQty == 0.0000m && record.OosLostQty == 0.0000m), "TC-01-02 SKU-B must have 0.0000m across all 25 months");

        Console.WriteLine("[PASS] TC-01-02: Init Mode 25-Month Full-Dump, Unit Conversion & Dense Zero-Fill");
    }

    /// <summary>
    /// TC-01-03: PMH & MTW 5-Warehouse Routing & Regional Pharmacy Exclusion
    /// </summary>
    public static void Test_TC01_03_PmhAndMtw5WarehouseRoutingAndPharmacyCenterExclusion()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-ROUTE", "Routing Test Item", "HB", 1.0m));

        // Exact CUSTTABLE columns: WAREHOUSE, CUSTGROUP, PRICEGROUP
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 100.0m));     // MDC (B2B)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-02"), "Invoice", "WHCM01", "CUST-02", "DOMCM", null, 200.0m)); // RDC-CM (B2B)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-03"), "Invoice", "WHPT01", "CUST-03", "DOMPT", null, 300.0m)); // RDC-PT (B2B)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-04"), "Invoice", "MW01", "CUST-04", null, "ONLINE", 400.0m));    // MTW (B2C)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-05"), "Invoice", "MW01", "CUST-05", null, "WHS", 500.0m));       // MTW (B2B)
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-06"), "Invoice", "MW01", "CUST-06", null, null, 600.0m));        // MTW (B2B) via null price group

        // Exclusions:
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-07"), "Invoice", "WH01", "1000001", "DOM", null, 999.0m));     // Phitsanulok Center -> Exclude
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-08"), "Invoice", "WHCM01", "2003323", "DOMCM", null, 888.0m)); // Chiang Mai Center -> Exclude
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-09"), "Invoice", "WHPT01", "5006299", "DOMPT", null, 777.0m)); // Surat Thani Center -> Exclude
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUTE", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "CUST-99", "EXP", null, 111.0m));       // Unmapped CUSTGROUP 'EXP' -> Exclude

        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608"));

        var stagingRecords = repository.GetExistingStagingRecords();
        var mdcB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "MDC (B2B)");
        var cmB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-CM (B2B)");
        var ptB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-PT (B2B)");
        var mtwB2cRecord = stagingRecords.Single(record => record.WarehouseCode == "MTW (B2C)");
        var mtwB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "MTW (B2B)");

        Assert(mdcB2bRecord.RawSalesQty == 100.0000m, $"TC-01-03 MDC (B2B) expected 100, got {mdcB2bRecord.RawSalesQty}");
        Assert(cmB2bRecord.RawSalesQty == 200.0000m, $"TC-01-03 RDC-CM (B2B) expected 200, got {cmB2bRecord.RawSalesQty}");
        Assert(ptB2bRecord.RawSalesQty == 300.0000m, $"TC-01-03 RDC-PT (B2B) expected 300, got {ptB2bRecord.RawSalesQty}");
        Assert(mtwB2cRecord.RawSalesQty == 400.0000m, $"TC-01-03 MTW (B2C) expected 400, got {mtwB2cRecord.RawSalesQty}");
        Assert(mtwB2bRecord.RawSalesQty == 1100.0000m, $"TC-01-03 MTW (B2B) expected 1100 (500+600), got {mtwB2bRecord.RawSalesQty}");

        // Remaining 5 POS warehouses must be 0.0000m
        var posWarehouses = DemandIngestionEngine.AllTargetWarehouses.Except(new[] { "MDC (B2B)", "RDC-CM (B2B)", "RDC-PT (B2B)", "MTW (B2C)", "MTW (B2B)" });
        foreach (var warehouseCode in posWarehouses)
        {
            var record = stagingRecords.Single(x => x.WarehouseCode == warehouseCode);
            Assert(record.RawSalesQty == 0.0000m, $"TC-01-03 {warehouseCode} must be 0.0000m");
        }

        Console.WriteLine("[PASS] TC-01-03: PMH & MTW 5-Warehouse Routing & Regional Pharmacy Exclusion");
    }

    /// <summary>
    /// TC-01-04: Document Type Whitelist, Netting, Negative Sales Preservation & Factor Fallback
    /// </summary>
    public static void Test_TC01_04_DocumentTypeFilteringNettingAndNegativeSalesPreservation()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-NORM", "Normal Item", "HB", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-NEG", "Negative Item", "HB", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-FAC0", "Zero Factor Item", "HB", 0.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-FACN", "Null Factor Item", "HB", null));

        // SKU-NORM: Invoice 100, CN 20, Change 5, Sample 50 (excluded), Memo -10 (excluded) -> 100 - 20 + 5 = 85
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NORM", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 100.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NORM", DateTime.Parse("2026-08-02"), "CN", "WH01", "CUST-01", "DOM", null, -20.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NORM", DateTime.Parse("2026-08-03"), "Change", "WH01", "CUST-01", "DOM", null, 5.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NORM", DateTime.Parse("2026-08-04"), "Sample", "WH01", "CUST-01", "DOM", null, 50.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NORM", DateTime.Parse("2026-08-05"), "Memo", "WH01", "CUST-01", "DOM", null, -10.0m));

        // SKU-NEG: Invoice 10, CN -25 -> Net = -15
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NEG", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 10.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NEG", DateTime.Parse("2026-08-02"), "CN", "WH01", "CUST-01", "DOM", null, -25.0m));

        // SKU-FAC0: Invoice 12, Factor 0 -> Fallback to 1.0 -> 12 * 1.0 = 12
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-FAC0", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 12.0m));

        // SKU-FACN: Invoice 8, Factor NULL -> Fallback to 1.0 -> 8 * 1.0 = 8
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-FACN", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 8.0m));

        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }));

        var stagingRecords = repository.GetExistingStagingRecords();
        var normalRecord = stagingRecords.Single(record => record.ItemId == "SKU-NORM" && record.WarehouseCode == "MDC (B2B)");
        var negativeRecord = stagingRecords.Single(record => record.ItemId == "SKU-NEG" && record.WarehouseCode == "MDC (B2B)");
        var zeroFactorRecord = stagingRecords.Single(record => record.ItemId == "SKU-FAC0" && record.WarehouseCode == "MDC (B2B)");
        var nullFactorRecord = stagingRecords.Single(record => record.ItemId == "SKU-FACN" && record.WarehouseCode == "MDC (B2B)");

        Assert(normalRecord.RawSalesQty == 85.0000m, $"TC-01-04 SKU-NORM expected 85.0000m, got {normalRecord.RawSalesQty}");
        Assert(negativeRecord.RawSalesQty == -15.0000m, $"TC-01-04 SKU-NEG expected -15.0000m preserved, got {negativeRecord.RawSalesQty}");
        Assert(zeroFactorRecord.RawSalesQty == 12.0000m, $"TC-01-04 SKU-FAC0 expected 12.0000m, got {zeroFactorRecord.RawSalesQty}");
        Assert(nullFactorRecord.RawSalesQty == 8.0000m, $"TC-01-04 SKU-FACN expected 8.0000m, got {nullFactorRecord.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-04: Document Type Whitelist, Netting, Negative Sales Preservation & Factor Fallback");
    }

    /// <summary>
    /// TC-01-05: POS 5-Warehouse Routing, Branch Aggregation & Dirty Row Filtering
    /// Exact columns: BRANCHID and VENDORPA
    /// </summary>
    public static void Test_TC01_05_Pos5WarehouseRoutingAndInvalidRowFiltering()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-POS", "POS Test Item", "HB", 1.0m));

        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-01"), "1111111", "90001", 45.0m));  // ONLINE WH = 45
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-02"), "B101", "90003", 50.0m));     // RDC-PT (B2C)
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-03"), "B102", "90003", 30.0m));     // RDC-PT (B2C) sum = 80
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-04"), "B201", "90002", 60.0m));     // RDC-CM (B2C) = 60
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-05"), "1000001", "10746", 90.0m));   // RDC-SR (B2C) = 90
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-06"), "B301", "10746", 110.0m));    // MDC (B2CXSR) = 110

        // Invalid rows to be filtered:
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-07"), "B888", "99999", 99.0m));    // Unmapped VENDORPA -> Exclude
        repository.SourcePosSales.Add(new RawPosSalesRow(null, DateTime.Parse("2026-08-08"), "B101", "90003", 50.0m));          // ITEMID NULL -> Exclude
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", null, "B101", "90003", 50.0m));                              // DOCDATE NULL -> Exclude
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS", DateTime.Parse("2026-08-09"), "B101", "90003", null));       // QTY NULL -> 0.0000m

        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608"));

        var stagingRecords = repository.GetExistingStagingRecords();
        var onlineRecord = stagingRecords.Single(record => record.WarehouseCode == "ONLINE WH");
        var ptB2cRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-PT (B2C)");
        var cmB2cRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-CM (B2C)");
        var srB2cRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-SR (B2C)");
        var mdcB2cRecord = stagingRecords.Single(record => record.WarehouseCode == "MDC (B2CXSR)");

        Assert(onlineRecord.RawSalesQty == 45.0000m, $"TC-01-05 ONLINE WH expected 45, got {onlineRecord.RawSalesQty}");
        Assert(ptB2cRecord.RawSalesQty == 80.0000m, $"TC-01-05 RDC-PT (B2C) expected 80 (50+30+0), got {ptB2cRecord.RawSalesQty}");
        Assert(cmB2cRecord.RawSalesQty == 60.0000m, $"TC-01-05 RDC-CM (B2C) expected 60, got {cmB2cRecord.RawSalesQty}");
        Assert(srB2cRecord.RawSalesQty == 90.0000m, $"TC-01-05 RDC-SR (B2C) expected 90, got {srB2cRecord.RawSalesQty}");
        Assert(mdcB2cRecord.RawSalesQty == 110.0000m, $"TC-01-05 MDC (B2CXSR) expected 110, got {mdcB2cRecord.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-05: POS 5-Warehouse Routing, Branch Aggregation & Dirty Row Filtering");
    }

    /// <summary>
    /// TC-01-06: MySQL OOS Extraction, Warehouse Mapping & Dirty Row Filtering
    /// Exact columns: inventlocation and custgroup
    /// </summary>
    public static void Test_TC01_06_MysqlOosExtractionWarehouseMappingAndDirtyRowFiltering()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-OOS", "OOS Item (Box of 6)", "HB", 6.0m));

        // Valid wishlists:
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-02"), "CUST-01", "WH01", "DOM", 10.0m));   // MDC (B2B): 10 * 6 = 60
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-05"), "CUST-02", "WH01", "DOM", 5.0m));    // MDC (B2B): 5 * 6 = 30 -> Total MDC = 90
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-10"), "CUST-03", "WHCM01", "DOMCM", 10.0m)); // RDC-CM (B2B): 10 * 6 = 60
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-12"), "CUST-04", "WHPT01", "DOMPT", 10.0m)); // RDC-PT (B2B): 10 * 6 = 60

        // Exclusions:
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-03"), "1000001", "WH01", "DOM", 20.0m));    // Phitsanulok Center -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-04"), "2003323", "WHCM01", "DOMCM", 20.0m)); // Chiang Mai Center -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-05"), "5006299", "WHPT01", "DOMPT", 20.0m)); // Surat Thani Center -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-NOT-EXIST", DateTime.Parse("2026-08-06"), "CUST-01", "WH01", "DOM", 10.0m)); // Not in Master -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow(null, DateTime.Parse("2026-08-07"), "CUST-01", "WH01", "DOM", 10.0m));             // ProductCode NULL -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", null, "CUST-01", "WH01", "DOM", 10.0m));                                 // CreDate NULL -> Exclude
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-OOS", DateTime.Parse("2026-08-08"), "CUST-01", "WH01", "DOM", -5.0m));        // Qty < 0 -> 0.0000m

        // Add dummy sales so that Stage 0 OOS cap does not cap them to 0
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-OOS", DateTime.Parse("2026-08-01"), "Invoice", "WH01", "CUST-01", "DOM", null, 500.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-OOS", DateTime.Parse("2026-08-01"), "Invoice", "WHCM01", "CUST-01", "DOMCM", null, 500.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-OOS", DateTime.Parse("2026-08-01"), "Invoice", "WHPT01", "CUST-01", "DOMPT", null, 500.0m));

        var ingestionEngine = new DemandIngestionEngine(repository);
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608"));

        var stagingRecords = repository.GetExistingStagingRecords();
        var mdcB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "MDC (B2B)");
        var cmB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-CM (B2B)");
        var ptB2bRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-PT (B2B)");

        Assert(mdcB2bRecord.OosLostQty == 90.0000m, $"TC-01-06 MDC (B2B) expected 90, got {mdcB2bRecord.OosLostQty}");
        Assert(cmB2bRecord.OosLostQty == 60.0000m, $"TC-01-06 RDC-CM (B2B) expected 60, got {cmB2bRecord.OosLostQty}");
        Assert(ptB2bRecord.OosLostQty == 60.0000m, $"TC-01-06 RDC-PT (B2B) expected 60, got {ptB2bRecord.OosLostQty}");

        // Remaining 7 warehouses must have oos_lost_qty = 0.0000m
        var otherWarehouses = DemandIngestionEngine.AllTargetWarehouses.Except(new[] { "MDC (B2B)", "RDC-CM (B2B)", "RDC-PT (B2B)" });
        foreach (var warehouseCode in otherWarehouses)
        {
            var record = stagingRecords.Single(x => x.WarehouseCode == warehouseCode);
            Assert(record.OosLostQty == 0.0000m, $"TC-01-06 {warehouseCode} OOS must be 0.0000m");
        }

        Console.WriteLine("[PASS] TC-01-06: MySQL OOS Extraction, Warehouse Mapping & Dirty Row Filtering");
    }

    /// <summary>
    /// TC-01-07: Stage 0 OOS Sanity Cap in Init and NonInit Modes
    /// </summary>
    public static void Test_TC01_07_Stage0OosSanityCapInInitAndNonInitModes()
    {
        // ---------------- Sub-case 7A: Init Mode (25-Month In-Memory Window) ----------------
        var repositoryInit = new MockDrpIngestionRepository();
        repositoryInit.SourceInventTable.Add(new RawInventTableRow("SKU-CAP-01", "Item Cap 1", "HB", 1.0m));
        repositoryInit.SourceInventTable.Add(new RawInventTableRow("SKU-CAP-02", "Item Cap 2", "HB", 1.0m));
        repositoryInit.SourceInventTable.Add(new RawInventTableRow("SKU-CAP-03", "Item Cap 3", "HB", 1.0m));
        repositoryInit.SourceInventTable.Add(new RawInventTableRow("SKU-CAP-04", "Item Cap 4", "HB", 1.0m));

        // SKU-CAP-01: max hist sales = 150 (in 202501). In 202609: raw_oos = 300 -> Capped to 150
        repositoryInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-CAP-01", DateTime.Parse("2025-01-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 150.0m));
        repositoryInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-CAP-01", DateTime.Parse("2026-09-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 100.0m));
        repositoryInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-CAP-01", DateTime.Parse("2026-09-15"), "CUST-01", "WH01", "DOM", 300.0m));

        // SKU-CAP-02: max hist sales = 100 (in 202506). In 202609: raw_oos = 80 -> Uncapped = 80
        repositoryInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-CAP-02", DateTime.Parse("2025-06-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 100.0m));
        repositoryInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-CAP-02", DateTime.Parse("2026-09-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 50.0m));
        repositoryInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-CAP-02", DateTime.Parse("2026-09-15"), "CUST-01", "WH01", "DOM", 80.0m));

        // SKU-CAP-03: max hist sales = 200 (in 202609). In 202609: raw_oos = 200 -> Capped = 200 (Boundary Equality)
        repositoryInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-CAP-03", DateTime.Parse("2026-09-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 200.0m));
        repositoryInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-CAP-03", DateTime.Parse("2026-09-15"), "CUST-01", "WH01", "DOM", 200.0m));

        // SKU-CAP-04: zero sales throughout. In 202609: raw_oos = 50 -> Capped to 0.0000m (Zero-Sales Guard)
        repositoryInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-CAP-04", DateTime.Parse("2026-09-15"), "CUST-01", "WH01", "DOM", 50.0m));

        var ingestionEngineInit = new DemandIngestionEngine(repositoryInit);
        ingestionEngineInit.ExecuteIngestion(new IngestionJobRequest("Init", "202609", "MDC (B2B)", new[] { "MDC (B2B)" }));

        var stagingRecordsInit = repositoryInit.GetExistingStagingRecords();
        var cappedRecord1 = stagingRecordsInit.Single(record => record.ItemId == "SKU-CAP-01" && record.YearMonth == "202609");
        var cappedRecord2 = stagingRecordsInit.Single(record => record.ItemId == "SKU-CAP-02" && record.YearMonth == "202609");
        var cappedRecord3 = stagingRecordsInit.Single(record => record.ItemId == "SKU-CAP-03" && record.YearMonth == "202609");
        var cappedRecord4 = stagingRecordsInit.Single(record => record.ItemId == "SKU-CAP-04" && record.YearMonth == "202609");

        Assert(cappedRecord1.OosLostQty == 150.0000m, $"TC-01-07 Init SKU-CAP-01 expected 150.0000m, got {cappedRecord1.OosLostQty}");
        Assert(cappedRecord2.OosLostQty == 80.0000m, $"TC-01-07 Init SKU-CAP-02 expected 80.0000m, got {cappedRecord2.OosLostQty}");
        Assert(cappedRecord3.OosLostQty == 200.0000m, $"TC-01-07 Init SKU-CAP-03 expected 200.0000m, got {cappedRecord3.OosLostQty}");
        Assert(cappedRecord4.OosLostQty == 0.0000m, $"TC-01-07 Init SKU-CAP-04 expected 0.0000m, got {cappedRecord4.OosLostQty}");

        // ---------------- Sub-case 7B: NonInit Mode (M-1 vs 23M in Staging) ----------------
        var repositoryNonInit = new MockDrpIngestionRepository();
        repositoryNonInit.SourceInventTable.Add(new RawInventTableRow("SKU-NONINIT-01", "NonInit Item 1", "HB", 1.0m));
        repositoryNonInit.SourceInventTable.Add(new RawInventTableRow("SKU-NONINIT-02", "NonInit Item 2", "HB", 1.0m));

        // Staging historical rows (23 months: 202409 to 202607)
        // SKU-NONINIT-01: max historical sales in 23M = 300 (in 202511)
        repositoryNonInit.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-NONINIT-01", "MDC (B2B)", "202511", 300.0m, 0.0m, DateTime.UtcNow));
        repositoryNonInit.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-NONINIT-01", "MDC (B2B)", "202607", 100.0m, 0.0m, DateTime.UtcNow));

        // SKU-NONINIT-02: max historical sales in 23M = 120 (in 202508)
        repositoryNonInit.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-NONINIT-02", "MDC (B2B)", "202508", 120.0m, 0.0m, DateTime.UtcNow));

        // In 202608:
        // SKU-NONINIT-01: sales = 150, raw_oos = 500 -> Max sales = 300 -> Capped to 300
        repositoryNonInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NONINIT-01", DateTime.Parse("2026-08-05"), "Invoice", "WH01", "CUST-01", "DOM", null, 150.0m));
        repositoryNonInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-NONINIT-01", DateTime.Parse("2026-08-10"), "CUST-01", "WH01", "DOM", 500.0m));

        // SKU-NONINIT-02: sales = 80, raw_oos = 90 -> Max sales = 120 -> Uncapped = 90
        repositoryNonInit.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-NONINIT-02", DateTime.Parse("2026-08-05"), "Invoice", "WH01", "CUST-01", "DOM", null, 80.0m));
        repositoryNonInit.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-NONINIT-02", DateTime.Parse("2026-08-10"), "CUST-01", "WH01", "DOM", 90.0m));

        var ingestionEngineNonInit = new DemandIngestionEngine(repositoryNonInit);
        ingestionEngineNonInit.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }));

        var stagingRecordsNonInit = repositoryNonInit.GetExistingStagingRecords();
        var nonInitRecord1 = stagingRecordsNonInit.Single(record => record.ItemId == "SKU-NONINIT-01" && record.YearMonth == "202608");
        var nonInitRecord2 = stagingRecordsNonInit.Single(record => record.ItemId == "SKU-NONINIT-02" && record.YearMonth == "202608");

        Assert(nonInitRecord1.OosLostQty == 300.0000m, $"TC-01-07 NonInit SKU-NONINIT-01 expected 300.0000m, got {nonInitRecord1.OosLostQty}");
        Assert(nonInitRecord2.OosLostQty == 90.0000m, $"TC-01-07 NonInit SKU-NONINIT-02 expected 90.0000m, got {nonInitRecord2.OosLostQty}");

        Console.WriteLine("[PASS] TC-01-07: Stage 0 OOS Sanity Cap in Init and NonInit Modes");
    }

    /// <summary>
    /// TC-01-08: Scoped Idempotency & Manual User OOS Edit Preservation
    /// </summary>
    public static void Test_TC01_08_ScopedIdempotencyAndManualOosEditPreservation()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-IDEM", "Idempotency Item", "HB", 1.0m));

        // Existing Staging Records:
        // 1. Prior month (202607) at MDC (B2B) where user manually edited oos_lost_qty = 150.0000m
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-IDEM", "MDC (B2B)", "202607", 100.0m, 150.0m, DateTime.Parse("2026-08-01T00:00:00Z")));
        // 2. Target month (202608) at different warehouse RDC-CM (B2B) = 50.0m
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-IDEM", "RDC-CM (B2B)", "202608", 50.0m, 0.0m, DateTime.Parse("2026-09-01T00:00:00Z")));
        // 3. Target month (202608) at target warehouse MDC (B2B) = old sales 120.0m
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-IDEM", "MDC (B2B)", "202608", 120.0m, 0.0m, DateTime.Parse("2026-09-01T00:00:00Z")));

        // New source data for 202608 at MDC (B2B): updated sales = 220.0m
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-IDEM", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 220.0m));

        var ingestionEngine = new DemandIngestionEngine(repository);
        // Execute scoped sync for NonInit, 202608, MDC (B2B)
        ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }));

        var stagingRecords = repository.GetExistingStagingRecords();
        Assert(stagingRecords.Count == 3, $"TC-01-08 Expected exactly 3 rows in Staging, got {stagingRecords.Count}");

        // Row 1: Prior month manual edit must be strictly preserved
        var priorMonthRecord = stagingRecords.Single(record => record.WarehouseCode == "MDC (B2B)" && record.YearMonth == "202607");
        Assert(priorMonthRecord.OosLostQty == 150.0000m, $"TC-01-08 Manual OOS edit in 202607 must remain 150.0000m, got {priorMonthRecord.OosLostQty}");
        Assert(priorMonthRecord.RawSalesQty == 100.0000m, "TC-01-08 Prior month sales must remain 100.0000m");

        // Row 2: Different warehouse in target month must remain untouched
        var otherWarehouseRecord = stagingRecords.Single(record => record.WarehouseCode == "RDC-CM (B2B)" && record.YearMonth == "202608");
        Assert(otherWarehouseRecord.RawSalesQty == 50.0000m, "TC-01-08 Other warehouse sales must remain untouched");

        // Row 3: Scoped target row must be updated to 220.0000m without duplicate
        var targetMonthRecord = stagingRecords.Single(record => record.WarehouseCode == "MDC (B2B)" && record.YearMonth == "202608");
        Assert(targetMonthRecord.RawSalesQty == 220.0000m, $"TC-01-08 Target row must be cleanly replaced with 220.0000m, got {targetMonthRecord.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-08: Scoped Idempotency & Manual User OOS Edit Preservation");
    }

    /// <summary>
    /// TC-01-09: Atomic Database Transaction Rollback on Bulk Copy Failure
    /// </summary>
    public static void Test_TC01_09_AtomicTransactionRollbackOnBulkCopyFailure()
    {
        var repository = new MockDrpIngestionRepository();

        // Initial database state
        repository.MasterItemsTable["SKU-01"] = new DrpMasItemRecord("SKU-01", "Old Name", "HB", DateTime.Parse("2026-08-01T00:00:00Z"));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-01", "MDC (B2B)", "202607", 100.0m, 0.0m, DateTime.Parse("2026-08-01T00:00:00Z")));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-01", "MDC (B2B)", "202608", 110.0m, 0.0m, DateTime.Parse("2026-09-01T00:00:00Z")));

        // Source data introduces changes: SKU-01 updated to "New Name", SKU-02 inserted
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-01", "New Name", "YP", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-02", "Item 2", "HB", 1.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-01", DateTime.Parse("2026-08-15"), "Invoice", "WH01", "CUST-01", "DOM", null, 500.0m));

        // Inject Bulk Insert Failure
        repository.SimulateBulkInsertFailure = true;

        var ingestionEngine = new DemandIngestionEngine(repository);

        bool exceptionThrown = false;
        try
        {
            ingestionEngine.ExecuteIngestion(new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }));
        }
        catch (InvalidOperationException ex)
        {
            exceptionThrown = true;
            Assert(ex.Message.Contains("Simulated SQL Bulk Copy Failure"), "Unexpected exception message");
        }

        Assert(exceptionThrown, "TC-01-09 Expected InvalidOperationException to be thrown");

        // Verify Master Items rollback
        var masterItems = repository.GetExistingMasterItems();
        Assert(masterItems.Count == 1, $"TC-01-09 Expected master count to remain 1, got {masterItems.Count}");
        Assert(masterItems["SKU-01"].ItemName == "Old Name", "TC-01-09 SKU-01 name must be rolled back to 'Old Name'");
        Assert(masterItems["SKU-01"].ProductLine == "HB", "TC-01-09 SKU-01 product line must be rolled back to 'HB'");
        Assert(!masterItems.ContainsKey("SKU-02"), "TC-01-09 SKU-02 must NOT be present in Master Items");

        // Verify Staging Table rollback
        var stagingRecords = repository.GetExistingStagingRecords();
        Assert(stagingRecords.Count == 2, $"TC-01-09 Expected staging count to remain 2, got {stagingRecords.Count}");
        Assert(stagingRecords.Any(record => record.YearMonth == "202607" && record.RawSalesQty == 100.0m), "TC-01-09 Row 202607 must be preserved");
        Assert(stagingRecords.Any(record => record.YearMonth == "202608" && record.RawSalesQty == 110.0m), "TC-01-09 Row 202608 must be preserved with original 110.0m");

        Console.WriteLine("[PASS] TC-01-09: Atomic Database Transaction Rollback on Bulk Copy Failure");
    }

    /// <summary>
    /// TC-01-10: SALESPFN.QTY is already POS Unit -> must NOT be multiplied by FACTOR_SALESTOBASEUNIT
    /// (PMH/MTW side still converts: covered by TC-01-02 / TC-01-04)
    /// </summary>
    public static void Test_TC01_10_PosSalesMustNotApplyConversionFactor()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-POS-F12", "POS Factor Item", "HB", 12.0m));
        repository.SourcePosSales.Add(new RawPosSalesRow("SKU-POS-F12", DateTime.Parse("2026-08-02"), "B101", "90003", 10.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", TargetWarehouseCode: "RDC-PT (B2C)", ActiveWarehouses: new[] { "RDC-PT (B2C)" }));

        var record = repository.GetExistingStagingRecords().Single(r => r.ItemId == "SKU-POS-F12" && r.WarehouseCode == "RDC-PT (B2C)");
        Assert(record.RawSalesQty == 10.0000m, $"TC-01-10 PFN qty is POS Unit already, expected 10.0000m (no factor), got {record.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-10: POS (PFN) sales are not multiplied by conversion factor");
    }

    /// <summary>
    /// TC-01-11: NonInit max_hist_sales window = target-23 .. target-1 from Staging (+ current month).
    /// target 202608 -> 202409..202607 counted; 202408 (target-24) must NOT be counted.
    /// </summary>
    public static void Test_TC01_11_NonInitHistoricalWindowBoundaries()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-WIN-NEWEST", "Max at target-1", "HB", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-WIN-OLDEST", "Max at target-24", "HB", 1.0m));

        // target-1 (202607) is inside the window -> must be counted
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-WIN-NEWEST", "MDC (B2B)", "202607", 500.0m, 0.0m, DateTime.UtcNow));
        // target-24 (202408) is outside the window -> must be ignored
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-WIN-OLDEST", "MDC (B2B)", "202408", 700.0m, 0.0m, DateTime.UtcNow));

        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-WIN-NEWEST", DateTime.Parse("2026-08-05"), "Invoice", "WH01", "CUST-01", "DOM", null, 10.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-WIN-OLDEST", DateTime.Parse("2026-08-05"), "Invoice", "WH01", "CUST-01", "DOM", null, 20.0m));
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-WIN-NEWEST", DateTime.Parse("2026-08-10"), "CUST-01", "WH01", "DOM", 1000.0m));
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-WIN-OLDEST", DateTime.Parse("2026-08-10"), "CUST-01", "WH01", "DOM", 1000.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }));

        var staging = repository.GetExistingStagingRecords();
        var newest = staging.Single(r => r.ItemId == "SKU-WIN-NEWEST" && r.YearMonth == "202608");
        var oldest = staging.Single(r => r.ItemId == "SKU-WIN-OLDEST" && r.YearMonth == "202608");

        Assert(newest.OosLostQty == 500.0000m, $"TC-01-11 202607 (target-1) must be counted: expected cap 500.0000m, got {newest.OosLostQty}");
        Assert(oldest.OosLostQty == 20.0000m, $"TC-01-11 202408 (target-24) must be ignored: expected cap 20.0000m (current month only), got {oldest.OosLostQty}");

        Console.WriteLine("[PASS] TC-01-11: NonInit historical window boundaries (target-23..target-1)");
    }

    /// <summary>
    /// TC-01-12: Init deletes only 202409..202609. Rows outside the window (accumulated history) must survive.
    /// </summary>
    public static void Test_TC01_12_InitMustPreserveStagingOutsideWindow()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-INIT", "Init Item", "HB", 1.0m));

        // Outside window (202301): must be preserved with original values (e.g. user-edited OOS)
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-INIT", "MDC (B2B)", "202301", 40.0m, 15.0m, DateTime.Parse("2023-02-01T00:00:00Z")));
        // Inside window (202501): stale row must be replaced by the dense grid (zero-fill)
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-INIT", "MDC (B2B)", "202501", 999.0m, 0.0m, DateTime.Parse("2025-02-01T00:00:00Z")));

        new DemandIngestionEngine(repository).ExecuteIngestion(new IngestionJobRequest("Init", "202609"));

        var staging = repository.GetExistingStagingRecords();
        Assert(staging.Count == 251, $"TC-01-12 Expected 251 rows (1 SKU x 10 WH x 25M + 1 preserved), got {staging.Count}");

        var preserved = staging.Single(r => r.WarehouseCode == "MDC (B2B)" && r.YearMonth == "202301");
        Assert(preserved.RawSalesQty == 40.0000m && preserved.OosLostQty == 15.0000m, "TC-01-12 Row outside Init window must be preserved untouched");

        var replaced = staging.Single(r => r.WarehouseCode == "MDC (B2B)" && r.YearMonth == "202501");
        Assert(replaced.RawSalesQty == 0.0000m, $"TC-01-12 Row inside Init window must be replaced (expected 0.0000m), got {replaced.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-12: Init deletes only its 25-month window");
    }

    /// <summary>
    /// TC-01-13: Init with TargetWarehouseCode replaces all 25 months of that warehouse only.
    /// </summary>
    public static void Test_TC01_13_InitWithTargetWarehouseScopesDeleteToThatWarehouse()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-INIT-WH", "Init WH Item", "HB", 1.0m));

        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-INIT-WH", "MDC (B2B)", "202501", 999.0m, 0.0m, DateTime.UtcNow));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-INIT-WH", "RDC-CM (B2B)", "202501", 77.0m, 0.0m, DateTime.UtcNow));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("Init", "202609", TargetWarehouseCode: "MDC (B2B)"));

        var staging = repository.GetExistingStagingRecords();
        // MDC (B2B): 25 rows exactly (no duplicate of 202501) + RDC-CM (B2B): 1 untouched row
        Assert(staging.Count(r => r.WarehouseCode == "MDC (B2B)") == 25, "TC-01-13 MDC (B2B) must have exactly 25 rows (no PK duplicate)");
        var other = staging.Single(r => r.WarehouseCode == "RDC-CM (B2B)");
        Assert(other.RawSalesQty == 77.0000m, "TC-01-13 Other warehouse must remain untouched");

        Console.WriteLine("[PASS] TC-01-13: Init with TargetWarehouseCode only touches that warehouse");
    }

    /// <summary>
    /// TC-01-14: ActiveWarehouses subset (TargetWarehouseCode = null): delete scope == insert scope.
    /// </summary>
    public static void Test_TC01_14_ActiveWarehousesSubsetScopesDeleteToSubset()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-SUBSET", "Subset Item", "HB", 1.0m));

        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-SUBSET", "RDC-CM (B2B)", "202608", 50.0m, 0.0m, DateTime.UtcNow));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-SUBSET", "MDC (B2B)", "202608", 120.0m, 0.0m, DateTime.UtcNow));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", TargetWarehouseCode: null, ActiveWarehouses: new[] { "MDC (B2B)" }));

        var staging = repository.GetExistingStagingRecords();
        Assert(staging.Count == 2, $"TC-01-14 Expected 2 rows, got {staging.Count}");
        Assert(staging.Single(r => r.WarehouseCode == "RDC-CM (B2B)").RawSalesQty == 50.0000m, "TC-01-14 Warehouse outside ActiveWarehouses must not be deleted");
        Assert(staging.Single(r => r.WarehouseCode == "MDC (B2B)").RawSalesQty == 0.0000m, "TC-01-14 Warehouse in ActiveWarehouses must be replaced");

        Console.WriteLine("[PASS] TC-01-14: ActiveWarehouses subset scopes the delete");
    }

    /// <summary>
    /// TC-01-15: Unknown warehouse code must be rejected before any write (no junk rows, nothing deleted).
    /// </summary>
    public static void Test_TC01_15_UnknownWarehouseCodeIsRejectedBeforeAnyWrite()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-BAD-WH", "Bad WH Item", "HB", 1.0m));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-BAD-WH", "MDC (B2B)", "202608", 120.0m, 0.0m, DateTime.UtcNow));

        bool thrown = false;
        try
        {
            new DemandIngestionEngine(repository).ExecuteIngestion(
                new IngestionJobRequest("NonInit", "202608", TargetWarehouseCode: "NOT-A-WAREHOUSE"));
        }
        catch (ArgumentException)
        {
            thrown = true;
        }

        Assert(thrown, "TC-01-15 Expected ArgumentException for unknown warehouse code");
        Assert(repository.GetExistingStagingRecords().Count == 1 && repository.MasterItemsTable.Count == 0,
            "TC-01-15 No write may happen when the request is rejected");

        Console.WriteLine("[PASS] TC-01-15: Unknown warehouse code is rejected before any write");
    }

    // เวลารันคงที่สำหรับ Test ชุดนี้: M-1 = 202608 (ไม่ขึ้นกับวันที่จริงของเครื่อง)
    private static readonly DateTime AsOfSeptember2026 = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// TC-01-16: Mode ต้องเป็น 'Init' หรือ 'NonInit' เท่านั้น (พิมพ์ผิดต้อง throw ไม่ตกไปเป็น NonInit เงียบๆ)
    /// </summary>
    public static void Test_TC01_16_InvalidModeIsRejected()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-MODE", "Mode Item", "HB", 1.0m));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-MODE", "MDC (B2B)", "202608", 10.0m, 0.0m, DateTime.UtcNow));

        bool thrown = false;
        try
        {
            new DemandIngestionEngine(repository).ExecuteIngestion(
                new IngestionJobRequest("Intt", "202608", ExecutionTimestamp: AsOfSeptember2026));
        }
        catch (ArgumentException) { thrown = true; }

        Assert(thrown, "TC-01-16 Expected ArgumentException for unknown mode");
        Assert(repository.GetExistingStagingRecords().Count == 1 && repository.MasterItemsTable.Count == 0, "TC-01-16 No write may happen when the request is rejected");

        Console.WriteLine("[PASS] TC-01-16: Invalid mode is rejected");
    }

    /// <summary>
    /// TC-01-17: TargetPeriod ต้องเป็น yyyyMM ที่ถูกต้อง (ทั้งโหมด NonInit และ Init)
    /// </summary>
    public static void Test_TC01_17_InvalidPeriodFormatIsRejected()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-FMT", "Format Item", "HB", 1.0m));

        foreach (var mode in new[] { "NonInit", "Init" })
        {
            foreach (var badPeriod in new[] { "2026-08", "202613", "20268", "abcdef", "202600" })
            {
                bool thrown = false;
                try
                {
                    new DemandIngestionEngine(repository).ExecuteIngestion(
                        new IngestionJobRequest(mode, badPeriod, ExecutionTimestamp: AsOfSeptember2026));
                }
                catch (ArgumentException) { thrown = true; }

                Assert(thrown, $"TC-01-17 Expected ArgumentException for mode={mode}, period='{badPeriod}'");
            }
        }

        Assert(repository.GetExistingStagingRecords().Count == 0 && repository.MasterItemsTable.Count == 0, "TC-01-17 No write may happen when the request is rejected");

        Console.WriteLine("[PASS] TC-01-17: Invalid period format is rejected");
    }

    /// <summary>
    /// TC-01-18: Strict Closed Calendar Month: งวดปัจจุบันและอนาคตต้องถูกปฏิเสธ (as-of 2026-09-15 -> ใช้ได้ถึง 202608)
    /// </summary>
    public static void Test_TC01_18_OpenOrFuturePeriodIsRejected()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-CLOSED", "Closed Period Item", "HB", 1.0m));

        foreach (var openPeriod in new[] { "202609", "202610" })
        {
            bool thrown = false;
            try
            {
                new DemandIngestionEngine(repository).ExecuteIngestion(
                    new IngestionJobRequest("NonInit", openPeriod, ExecutionTimestamp: AsOfSeptember2026));
            }
            catch (ArgumentException) { thrown = true; }

            Assert(thrown, $"TC-01-18 Expected ArgumentException for open/future period {openPeriod}");
        }
        Assert(repository.GetExistingStagingRecords().Count == 0, "TC-01-18 No write may happen when the request is rejected");

        // งวด M-1 (202608) ต้องผ่าน
        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", ExecutionTimestamp: AsOfSeptember2026));
        Assert(repository.GetExistingStagingRecords().Count == 10, "TC-01-18 Closed period 202608 must be accepted (1 SKU x 10 WH)");

        Console.WriteLine("[PASS] TC-01-18: Open/future period is rejected, M-1 accepted");
    }

    /// <summary>
    /// TC-01-19: ไม่ระบุ TargetPeriod -> Default = M-1 (เดือนก่อนหน้าของเวลารัน)
    /// </summary>
    public static void Test_TC01_19_DefaultTargetPeriodIsLatestClosedMonth()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-DEFAULT", "Default Period Item", "HB", 1.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-DEFAULT", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 7.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", null, ExecutionTimestamp: AsOfSeptember2026));

        var staging = repository.GetExistingStagingRecords();
        Assert(staging.Count == 10 && staging.All(r => r.YearMonth == "202608"), "TC-01-19 Default period must be 202608 for all rows");
        Assert(staging.Single(r => r.WarehouseCode == "MDC (B2B)").RawSalesQty == 7.0000m, "TC-01-19 Sales of 202608 must be ingested");

        Console.WriteLine("[PASS] TC-01-19: Default TargetPeriod = M-1");
    }

    /// <summary>
    /// TC-01-20: Guard กันล้างตาราง: ถ้าไม่มีแถวให้เขียนเลย (Master ว่าง) ต้องหยุดก่อนลบข้อมูลเดิม
    /// </summary>
    public static void Test_TC01_20_EmptyGridMustNotWipeExistingStaging()
    {
        var repository = new MockDrpIngestionRepository();
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-LEGACY", "MDC (B2B)", "202608", 120.0m, 5.0m, DateTime.UtcNow));

        bool thrown = false;
        try
        {
            new DemandIngestionEngine(repository).ExecuteIngestion(
                new IngestionJobRequest("NonInit", "202608", ExecutionTimestamp: AsOfSeptember2026));
        }
        catch (InvalidOperationException) { thrown = true; }

        Assert(thrown, "TC-01-20 Expected InvalidOperationException when there is nothing to write");
        Assert(repository.GetExistingStagingRecords().Count == 1, "TC-01-20 Existing staging row must NOT be deleted");

        Console.WriteLine("[PASS] TC-01-20: Empty grid does not wipe existing staging");
    }

    /// <summary>
    /// TC-01-21: ปัดเศษ 4 ตำแหน่ง (NUMERIC(38,4)) หลังรวมยอด: 1 x 1.23456 = 1.2346 (Sales และ OOS)
    /// </summary>
    public static void Test_TC01_21_QuantitiesAreRoundedToFourDecimals()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-ROUND", "Rounding Item", "HB", 1.23456m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-ROUND", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "CUST-01", "DOM", null, 1.0m));
        repository.SourceMysqlOos.Add(new RawMysqlOosRow("SKU-ROUND", DateTime.Parse("2026-08-12"), "CUST-01", "WH01", "DOM", 1.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }, AsOfSeptember2026));

        var record = repository.GetExistingStagingRecords().Single(r => r.ItemId == "SKU-ROUND");
        Assert(record.RawSalesQty == 1.2346m, $"TC-01-21 Sales expected 1.2346m, got {record.RawSalesQty}");
        Assert(record.OosLostQty == 1.2346m, $"TC-01-21 OOS expected 1.2346m, got {record.OosLostQty}");

        Console.WriteLine("[PASS] TC-01-21: Quantities are rounded to 4 decimals");
    }

    /// <summary>
    /// TC-01-22: เพดาน OOS ใช้ max ของ (SKU, คลัง) นั้นๆ เท่านั้น ไม่ปนข้าม SKU หรือข้ามคลัง
    /// (regression ของการเปลี่ยนไปใช้ GetHistoricalMaxSales ที่ GROUP BY ตาม item_id + warehouse_code)
    /// </summary>
    public static void Test_TC01_22_HistoricalMaxIsScopedPerItemAndWarehouse()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-A", "Item A", "HB", 1.0m));
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-B", "Item B", "HB", 1.0m));

        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-A", "MDC (B2B)", "202511", 300.0m, 0.0m, DateTime.UtcNow));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-A", "RDC-CM (B2B)", "202511", 50.0m, 0.0m, DateTime.UtcNow));
        repository.StagingDemand24MTable.Add(new DrpStgDemand24MRecord("SKU-B", "MDC (B2B)", "202511", 90.0m, 0.0m, DateTime.UtcNow));

        foreach (var item in new[] { "SKU-A", "SKU-B" })
        {
            repository.SourceMysqlOos.Add(new RawMysqlOosRow(item, DateTime.Parse("2026-08-10"), "CUST-01", "WH01", "DOM", 1000.0m));
            repository.SourceMysqlOos.Add(new RawMysqlOosRow(item, DateTime.Parse("2026-08-10"), "CUST-02", "WHCM01", "DOMCM", 1000.0m));
        }

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", TargetWarehouseCode: null,
                ActiveWarehouses: new[] { "MDC (B2B)", "RDC-CM (B2B)" }, ExecutionTimestamp: AsOfSeptember2026));

        var target = repository.GetExistingStagingRecords().Where(r => r.YearMonth == "202608").ToList();
        decimal Oos(string item, string wh) => target.Single(r => r.ItemId == item && r.WarehouseCode == wh).OosLostQty;

        Assert(Oos("SKU-A", "MDC (B2B)") == 300.0000m, $"TC-01-22 SKU-A/MDC expected 300.0000m, got {Oos("SKU-A", "MDC (B2B)")}");
        Assert(Oos("SKU-A", "RDC-CM (B2B)") == 50.0000m, $"TC-01-22 SKU-A/RDC-CM expected 50.0000m, got {Oos("SKU-A", "RDC-CM (B2B)")}");
        Assert(Oos("SKU-B", "MDC (B2B)") == 90.0000m, $"TC-01-22 SKU-B/MDC expected 90.0000m, got {Oos("SKU-B", "MDC (B2B)")}");
        Assert(Oos("SKU-B", "RDC-CM (B2B)") == 0.0000m, $"TC-01-22 SKU-B/RDC-CM never sold -> expected 0.0000m, got {Oos("SKU-B", "RDC-CM (B2B)")}");

        Console.WriteLine("[PASS] TC-01-22: Historical max is scoped per (item, warehouse)");
    }

    /// <summary>
    /// TC-01-23: Default TargetPeriod (M-1) คิดจากเวลาไทย (UTC+7) ไม่ใช่เวลา UTC
    /// 2026-09-30 20:00 UTC = 2026-10-01 03:00 ไทย -> M-1 = 202609 | 2026-09-30 16:00 UTC = 2026-09-30 23:00 ไทย -> M-1 = 202608
    /// </summary>
    public static void Test_TC01_23_DefaultPeriodUsesThaiTime()
    {
        AssertDefaultPeriod(new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc), "202609");
        AssertDefaultPeriod(new DateTime(2026, 9, 30, 16, 0, 0, DateTimeKind.Utc), "202608");

        Console.WriteLine("[PASS] TC-01-23: Default M-1 uses Thai time (UTC+7)");
    }

    private static void AssertDefaultPeriod(DateTime executionUtc, string expectedPeriod)
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-TZ", "Thai Time Item", "HB", 1.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", null, ExecutionTimestamp: executionUtc));

        var staging = repository.GetExistingStagingRecords();
        Assert(staging.Count == 10 && staging.All(r => r.YearMonth == expectedPeriod),
            $"TC-01-23 execution {executionUtc:yyyy-MM-dd HH:mm} UTC expected default period {expectedPeriod}");
    }

    /// <summary>
    /// TC-01-24: ITEMNAME ยาวเกิน 200 ตัวอักษร (item_name = NVARCHAR(200)) ต้องถูกตัดให้พอดี ไม่ให้ Upsert ล้มทั้งงาน
    /// </summary>
    public static void Test_TC01_24_LongItemNameIsTruncatedTo200()
    {
        var repository = new MockDrpIngestionRepository();
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-LONG", new string('A', 250), "HB", 1.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", ExecutionTimestamp: AsOfSeptember2026));

        Assert(repository.MasterItemsTable["SKU-LONG"].ItemName.Length == 200,
            $"TC-01-24 item_name must be truncated to 200 chars, got {repository.MasterItemsTable["SKU-LONG"].ItemName.Length}");

        Console.WriteLine("[PASS] TC-01-24: Item name longer than 200 chars is truncated");
    }

    /// <summary>
    /// TC-01-25: SKU ที่มีใน DRP_MAS_ITEM แต่หายจาก INVENTTABLE: เก็บไว้ไม่ลบ และใช้ Factor 1.0 (ยืนยันกับทีมแล้ว)
    /// </summary>
    public static void Test_TC01_25_SkuMissingFromInventTableIsKeptWithFactorOne()
    {
        var repository = new MockDrpIngestionRepository();
        var oldStamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repository.MasterItemsTable["SKU-LEGACY"] = new DrpMasItemRecord("SKU-LEGACY", "Legacy Item", "YP", oldStamp);
        repository.SourceInventTable.Add(new RawInventTableRow("SKU-NEW", "New Item", "HB", 2.0m));
        repository.SourceSalesOrders.Add(new RawSalesOrderRow("SKU-LEGACY", DateTime.Parse("2026-08-10"), "Invoice", "WH01", "C001", "DOM", null, 7.0m));

        new DemandIngestionEngine(repository).ExecuteIngestion(
            new IngestionJobRequest("NonInit", "202608", "MDC (B2B)", new[] { "MDC (B2B)" }, AsOfSeptember2026));

        Assert(repository.MasterItemsTable.ContainsKey("SKU-LEGACY") &&
               repository.MasterItemsTable["SKU-LEGACY"].ItemName == "Legacy Item" &&
               repository.MasterItemsTable["SKU-LEGACY"].UpdatedAt == oldStamp,
            "TC-01-25 SKU missing from INVENTTABLE must be kept untouched in DRP_MAS_ITEM");

        var legacy = repository.GetExistingStagingRecords().Single(r => r.ItemId == "SKU-LEGACY" && r.WarehouseCode == "MDC (B2B)");
        Assert(legacy.RawSalesQty == 7.0000m, $"TC-01-25 Factor must fall back to 1.0 (expected 7.0000m), got {legacy.RawSalesQty}");

        Console.WriteLine("[PASS] TC-01-25: SKU missing from INVENTTABLE is kept, Factor falls back to 1.0");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception($"[ASSERTION FAILED] {message}");
        }
    }
}
