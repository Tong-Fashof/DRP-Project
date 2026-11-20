using System.Globalization;
using Serilog;

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

    private const int ThaiUtcOffsetHours = 7;      // เวลาไทย = UTC+7 (ไม่มี DST)
    private const int MaxItemNameLength = 200;         // DRP_MAS_ITEM.item_name = NVARCHAR(200)

    public DemandIngestionEngine(IDrpIngestionRepository repository)
    {
        _repository = repository;
    }

    public void ExecuteIngestion(IngestionJobRequest request)
    {
        var executionTimestamp = request.ExecutionTimestamp ?? DateTime.UtcNow;

        // 0. Validate Input ก่อนอ่าน/เขียนข้อมูลใดๆ: Mode ต้องเป็น Init หรือ NonInit เท่านั้น (พิมพ์ผิดต้องไม่ตกไปเป็น NonInit เงียบๆ)
        var executionMode = (request.Mode ?? "").Trim();
        if (!executionMode.Equals("Init", StringComparison.OrdinalIgnoreCase) &&
            !executionMode.Equals("NonInit", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown mode '{request.Mode}'. Expected 'Init' or 'NonInit'.", nameof(request));

        // งวดล่าสุดที่ปิดแล้ว (M-1) = เดือนก่อนหน้าของ "เวลาไทย" ณ เวลารัน | ไม่ระบุ TargetPeriod -> ใช้ M-1 (Default ตามสเปก)
        string latestClosedPeriod = ToThaiTime(executionTimestamp).AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture);
        string targetPeriodYearMonth = string.IsNullOrWhiteSpace(request.TargetPeriod) ? latestClosedPeriod : request.TargetPeriod.Trim();

        if (targetPeriodYearMonth.Length != 6 ||
            !DateTime.TryParseExact(targetPeriodYearMonth, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException($"TargetPeriod must be a valid yyyyMM (got '{request.TargetPeriod}').", nameof(request));

        // Strict Closed Calendar Month: ห้ามงวดปัจจุบัน (ยังไม่ปิดงวด) และงวดอนาคต
        if (string.CompareOrdinal(targetPeriodYearMonth, latestClosedPeriod) > 0)
            throw new ArgumentException($"TargetPeriod {targetPeriodYearMonth} is not a closed month (latest closed month is {latestClosedPeriod}).", nameof(request));

        // 1. Calculate Period Range (Init: 25 months ending at targetPeriod = M-25..M-1; NonInit: targetPeriod only = M-1)
        List<string> periodMonths = new();
        if (executionMode.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            DateTime baseDate = DateTime.ParseExact(targetPeriodYearMonth, "yyyyMM", CultureInfo.InvariantCulture);
            for (int monthOffset = 24; monthOffset >= 0; monthOffset--)
            {
                periodMonths.Add(baseDate.AddMonths(-monthOffset).ToString("yyyyMM", CultureInfo.InvariantCulture));
            }
        }
        else
        {
            periodMonths.Add(targetPeriodYearMonth);
        }

        string startPeriodYearMonth = periodMonths.First();
        string endPeriodYearMonth = periodMonths.Last();

        // ชุดคลังที่ประมวลผล: คำนวณและตรวจครั้งเดียว แล้วใช้ชุดเดียวกันทั้งตอนสร้าง Grid และตอน DELETE (Delete scope == Insert scope)
        IReadOnlyList<string> targetWarehouses = ResolveTargetWarehouses(request);

        // 2. Step 0: Sync Master Catalog from INVENTTABLE
        // INVENTTABLE เป็น Multi-Company (Ax): ITEMID เดียวกันมีได้หลายแถวคนละ Company (พบจริง: PMH / PFN)
        // พร้อม FACTOR_SALESTOBASEUNIT คนละค่า -> ต้องแยก Factor ตาม Company ตามคำยืนยันของ SA:
        //   ยอดขายจาก SALESORDER (PMH/MTW) -> ใช้ Factor ฝั่ง Company = 'PMH'
        //   ยอดขายจาก SALESPFN (PFN)       -> ใช้ Factor ฝั่ง Company = 'PFN' (ปัจจุบันสเปกยังไม่มีสูตรที่คูณ Factor ฝั่งนี้)
        //   OOS จาก MySQL (3 คลัง PMH B2B) -> ใช้ Factor ฝั่ง Company = 'PMH' (เป็นยอดฝั่ง PMH)
        var rawInventTableRows = _repository.FetchSourceInventTable();
        if (rawInventTableRows.Count == 0)
            Log.Warning("PBI-01: INVENTTABLE returned 0 rows (Master Catalog จะไม่ถูกอัปเดต ใช้เฉพาะ SKU ที่มีอยู่ใน DRP_MAS_ITEM)");

        var masterSourceRowByItemId = new Dictionary<string, RawInventTableRow>(StringComparer.OrdinalIgnoreCase);
        var factorByItemIdPmh = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var factorByItemIdPfn = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var invalidFactorSamples = new List<string>();

        foreach (var row in rawInventTableRows)
        {
            if (string.IsNullOrWhiteSpace(row.ItemId)) continue;
            var cleanItemId = row.ItemId.Trim();
            var cleanCompany = (row.Company ?? "").Trim().ToUpperInvariant();

            // Factor NULL / 0 / ติดลบ -> Fallback 1.0 (สเปก: บันทึก Warning Log)
            bool isInvalidFactor = !row.FactorSalesToBaseUnit.HasValue || row.FactorSalesToBaseUnit.Value <= 0m;
            var factor = isInvalidFactor ? 1.0m : row.FactorSalesToBaseUnit!.Value;
            if (isInvalidFactor && invalidFactorSamples.Count < 10)
                invalidFactorSamples.Add($"{cleanItemId}({cleanCompany})");

            if (cleanCompany == "PMH") factorByItemIdPmh[cleanItemId] = factor;
            else if (cleanCompany == "PFN") factorByItemIdPfn[cleanItemId] = factor;

            // Master metadata (item_name, product_line): ยึด Company = 'PMH' เป็นหลัก
            // ถ้า SKU มีอยู่เฉพาะฝั่ง PFN เท่านั้น (ไม่มีแถว PMH) ให้ใช้แถว PFN แทน กัน SKU หายไปจาก Master
            if (!masterSourceRowByItemId.ContainsKey(cleanItemId) || cleanCompany == "PMH")
                masterSourceRowByItemId[cleanItemId] = row;
        }

        var invalidFactorCount = rawInventTableRows.Count(row =>
            !string.IsNullOrWhiteSpace(row.ItemId) &&
            (!row.FactorSalesToBaseUnit.HasValue || row.FactorSalesToBaseUnit.Value <= 0m));
        if (invalidFactorCount > 0)
            Log.Warning("PBI-01: {Count} แถวมี FACTOR_SALESTOBASEUNIT เป็น NULL/0/ติดลบ -> Fallback 1.0 (ตัวอย่าง: {Sample})",
                invalidFactorCount, string.Join(", ", invalidFactorSamples));

        var existingMasterItems = _repository.GetExistingMasterItems();
        var activeMasterCatalog = new Dictionary<string, DrpMasItemRecord>(existingMasterItems, StringComparer.OrdinalIgnoreCase);
        var masterUpsertList = new List<DrpMasItemRecord>();

        int truncatedNameCount = 0;
        foreach (var (cleanItemId, inventRow) in masterSourceRowByItemId)
        {
            var cleanItemName = inventRow.ItemName ?? "";
            if (cleanItemName.Length > MaxItemNameLength)
            {
                // item_name เป็น NVARCHAR(200): ชื่อยาวเกินจะทำให้ Upsert ล้มทั้งงาน จึงตัดให้พอดีและบันทึก Warning
                cleanItemName = cleanItemName.Substring(0, MaxItemNameLength);
                truncatedNameCount++;
            }

            var cleanProductLine = (inventRow.ProductLine ?? "").Trim().ToUpperInvariant();
            var normalizedProductLine = cleanProductLine == "YP" ? "YP" : "HB";

            var itemRecord = new DrpMasItemRecord(cleanItemId, cleanItemName, normalizedProductLine, executionTimestamp);
            masterUpsertList.Add(itemRecord);
            activeMasterCatalog[cleanItemId] = itemRecord;
        }

        if (truncatedNameCount > 0)
            Log.Warning("PBI-01: {Count} SKU มี ITEMNAME ยาวเกิน {Max} ตัวอักษร -> ตัดให้พอดี", truncatedNameCount, MaxItemNameLength);

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

            var orderYearMonth = salesOrder.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture);
            if (string.Compare(orderYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(orderYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapPmhMtwWarehouse(salesOrder.Warehouse, salesOrder.CustGroup, salesOrder.PriceGroup);
            if (mappedWarehouse == null)
                continue; // Unmapped warehouse pair

            // SALESORDER = ฝั่ง PMH/MTW -> ใช้ Factor Company = 'PMH'
            var conversionFactor = factorByItemIdPmh.GetValueOrDefault(cleanItemId, 1.0m);
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

            var orderYearMonth = posSale.DocDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture);
            if (string.Compare(orderYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(orderYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapPosWarehouse(posSale.BranchId, posSale.VendorPa);
            if (mappedWarehouse == null)
                continue; // Unmapped branch

            // SALESPFN.QTY เป็น POS Unit อยู่แล้ว: ไม่คูณ FACTOR_SALESTOBASEUNIT (FM-00 ขั้นที่ 2.2) | QTY NULL -> 0.0000
            // (factorByItemIdPfn ถูกคำนวณเตรียมไว้ แต่ยังไม่มีสูตรในสเปกปัจจุบันที่ใช้คูณค่านี้)
            var normalizedQuantity = posSale.Qty ?? 0.0000m;

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

            var wishlistYearMonth = wishlistOos.CreDate.Value.ToString("yyyyMM", CultureInfo.InvariantCulture);
            if (string.Compare(wishlistYearMonth, startPeriodYearMonth, StringComparison.Ordinal) < 0 ||
                string.Compare(wishlistYearMonth, endPeriodYearMonth, StringComparison.Ordinal) > 0)
                continue;

            string? mappedWarehouse = MapMysqlOosWarehouse(wishlistOos.InventLocation, wishlistOos.CustGroup);
            if (mappedWarehouse == null)
                continue;

            var rawQuantity = wishlistOos.Qty ?? 0.0000m;
            if (rawQuantity < 0) rawQuantity = 0.0000m;

            // OOS (3 คลัง PMH B2B) -> ใช้ Factor Company = 'PMH'
            var conversionFactor = factorByItemIdPmh.GetValueOrDefault(cleanItemId, 1.0m);
            var normalizedQuantity = rawQuantity * conversionFactor;

            var aggregationKey = (cleanItemId, mappedWarehouse, wishlistYearMonth);
            oosAggregation[aggregationKey] = oosAggregation.GetValueOrDefault(aggregationKey, 0.0000m) + normalizedQuantity;
        }

        // 6. Build Staging Grid & Stage 0 OOS Capping
        // Grid สร้างแบบ Lazy (ทีละแถวตอน BulkInsert) ไม่ถือทั้งก้อนใน Memory: Init 25 งวด = ~10.5 ล้านแถว
        // การปัดเศษ 4 ตำแหน่งหลังรวมยอด (Round4) ทำตอนอ่านค่า เพื่อให้เพดาน OOS กับยอดขายใช้ค่าเดียวกัน
        long stagingRowCount = (long)activeMasterCatalog.Count * targetWarehouses.Count * periodMonths.Count;
        IEnumerable<DrpStgDemand24MRecord> stagingRecordsToInsert;

        if (executionMode.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            // Init mode: 25 months dense grid for all activeMasterCatalog x targetWarehouses x periodMonths
            stagingRecordsToInsert = BuildInitGrid(
                activeMasterCatalog.Keys, targetWarehouses, periodMonths, salesAggregation, oosAggregation, executionTimestamp);
        }
        else
        {
            // NonInit mode: Process targetPeriodYearMonth (= M-1, งวดสุดท้ายของ window)
            // Historical window for maxHistoricalSales = 23 งวดก่อนหน้า (target-23 .. target-1) จาก Staging + ยอดขายงวด target ที่กำลังนำเข้า
            // เช่น target 202608 -> 202409..202607 (รวม target-1, ไม่รวม target-24 = 202408)
            DateTime baseDate = DateTime.ParseExact(targetPeriodYearMonth, "yyyyMM", CultureInfo.InvariantCulture);
            string historyStartPeriod = baseDate.AddMonths(-23).ToString("yyyyMM", CultureInfo.InvariantCulture);
            string historyEndPeriod = baseDate.AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture);

            // MAX(raw_sales_qty) ต่อ (SKU, คลัง) ของ 23 งวดก่อนหน้า: ให้ฐานข้อมูล GROUP BY แล้วส่งมาเฉพาะค่า max
            // (ต้องอ่านก่อนเริ่ม Transaction และก่อนสร้าง Grid)
            var historicalMaxSales = _repository.GetHistoricalMaxSales(historyStartPeriod, historyEndPeriod);
            if (historicalMaxSales.Count == 0)
                Log.Warning("PBI-01: NonInit {Period} ไม่พบประวัติ 23 งวดก่อนหน้าใน Staging (ยังไม่เคยรัน Init?) เพดาน OOS จะคิดจากยอดขายงวดนี้เท่านั้น", targetPeriodYearMonth);

            stagingRecordsToInsert = BuildNonInitGrid(
                activeMasterCatalog.Keys, targetWarehouses, targetPeriodYearMonth, historicalMaxSales, salesAggregation, oosAggregation, executionTimestamp);
        }

        // Guard: ไม่มีแถวให้เขียนเลย (เช่น Master Catalog ว่าง) -> หยุดก่อนเริ่ม Transaction ไม่ลบข้อมูลเดิมโดยไม่มีข้อมูลใหม่มาแทน
        if (stagingRowCount == 0)
            throw new InvalidOperationException("ไม่มีแถว Staging ให้บันทึก (Master Catalog ว่าง) ยกเลิกเพื่อป้องกันการลบข้อมูลเดิม");

        // 7. Atomic Transactional Write
        _repository.BeginTransaction();
        try
        {
            if (masterUpsertList.Count > 0)
            {
                _repository.UpsertMasterItems(masterUpsertList);
            }

            // ขอบเขต DELETE = ช่วงงวดเดียวกับที่ insert (Init 25 งวด / NonInit งวดเดียว) และชุดคลังเดียวกับ Grid
            // ครบ 10 คลัง -> null (ไม่ต้องกรองคลัง) | ชุดย่อย -> ลบเฉพาะคลังเหล่านั้น ไม่กระทบคลังอื่น
            bool isFullWarehouseScope = targetWarehouses.Count == AllTargetWarehouses.Count;
            _repository.DeleteStagingScope(startPeriodYearMonth, endPeriodYearMonth, isFullWarehouseScope ? null : targetWarehouses);
            _repository.BulkInsertStaging(stagingRecordsToInsert);

            _repository.CommitTransaction();

            Log.Information("PBI-01 Ingestion สำเร็จ Mode={Mode} Period={Start}-{End} Warehouses={Warehouses} MasterUpsert={MasterRows} StagingRows={StagingRows}",
                executionMode, startPeriodYearMonth, endPeriodYearMonth, targetWarehouses.Count, masterUpsertList.Count, stagingRowCount);
        }
        catch
        {
            _repository.RollbackTransaction();
            throw;
        }
    }

    // เวลาไทย (UTC+7): Local -> แปลงเป็น UTC ก่อน (ผลจึงไม่ขึ้นกับ time zone ของเครื่อง) | Utc/Unspecified ถือเป็น UTC
    private static DateTime ToThaiTime(DateTime timestamp)
    {
        DateTime utc = timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
        return utc.AddHours(ThaiUtcOffsetHours);
    }

    // ปัด 4 ตำแหน่ง (AwayFromZero เหมือนฝั่ง Assembler) ให้ตรง NUMERIC(38,4)
    private static decimal Round4(decimal value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    // Init: Dense Grid ทุก SKU x คลัง x 25 งวด | เพดาน OOS = MAX ยอดขายของ SKU/คลังนั้นทั้ง 25 งวด (จากข้อมูลใน Memory)
    // yield return: สร้างทีละแถวตามที่ BulkInsert ดึง ไม่เก็บทั้ง Grid
    private static IEnumerable<DrpStgDemand24MRecord> BuildInitGrid(
        IEnumerable<string> itemIds,
        IReadOnlyList<string> warehouses,
        IReadOnlyList<string> periods,
        Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal> salesAggregation,
        Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal> oosAggregation,
        DateTime importedAt)
    {
        foreach (var itemId in itemIds)
        {
            foreach (var warehouseCode in warehouses)
            {
                decimal maxHistoricalSales = 0.0000m;
                foreach (var period in periods)
                {
                    var salesAmount = Round4(salesAggregation.GetValueOrDefault((itemId, warehouseCode, period), 0.0000m));
                    if (salesAmount > maxHistoricalSales) maxHistoricalSales = salesAmount;
                }

                foreach (var period in periods)
                {
                    var rawSalesQuantity = Round4(salesAggregation.GetValueOrDefault((itemId, warehouseCode, period), 0.0000m));
                    var rawOosQuantity = Round4(oosAggregation.GetValueOrDefault((itemId, warehouseCode, period), 0.0000m));

                    // Stage 0 OOS Capping formula: MIN(MAX(0, raw_oos), max_hist_sales)
                    var cappedOosQuantity = Math.Min(Math.Max(0.0000m, rawOosQuantity), maxHistoricalSales);

                    yield return new DrpStgDemand24MRecord(itemId, warehouseCode, period, rawSalesQuantity, cappedOosQuantity, importedAt);
                }
            }
        }
    }

    // NonInit: Grid ของงวดเดียว | เพดาน OOS = MAX(ประวัติ 23 งวดใน Staging, ยอดขายงวดที่กำลังนำเข้า)
    private static IEnumerable<DrpStgDemand24MRecord> BuildNonInitGrid(
        IEnumerable<string> itemIds,
        IReadOnlyList<string> warehouses,
        string targetPeriod,
        IReadOnlyDictionary<(string ItemId, string WarehouseCode), decimal> historicalMaxSales,
        Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal> salesAggregation,
        Dictionary<(string ItemId, string WarehouseCode, string YearMonth), decimal> oosAggregation,
        DateTime importedAt)
    {
        foreach (var itemId in itemIds)
        {
            foreach (var warehouseCode in warehouses)
            {
                decimal maxStagingSales = historicalMaxSales.GetValueOrDefault((itemId, warehouseCode), 0.0000m);

                var currentSales = Round4(salesAggregation.GetValueOrDefault((itemId, warehouseCode, targetPeriod), 0.0000m));
                var maxHistoricalSales = Math.Max(0.0000m, Math.Max(maxStagingSales, currentSales));

                var rawOosQuantity = Round4(oosAggregation.GetValueOrDefault((itemId, warehouseCode, targetPeriod), 0.0000m));
                var cappedOosQuantity = Math.Min(Math.Max(0.0000m, rawOosQuantity), maxHistoricalSales);

                yield return new DrpStgDemand24MRecord(itemId, warehouseCode, targetPeriod, currentSales, cappedOosQuantity, importedAt);
            }
        }
    }

    /// <summary>
    /// ตัดสินชุดคลังที่ประมวลผล: ActiveWarehouses (ถ้ามี) > TargetWarehouseCode > ครบ 10 คลัง
    /// ตรวจชื่อคลังต้องอยู่ใน 10 คลังมาตรฐาน มิฉะนั้น throw ก่อนเริ่มอ่าน/เขียนข้อมูลใดๆ (กัน Row ขยะและ DELETE ว่างเปล่า)
    /// </summary>
    private static IReadOnlyList<string> ResolveTargetWarehouses(IngestionJobRequest request)
    {
        IReadOnlyList<string> requested =
            request.ActiveWarehouses != null && request.ActiveWarehouses.Count > 0
                ? request.ActiveWarehouses
                : (!string.IsNullOrWhiteSpace(request.TargetWarehouseCode)
                    ? new[] { request.TargetWarehouseCode.Trim() }
                    : AllTargetWarehouses);

        var unknownWarehouses = requested
            .Where(code => !AllTargetWarehouses.Contains(code, StringComparer.Ordinal))
            .ToList();
        if (unknownWarehouses.Count > 0)
            throw new ArgumentException($"Unknown warehouse code(s): {string.Join(", ", unknownWarehouses)}", nameof(request));

        return requested.Distinct(StringComparer.Ordinal).ToList();
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