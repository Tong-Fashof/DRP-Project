namespace DRP_Project.src.Cleansing
{

    // PBI-08 (SCRUM-19): Cleansed Dataset Finalize Engine
    // รวมผลจาก Gate 1/2 (KEEP), PBI-06 (Sigma), PBI-07 (IQR) เป็นชุดเดียว
    // คำนวณสถิติสะอาดใหม่ (Clean Mean/SD/CV/ADI/Trend r/Seasonal r) ผ่าน CleansedSkuDemandDto ในหน่วยความจำ
    // และสร้างแถว Fact รายเดือน (24 แถว/SKU) สำหรับ DRP_FCT_SKU_OUTLIER_CLEANSING
    // ไม่ยุ่งกับฐานข้อมูล (DB I/O อยู่ใน OutlierCleansingRepository.cs)

    // ผลลัพธ์ต่อ 1 SKU (Outbound Contract ของ PBI-08 -> Stage 1.2 / PBI-09 ถึง PBI-12), 15 พร็อพเพอร์ตี้
    public class CleansedSkuDemandDto
    {
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";
        public decimal[] CleansedDemand24M { get; set; } = new decimal[24];

        public decimal CleanMean { get; set; }
        public decimal CleanSd { get; set; }
        public decimal CleanCv { get; set; }
        public int CleanActiveMonths { get; set; }
        public decimal CleanAdi { get; set; }
        public decimal CleanTrendR { get; set; }
        public decimal CleanSeasonalR { get; set; }

        public int HistoryMonths { get; set; }
        public string OutlierMethod { get; set; } = "";

        // Normality Metadata Handoff: null สำหรับสาย KEEP จาก Gate 1/2 (ข้าม PBI-05)
        public decimal? Skewness { get; set; }
        public decimal? Kurtosis { get; set; }
        public decimal? JbPValue { get; set; }
    }

    // 1 แถว = 1 เดือนของ 1 SKU จับคู่ตรงกับคอลัมน์ตาราง DRP_FCT_SKU_OUTLIER_CLEANSING
    public class SkuOutlierCleansingRow
    {
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";
        public int MonthNo { get; set; }
        public string PeriodYearMonth { get; set; } = "";
        public string OutlierMethod { get; set; } = "";
        public string OutlierStatus { get; set; } = "";
        public decimal OriginalDemandQty { get; set; }
        public decimal CleansedDemandQty { get; set; }
        public decimal QtyReduced { get; set; }
        public DateTime CalculatedAt { get; set; }
    }

    public static class CleansedDatasetFinalizeEngine
    {
        public const string StatusNormal = "NORMAL";
        public const string StatusHighOutlier = "HIGH_OUTLIER";

        // รวม 3 สาย (KEEP, Sigma, IQR) -> สร้าง DTO สถิติสะอาด + แถว Fact รายเดือน ทีละ SKU
        // periodMap: month_no (1..24) -> period_year_month (YYYYMM) จาก Sliding 24M เดียวกับ Demand24M
        public static (List<CleansedSkuDemandDto> Dtos, List<SkuOutlierCleansingRow> Rows) FinalizeAll(
            List<SkuCleansingCandidate> keepCandidates,
            List<CleansedDemandSeries> sigmaCleansed,
            List<CleansedDemandSeries> iqrCleansed,
            Dictionary<int, string> periodMap)
        {
            var all = new List<CleansedDemandSeries>();
            all.AddRange(keepCandidates.Where(c => c.CleansingStatus == "KEEP").Select(ToKeepSeries));
            all.AddRange(sigmaCleansed);
            all.AddRange(iqrCleansed);

            var dtos = new List<CleansedSkuDemandDto>(all.Count);
            var rows = new List<SkuOutlierCleansingRow>(all.Count * 24);
            var calculatedAt = DateTime.Now;

            foreach (var s in all)
            {
                dtos.Add(BuildDto(s));
                rows.AddRange(BuildRows(s, periodMap, calculatedAt));
            }

            return (dtos, rows);
        }

        // DEC-PBI08-01 (Unified Inbound DTO): สาย KEEP จาก Gate 1/2 -> CleansedDemandSeries
        // ไม่ตัดยอดใดๆ (CleansedDemand24M = OriginalDemand24M, QtyReduced24M = 0 ทั้งหมด, CapLimit = 0)
        // Skewness/Kurtosis/JbPValue = null เพราะข้ามการทดสอบ Normality (PBI-05)
        public static CleansedDemandSeries ToKeepSeries(SkuCleansingCandidate c)
        {
            var original = (decimal[])c.Demand24M.Clone();

            return new CleansedDemandSeries
            {
                WarehouseCode = c.warehouse_code,
                ItemId = c.item_id,
                OriginalDemand24M = original,
                CleansedDemand24M = (decimal[])original.Clone(),
                QtyReduced24M = new decimal[24],
                CleansingMethod = "KEEP",
                CapLimit = 0.0000m,
                HistoryMonths = c.HistoryMonths,
                AvgDemand = CleansingEngine.CalculateAvgDemand(c.Demand24M),
                Sd = c.Sd,
                Skewness = null,
                Kurtosis = null,
                JbPValue = null
            };
        }

        // FM-16 ขั้นที่ 2.4: คำนวณสถิติสะอาด 7 ค่า จาก CleansedDemand24M
        // ใช้สูตรเดียวกับ PBI-03/04 (FM-04, FM-05, FM-06, FM-07, FM-08, FM-09, FM-13) ทุก SKU 100% (Clean Statistics Equality)
        // ปัดเศษ 4 ตำแหน่ง (AwayFromZero) เฉพาะตอนเก็บผลลัพธ์ลง DTO
        public static CleansedSkuDemandDto BuildDto(CleansedDemandSeries s)
        {
            decimal[] demand = s.CleansedDemand24M;

            decimal mean = CleansingEngine.CalculateAvgDemand(demand);                          // FM-06
            decimal sd = CleansingEngine.CalculateSd(demand);                                   // FM-07 (Guard mean=0 ให้ sd=0: mean ไม่ติดลบ ผลเท่ากัน)
            decimal cv = mean == 0m ? 0m : sd / mean;                                            // FM-13
            int activeMonths = CleansingEngine.CalculateActiveMonths(demand);                    // FM-04
            decimal adi = CleansingEngine.CalculateAdi(activeMonths);                             // FM-05
            decimal trendR = CleansingEngine.CalculateTrendR(demand, s.HistoryMonths, sd);        // FM-08 (ปัด 4 ตำแหน่งในฟังก์ชันแล้ว)
            decimal seasonalR = CleansingEngine.CalculateLag12R(demand, s.HistoryMonths);         // FM-09 (ปัด 4 ตำแหน่งในฟังก์ชันแล้ว)

            return new CleansedSkuDemandDto
            {
                WarehouseCode = s.WarehouseCode,
                ItemId = s.ItemId,
                CleansedDemand24M = demand,
                CleanMean = Round4(mean),
                CleanSd = Round4(sd),
                CleanCv = Round4(cv),
                CleanActiveMonths = activeMonths,
                CleanAdi = Round4(adi),
                CleanTrendR = trendR,
                CleanSeasonalR = seasonalR,
                HistoryMonths = s.HistoryMonths,
                OutlierMethod = s.CleansingMethod,
                Skewness = s.Skewness,
                Kurtosis = s.Kurtosis,
                JbPValue = s.JbPValue
            };
        }

        // 24 แถว/SKU ตรงกับคอลัมน์ DRP_FCT_SKU_OUTLIER_CLEANSING
        // outlier_status: qty_reduced > 0 -> HIGH_OUTLIER มิฉะนั้น NORMAL (รวมสาย KEEP ที่ไม่ตัดยอดเลย = NORMAL ทุกเดือน)
        public static List<SkuOutlierCleansingRow> BuildRows(CleansedDemandSeries s, Dictionary<int, string> periodMap, DateTime calculatedAt)
        {
            var rows = new List<SkuOutlierCleansingRow>(24);

            for (int t = 0; t < 24; t++)
            {
                int monthNo = t + 1;

                if (!periodMap.TryGetValue(monthNo, out string? periodYearMonth))
                    throw new InvalidOperationException($"ไม่พบ period_year_month สำหรับ month_no={monthNo} ({s.WarehouseCode}|{s.ItemId})");

                decimal original = Round4(s.OriginalDemand24M[t]);
                decimal cleansedQty = Round4(s.CleansedDemand24M[t]);
                decimal reduced = Round4(Math.Max(0.0000m, s.QtyReduced24M[t]));

                rows.Add(new SkuOutlierCleansingRow
                {
                    WarehouseCode = s.WarehouseCode,
                    ItemId = s.ItemId,
                    MonthNo = monthNo,
                    PeriodYearMonth = periodYearMonth,
                    OutlierMethod = s.CleansingMethod,
                    OutlierStatus = reduced > 0m ? StatusHighOutlier : StatusNormal,
                    OriginalDemandQty = original,
                    CleansedDemandQty = cleansedQty,
                    QtyReduced = reduced,
                    CalculatedAt = calculatedAt
                });
            }

            return rows;
        }

        private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

}