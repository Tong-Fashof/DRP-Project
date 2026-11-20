namespace DRP_Project.src.Ingestion;

public record RawInventTableRow(
    string? ItemId,
    string? ItemName,
    string? ProductLine,
    decimal? FactorSalesToBaseUnit,
    string? Company = "PMH"   // "PMH" หรือ "PFN" (INVENTTABLE เป็น Multi-Company: ITEMID เดียวกันมีได้หลายแถวคนละ Company
                              // พร้อม Factor คนละค่า ตามคำยืนยันของ SA: SALESORDER -> ใช้ Factor ฝั่ง PMH, SALESPFN -> ใช้ฝั่ง PFN)
                              // Default = "PMH": Test เดิม/ข้อมูลที่ไม่ได้ระบุ Company ถือเป็นฝั่ง PMH โดยปริยาย (ไม่ต้องแก้ Test ที่มีอยู่)
);

public record RawSalesOrderRow(
    string? ItemId,
    DateTime? DocDate,
    string? DocumentType,
    string? Warehouse,
    string? CustomerId,
    string? CustGroup,
    string? PriceGroup,
    decimal? Qty
);

public record RawPosSalesRow(
    string? ItemId,
    DateTime? DocDate,
    string? BranchId,
    string? VendorPa,
    decimal? Qty
);

public record RawMysqlOosRow(
    string? ProductCode,
    DateTime? CreDate,
    string? CustomerId,
    string? InventLocation,
    string? CustGroup,
    decimal? Qty
);

public record DrpMasItemRecord(
    string ItemId,
    string ItemName,
    string ProductLine,
    DateTime UpdatedAt
);

public record DrpStgDemand24MRecord(
    string ItemId,
    string WarehouseCode,
    string YearMonth,
    decimal RawSalesQty,
    decimal OosLostQty,
    DateTime ImportedAt
);

public record IngestionJobRequest(
    string Mode, // "Init" or "NonInit"
    string TargetPeriod, // "YYYYMM"
    string? TargetWarehouseCode = null,
    IReadOnlyList<string>? ActiveWarehouses = null,
    DateTime? ExecutionTimestamp = null
);