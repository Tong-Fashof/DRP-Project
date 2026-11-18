namespace DRP_Project.src.Ingestion;

public record RawInventTableRow(
    string? ItemId,
    string? ItemName,
    string? ProductLine,
    decimal? FactorSalesToBaseUnit
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
