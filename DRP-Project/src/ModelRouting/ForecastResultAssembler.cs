using DRP_Project.src.Cleansing;

namespace DRP_Project.src.ModelRouting
{

    // ======================================================================
    // PBI-11B: In-Memory Pipeline Aggregator & Deliverable Assembler
    // ประกอบ SkuForecastResult 72 คอลัมน์ต่อ SKU จากผล PBI-02..11 ในหน่วยความจำ แล้วส่งต่อ PBI-12 (ไม่ยุ่ง DB)
    // สถิติ Raw/Clean คำนวณบน Active Window (เดือนแรกที่ยอด > 0 ถึงเดือนที่ 24) ตามสูตร Excel Template V1
    // ใช้ซ้ำฟังก์ชันเดิม: CleansingEngine.CalculateTrendR/CalculateLag12R, IqrCappingEngine.PercentileInc
    // คำนวณด้วย double | avg และ sd ของ Window ปัด 4 ตำแหน่งก่อนใช้ต่อ (ตาม Excel) | ผลอื่นปัด 4 ตำแหน่ง (AwayFromZero) ตอนเก็บลง Record
    // ======================================================================

    // PBI-11B: Streamlined Golden Context 72 คอลัมน์ต่อ 1 SKU (เรียงตาม Source-to-Target Mapping ลำดับ 1..72)
    // + 2 ฟิลด์เสริมที่ส่งต่อให้ PBI-12: OutlierStatus24M (13 สถานะ x 24 งวด) และ IsShortCircuited
    // ไม่มีสูตร ไม่ยุ่ง DB | NULL = คำนวณทางคณิตศาสตร์ไม่ได้ | 0.0000 = ติด Guard ตามสูตร Excel
    public class SkuForecastResult
    {
        // กลุ่ม 1: Identity
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string AsOfPeriod { get; set; } = "";

        // กลุ่ม 2: Totals
        public decimal RawTotal24M { get; set; }
        public decimal OosTotal24M { get; set; }

        // กลุ่ม 3: Basic & Distribution Stats (Active Window)
        public decimal RawAvg { get; set; }
        public decimal RawSd { get; set; }
        public decimal RawCv { get; set; }
        public int? SigmaUsed { get; set; }
        public int RawTtlMth { get; set; }
        public int RawActiveMth { get; set; }
        public decimal? RawAdi { get; set; }
        public decimal? RawSkewness { get; set; }
        public decimal? RawKurtosis { get; set; }
        public decimal? JbStatistic { get; set; }
        public decimal? JbPValue { get; set; }
        public string DemandDistribution { get; set; } = "";
        public string OutlierMethod { get; set; } = "";
        public string SelectedOutlierTool { get; set; } = "";

        // กลุ่ม 4: Caps
        public decimal? Lower2Sigma { get; set; }
        public decimal? Upper2Sigma { get; set; }
        public decimal? Lower3Sigma { get; set; }
        public decimal? Upper3Sigma { get; set; }
        public decimal? Q1 { get; set; }
        public decimal? Q3 { get; set; }
        public decimal? Iqr { get; set; }
        public decimal? Lower15Iqr { get; set; }
        public decimal? Upper15Iqr { get; set; }
        public decimal? Lower3Iqr { get; set; }
        public decimal? Upper3Iqr { get; set; }

        // กลุ่ม 5: Outlier Summary & Action
        public int WarningCount { get; set; }
        public int ExtremeCount { get; set; }
        public int HighOutlierCount { get; set; }
        public int LowOutlierCount { get; set; }
        public string OutlierReviewStatus { get; set; } = "";
        public string Action { get; set; } = "";

        // กลุ่ม 6: Checks & Ceiling
        public int SalesRow { get; set; }
        public int OosRow { get; set; }
        public string SourceCheck { get; set; } = "";
        public decimal? SelectedUpperCap { get; set; }

        // กลุ่ม 7: Trend & Seasonality (ยอดดิบ)
        public decimal? RawTrendR { get; set; }
        public decimal? RawLag12R { get; set; }
        public decimal? RawYear1TrendR { get; set; }
        public decimal? RawYear2TrendR { get; set; }
        public decimal? RawDetrendedLag12R { get; set; }
        public int NegativeMonthsCount { get; set; }
        public string OutlierSelectionReason { get; set; } = "";
        public int CappedHighMonthsCount { get; set; }

        // กลุ่ม 8: Clean Stats & Behavior (Active Window สะอาด)
        public int CleanTtlMth { get; set; }
        public int CleanActiveMth { get; set; }
        public decimal? CleanAdi { get; set; }
        public decimal CleanMean { get; set; }
        public decimal CleanSd { get; set; }
        public decimal CleanCv { get; set; }
        public decimal CleanCv2 { get; set; }
        public decimal? DetectTrend { get; set; }
        public decimal? CleanDetrendedSeasonalR { get; set; }
        public string? DemandGroup { get; set; }
        public string DemandPattern { get; set; } = "";

        // กลุ่ม 9: Model Routing & XYZ
        public string? AssignedModelId { get; set; }
        public string XyzClass { get; set; } = "";
        public decimal? CvEligibleXyz { get; set; }
        public decimal? CvPercentile { get; set; }
        public int RawRowRef { get; set; }
        public string AppliedOutlierTool { get; set; } = "";
        public decimal QtyReducedByCap { get; set; }

        // กลุ่ม 10: Reason & Post-Clean Correlation
        public string? SelectionReason { get; set; }
        public decimal? CleanLag12R { get; set; }
        public decimal? CleanYear1TrendR { get; set; }
        public decimal? CleanYear2TrendR { get; set; }

        // กลุ่ม 11: Audit
        public DateTime CalculatedAt { get; set; }
        public DateTime CreatedAt { get; set; }

        // เสริม: ส่งต่อให้ PBI-12 (ซิงค์ outlier_status ลงตาราง / ตัดสินว่าต้องประเมิน Step 7 SES หรือไม่)
        public string[] OutlierStatus24M { get; set; } = new string[24];
        public bool IsShortCircuited { get; set; }
    }


    // เกณฑ์จาก M_LOV 11 ตัว (Default ตามสเปก) | ผู้เรียก (ModelRoutingPipeline.RunAssemble) เป็นผู้โหลดมาใส่
    public class AssemblerConfig
    {
        public decimal ProtectTrendSeason { get; set; } = 1.000000m;
        public decimal AdiThreshold { get; set; } = 1.320000m;
        public decimal CvThreshold { get; set; } = 0.500000m;
        public decimal JbPValueThreshold { get; set; } = 0.050000m;
        public decimal SkewNormalLimit { get; set; } = 0.500000m;
        public decimal KurtNormalLimit { get; set; } = 1.000000m;
        public decimal SigmaCapMultiplier { get; set; } = 3.000000m;
        public decimal SkewModerateLimit { get; set; } = 1.000000m;
        public decimal KurtModerateLimit { get; set; } = 3.000000m;
        public decimal SeasonalCorrelation { get; set; } = 0.500000m;
        public decimal TrendCorrelation { get; set; } = 0.500000m;
    }

    public static class ForecastResultAssembler
    {
        // ค่าคงที่ตามสเปก (ไม่อยู่ใน M_LOV)
        private const double CvSquaredThreshold = 0.49;   // Smooth/Erratic, Intermittent/Lumpy
        private const double XThreshold = 0.20;           // cv_percentile <= 0.20 -> X
        private const double YThreshold = 0.50;           // cv_percentile <= 0.50 -> Y

        // Window ของ 1 อนุกรม: Active = จำนวนเดือน > 0, Ttl = ความยาว Active Window, W = ค่าใน Window
        private sealed class Win
        {
            public int Active;
            public int Ttl;
            public double[] W = Array.Empty<double>();
            public double Avg;
            public double Sd;
            public double Cv;
        }

        // เพดานทั้งหมดของ 1 SKU (null = Guard ไม่ผ่าน)
        private sealed class Caps
        {
            public double? L2, U2, L3, U3, Q1, Q3, Iqr, L15, U15, L3I, U3I;
        }

        // ======================================================
        // Entry point: ประกอบทุก SKU | จัดกลุ่มตามคลัง เรียง item_id แบบ Ordinal (sales_row 1-based)
        // asOfPeriod = งวดอ้างอิง YYYYMM | candidates ใช้ดึง net demand ต้นฉบับ + raw/oos + MonthsPresent
        // ======================================================
        public static List<SkuForecastResult> AssembleAll(List<SkuModelRoutingState> states, List<SkuCleansingCandidate> candidates, string asOfPeriod, AssemblerConfig config)
        {
            if (states == null) throw new ArgumentNullException(nameof(states), "Routing state collection cannot be null.");
            if (candidates == null) throw new ArgumentNullException(nameof(candidates), "Candidate collection cannot be null.");
            if (config == null) throw new ArgumentNullException(nameof(config), "Assembler config cannot be null.");

            var source = new Dictionary<string, SkuCleansingCandidate>(candidates.Count);
            foreach (var c in candidates) source[c.warehouse_code + "|" + c.item_id] = c;

            DateTime now = DateTime.UtcNow;   // calculated_at / created_at ทุกแถวใช้ค่าเดียวกัน
            var results = new List<SkuForecastResult>(states.Count);

            foreach (var g in states.GroupBy(s => s.WarehouseCode).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ordered = g.OrderBy(s => s.ItemId, StringComparer.Ordinal).ToList();
                var batch = new List<SkuForecastResult>(ordered.Count);
                var cvEligible = new List<double?>(ordered.Count);
                int row = 0;

                foreach (var st in ordered)
                {
                    row++;
                    if (st.CleansedDemand24M == null || st.CleansedDemand24M.Length != 24)
                        throw new ArgumentException($"CleansedDemand24M ต้องมี 24 งวด ({st.WarehouseCode}|{st.ItemId})");
                    if (!source.TryGetValue(st.WarehouseCode + "|" + st.ItemId, out var cand))
                        throw new InvalidOperationException($"ไม่พบ candidate ต้นทางของ SKU ({st.WarehouseCode}|{st.ItemId})");
                    if (cand.Demand24M.Length != 24 || cand.RawSales24M.Length != 24 || cand.OosUplift24M.Length != 24)
                        throw new ArgumentException($"อาร์เรย์ยอดขายต้องมี 24 งวด ({st.WarehouseCode}|{st.ItemId})");

                    var (result, cv) = AssembleOne(st, cand, asOfPeriod, config, row, now);
                    batch.Add(result);
                    cvEligible.Add(cv);
                }

                ApplyPercentileAndXyz(batch, cvEligible);   // ระดับคลัง: ต้องรู้ค่าของทุก SKU ก่อน
                results.AddRange(batch);
            }

            return results;
        }

        // ======================================================
        // ประกอบ 1 SKU (ยังไม่ใส่ cv_percentile / xyz_class: ต้องทำระดับคลัง)
        // คืน (Record, cvEligible เต็มความละเอียดไว้จัดอันดับ)
        // ======================================================
        private static (SkuForecastResult, double?) AssembleOne(SkuModelRoutingState st, SkuCleansingCandidate cand, string asOfPeriod, AssemblerConfig cfg, int row, DateTime now)
        {
            decimal[] netD = cand.Demand24M;            // net_demand_qty_24m (ต้นฉบับก่อนตัดยอด)
            decimal[] cleanD = st.CleansedDemand24M;    // cleansed_demand_qty_24m
            double[] net = ToDouble(netD);
            double[] clean = ToDouble(cleanD);

            double adiThr = (double)cfg.AdiThreshold;

            // ----- ขั้น 2.1: ตรวจข้อมูลต้นทาง -----
            int negativeMonths = cand.RawSales24M.Count(v => v < 0m) + cand.OosUplift24M.Count(v => v < 0m);
            string sourceCheck = negativeMonths > 0 ? "NEGATIVE VALUE IN SOURCE" : (cand.MonthsPresent < 24 ? "MISSING MONTHS" : "OK");

            // ----- ขั้น 2.2: สถิติพื้นฐานยอดดิบบน Active Window -----
            Win rw = BuildWin(net);
            double? rawAdi = rw.Active == 0 ? (double?)null : (double)rw.Ttl / rw.Active;

            double? skew = null, kurt = null, jb = null, jbP = null;
            if (rw.Ttl >= 12)
            {
                if (rw.Sd == 0.0)
                {
                    skew = 0.0; kurt = 0.0;   // jb / p-value = NULL
                }
                else
                {
                    var s = SkewKurtJb(rw.W, rw.Avg, rw.Sd);
                    skew = s.Skewness; kurt = s.Kurtosis; jb = s.JbScore; jbP = s.PValue;
                }
            }

            // demand_distribution: บันได 8 ขั้น
            string distribution;
            if (rw.Active == 0) distribution = "No Sales";
            else if (rw.Ttl < 12) distribution = "<12M - SMA";
            else if (rawAdi > adiThr) distribution = "Intermittent/Lumpy";
            else if (rw.Sd == 0.0) distribution = "Constant";
            else if (jbP >= (double)cfg.JbPValueThreshold && Math.Abs(skew ?? 0.0) <= (double)cfg.SkewNormalLimit && Math.Abs(kurt ?? 0.0) <= (double)cfg.KurtNormalLimit) distribution = "Approx Normal";
            else if (skew > (double)cfg.SkewNormalLimit) distribution = "Right-Skewed";
            else if (skew < -(double)cfg.SkewNormalLimit) distribution = "Left-Skewed";
            else distribution = "Non-Normal";

            string outlierMethod = distribution == "Approx Normal" ? "SIGMA"
                : (distribution == "Right-Skewed" || distribution == "Left-Skewed" || distribution == "Non-Normal") ? "IQR"
                : "NONE";

            // ----- ขั้น 2.3: สหสัมพันธ์ยอดดิบ (ฟังก์ชันเดิมคืนค่าปัด 4 ตำแหน่งและคืน 0 เมื่อติด Guard) -----
            decimal rawTrendR = CleansingEngine.CalculateTrendR(netD, rw.Ttl, (decimal)rw.Sd);
            decimal rawLag12R = Lag12Plain(net);
            decimal rawY1 = YearTrend(net, 0);
            decimal rawY2 = YearTrend(net, 12);
            decimal rawDetrended = CleansingEngine.CalculateLag12R(netD, rw.Ttl);

            // ----- ขั้น 2.4: เพดาน, เครื่องมือ, สถานะ Outlier -----
            Caps caps = BuildCaps(rw, netD, (double)cfg.SigmaCapMultiplier);

            string tool;
            if (sourceCheck != "OK") tool = "DATA ERROR";
            else if (outlierMethod == "NONE" || (cfg.ProtectTrendSeason == 1m && (Math.Abs(rawTrendR) >= cfg.TrendCorrelation || rawDetrended >= cfg.SeasonalCorrelation))) tool = "KEEP";
            else if (outlierMethod == "SIGMA") tool = rw.Cv <= (double)cfg.CvThreshold ? "2SIGMA" : "3SIGMA";
            else if (!(caps.Iqr > 0.0)) tool = "KEEP";   // IQR <= 0 (หรือไม่มีค่า) -> Zero IQR
            else tool = (Math.Abs(skew ?? 0.0) <= (double)cfg.SkewModerateLimit && Math.Abs(kurt ?? 0.0) <= (double)cfg.KurtModerateLimit) ? "1.5IQR" : "3.0IQR";

            int? sigmaUsed = tool == "2SIGMA" ? 2 : tool == "3SIGMA" ? 3 : (int?)null;

            double? selectedCap = tool switch
            {
                "2SIGMA" => caps.U2,
                "3SIGMA" => caps.U3,
                "1.5IQR" => caps.U15,
                "3.0IQR" => caps.U3I,
                _ => null
            };

            string[] status = BuildStatuses(net, rw, tool, caps);
            int warningCount = status.Count(x => x.Contains("WARNING") || x.Contains("OUTLIER"));
            int extremeCount = status.Count(x => x.Contains("STRONG") || x.Contains("EXTREME"));
            int highCount = status.Count(x => x.Contains("HIGH"));
            int lowCount = status.Count(x => x.Contains("LOW"));
            int cappedHigh = selectedCap.HasValue ? net.Count(v => v > selectedCap.Value) : 0;

            string reviewStatus = sourceCheck != "OK" ? "DATA ERROR"
                : tool == "KEEP" ? "KEEP"
                : cappedHigh > 0 ? "CAP HIGH"
                : lowCount > 0 ? "REVIEW LOW"
                : (warningCount + extremeCount) > 0 ? "REVIEW BELOW CAP"
                : "OK";

            string action = sourceCheck != "OK" ? "FIX SOURCE"
                : cappedHigh > 0 ? "AUTO CAP HIGH ONLY"
                : lowCount > 0 ? "REVIEW LOW; KEEP VALUE"
                : "KEEP VALUE";

            string selectionReasonOutlier;
            if (sourceCheck != "OK") selectionReasonOutlier = sourceCheck;
            else if (rw.Active == 0) selectionReasonOutlier = "NO POSITIVE DEMAND";
            else if (rw.Ttl < 12) selectionReasonOutlier = "HISTORY <12 MONTHS";
            else if (rawAdi > adiThr) selectionReasonOutlier = "INTERMITTENT: PRESERVE OCCURRENCES";
            else if (rw.Sd == 0.0) selectionReasonOutlier = "CONSTANT DEMAND";
            else if (Math.Abs(rawTrendR) >= cfg.TrendCorrelation || rawDetrended >= cfg.SeasonalCorrelation) selectionReasonOutlier = "PROTECT TREND / SEASONAL PATTERN";
            else if (tool == "KEEP") selectionReasonOutlier = "ZERO IQR: REVIEW";
            else selectionReasonOutlier = "AUTO " + tool;

            // ----- ขั้น 2.5: สถิติสะอาดบน Active Window สะอาด -----
            Win cw = BuildWin(clean);
            double? cleanAdi = cw.Active == 0 ? (double?)null : (double)cw.Ttl / cw.Active;
            double cleanCv2 = CalculateCv2(clean, cw.Active);
            decimal detectTrend = CleansingEngine.CalculateTrendR(cleanD, cw.Ttl, (decimal)cw.Sd);
            decimal cleanDetrended = CleansingEngine.CalculateLag12R(cleanD, cw.Ttl);

            string pattern;
            if (cw.Active == 0) pattern = "No Sales";
            else if (clean[21] <= 0 && clean[22] <= 0 && clean[23] <= 0) pattern = "No Sales Last 3 Months";
            else if (cw.Ttl < 12) pattern = "NEW";
            else if (cleanAdi <= adiThr && cleanCv2 <= CvSquaredThreshold) pattern = "Smooth";
            else if (cleanAdi <= adiThr) pattern = "Erratic";
            else if (cleanCv2 <= CvSquaredThreshold) pattern = "Intermittent";
            else pattern = "Lumpy";

            // cv_eligible_xyz: เก็บค่าเต็มความละเอียดไว้จัดอันดับ
            double? cvEligible = ((pattern == "Smooth" || pattern == "Erratic") && cw.Ttl >= 12) ? cw.Cv : (double?)null;

            // ----- ขั้น 2.6: แมปโมเดลจาก Step 1-6 (ไม่ติด -> null ให้ PBI-12 ประเมิน Step 7 SES) -----
            string? modelId = st.AssignedModelId;
            string? demandGroup = modelId switch
            {
                "1.1" => "G1",
                "1.2" => "G2",
                "1.3" => "G3",
                "1.4" => "G4",
                "1.5" => "G5",
                "1.6" => "No Sales Last 3 Months",
                "1.7" => "No Sales",
                _ => null
            };
            // ข้อความเหตุผลตาม Excel COL:AR (ผูกกับรหัสโมเดล ไม่ใช้ข้อความจาก ModelEngine)
            string? modelReason = modelId switch
            {
                "1.7" => "NO POSITIVE DEMAND IN 24 MONTHS",
                "1.6" => "NO POSITIVE DEMAND IN LAST 3 MONTHS",
                "1.4" => "HISTORY <12 MONTHS: SMA",
                "1.5" => "ADI >1.32: CROSTON",
                "1.1" => "REPEATED PATTERN AFTER DETRENDING",
                "1.2" => "ABS(TREND r) > THRESHOLD",
                _ => null
            };

            decimal qtyReduced = Math.Round(Math.Max(0m, netD.Sum() - cleanD.Sum()), 4, MidpointRounding.AwayFromZero);

            var r = new SkuForecastResult
            {
                WarehouseCode = st.WarehouseCode,
                ItemId = st.ItemId,
                AsOfPeriod = asOfPeriod,

                RawTotal24M = Math.Round(cand.RawSales24M.Sum(), 4, MidpointRounding.AwayFromZero),
                OosTotal24M = Math.Round(cand.OosUplift24M.Sum(), 4, MidpointRounding.AwayFromZero),

                RawAvg = Round4(rw.Avg),
                RawSd = Round4(rw.Sd),
                RawCv = Round4(rw.Cv),
                SigmaUsed = sigmaUsed,
                RawTtlMth = rw.Ttl,
                RawActiveMth = rw.Active,
                RawAdi = Round4(rawAdi),
                RawSkewness = Round4(skew),
                RawKurtosis = Round4(kurt),
                JbStatistic = Round4(jb),
                JbPValue = Round4(jbP),
                DemandDistribution = distribution,
                OutlierMethod = outlierMethod,
                SelectedOutlierTool = tool,

                Lower2Sigma = Round4(caps.L2),
                Upper2Sigma = Round4(caps.U2),
                Lower3Sigma = Round4(caps.L3),
                Upper3Sigma = Round4(caps.U3),
                Q1 = Round4(caps.Q1),
                Q3 = Round4(caps.Q3),
                Iqr = Round4(caps.Iqr),
                Lower15Iqr = Round4(caps.L15),
                Upper15Iqr = Round4(caps.U15),
                Lower3Iqr = Round4(caps.L3I),
                Upper3Iqr = Round4(caps.U3I),

                WarningCount = warningCount,
                ExtremeCount = extremeCount,
                HighOutlierCount = highCount,
                LowOutlierCount = lowCount,
                OutlierReviewStatus = reviewStatus,
                Action = action,

                SalesRow = row,
                OosRow = row,
                SourceCheck = sourceCheck,
                SelectedUpperCap = Round4(selectedCap),

                RawTrendR = rawTrendR,
                RawLag12R = rawLag12R,
                RawYear1TrendR = rawY1,
                RawYear2TrendR = rawY2,
                RawDetrendedLag12R = rawDetrended,
                NegativeMonthsCount = negativeMonths,
                OutlierSelectionReason = selectionReasonOutlier,
                CappedHighMonthsCount = cappedHigh,

                CleanTtlMth = cw.Ttl,
                CleanActiveMth = cw.Active,
                CleanAdi = Round4(cleanAdi),
                CleanMean = Round4(cw.Avg),
                CleanSd = Round4(cw.Sd),
                CleanCv = Round4(cw.Cv),
                CleanCv2 = Round4(cleanCv2),
                DetectTrend = detectTrend,
                CleanDetrendedSeasonalR = cleanDetrended,
                DemandGroup = demandGroup,
                DemandPattern = pattern,

                AssignedModelId = modelId,
                CvEligibleXyz = Round4(cvEligible),
                RawRowRef = row,
                AppliedOutlierTool = tool == "DATA ERROR" ? "KEEP" : tool,   // ฟิลด์นี้มีได้ 5 ค่า (ไม่รวม DATA ERROR)
                QtyReducedByCap = qtyReduced,

                SelectionReason = modelReason,
                CleanLag12R = Lag12Plain(clean),
                CleanYear1TrendR = YearTrend(clean, 0),
                CleanYear2TrendR = YearTrend(clean, 12),

                CalculatedAt = now,
                CreatedAt = now,

                OutlierStatus24M = status,
                IsShortCircuited = st.IsShortCircuited
            };

            return (r, cvEligible);
        }

        // ======================================================
        // ระดับคลัง: cv_percentile = COUNTIF(eligible, "<" & v) / (COUNT(eligible) - 1) | COUNT <= 1 -> 0
        // แล้วตัด xyz_class (ใช้ Binary Search บนอาเรย์เรียงแล้ว: O(n log n) แทน O(n^2))
        // ======================================================
        private static void ApplyPercentileAndXyz(List<SkuForecastResult> batch, List<double?> cvEligible)
        {
            double[] sorted = cvEligible.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            Array.Sort(sorted);
            int count = sorted.Length;

            for (int i = 0; i < batch.Count; i++)
            {
                SkuForecastResult r = batch[i];
                double? percentile = null;

                if (cvEligible[i].HasValue)
                {
                    percentile = count <= 1 ? 0.0 : CountLess(sorted, cvEligible[i]!.Value) / (double)(count - 1);
                    r.CvPercentile = Round4(percentile.Value);
                }

                if (r.DemandPattern == "No Sales" || r.DemandPattern == "No Sales Last 3 Months") r.XyzClass = "Excluded";
                else if (r.CleanTtlMth < 12) r.XyzClass = "NEW";
                else if (r.DemandPattern == "Intermittent") r.XyzClass = "I";
                else if (r.DemandPattern == "Lumpy") r.XyzClass = "L";
                else if (percentile <= XThreshold) r.XyzClass = "X";
                else if (percentile <= YThreshold) r.XyzClass = "Y";
                else r.XyzClass = "Z";
            }
        }

        // จำนวนสมาชิกที่ < value ในอาเรย์เรียงจากน้อยไปมาก (Lower Bound)
        private static int CountLess(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ======================================================
        // เพดาน Sigma / Quartile / IQR บน Active Window (Guard ttl >= 4, ขอบล่างไม่ต่ำกว่า 0)
        // ======================================================
        private static Caps BuildCaps(Win w, decimal[] netD, double sigmaCapMultiplier)
        {
            var c = new Caps();
            if (w.Ttl < 4) return c;

            if (w.Sd != 0.0)
            {
                c.L2 = Math.Max(0.0, w.Avg - 2.0 * w.Sd);
                c.U2 = Math.Max(0.0, w.Avg + 2.0 * w.Sd);
                c.L3 = Math.Max(0.0, w.Avg - sigmaCapMultiplier * w.Sd);
                c.U3 = Math.Max(0.0, w.Avg + sigmaCapMultiplier * w.Sd);
            }

            // QUARTILE.INC บน Window: เรียงสำเนาของ Window แล้วใช้ PercentileInc เดิม
            decimal[] sorted = netD.Skip(24 - w.Ttl).ToArray();
            Array.Sort(sorted);
            double q1 = (double)IqrCappingEngine.PercentileInc(sorted, 0.25m);
            double q3 = (double)IqrCappingEngine.PercentileInc(sorted, 0.75m);
            double iqr = q3 - q1;
            c.Q1 = q1; c.Q3 = q3; c.Iqr = iqr;

            if (iqr > 0.0)
            {
                c.L15 = Math.Max(0.0, q1 - 1.5 * iqr);
                c.U15 = Math.Max(0.0, q3 + 1.5 * iqr);
                c.L3I = Math.Max(0.0, q1 - 3.0 * iqr);
                c.U3I = Math.Max(0.0, q3 + 3.0 * iqr);
            }
            return c;
        }

        // ======================================================
        // outlier_status_24m[1..24]: 13 สถานะตามลำดับขั้นของสูตร Excel (หัวข้อ 3.0)
        // ======================================================
        private static string[] BuildStatuses(double[] net, Win w, string tool, Caps c)
        {
            var status = new string[24];
            for (int i = 0; i < 24; i++)
            {
                int t = i + 1;
                double v = net[i];

                if (w.Active == 0) status[i] = "NO SALES";
                else if (t < 25 - w.Ttl) status[i] = "LEADING ZERO";
                else if (tool == "KEEP") status[i] = "KEEP";
                else if (tool == "2SIGMA" || tool == "3SIGMA")
                {
                    if (v > c.U3) status[i] = "STRONG HIGH >3SIGMA";
                    else if (v < c.L3) status[i] = "STRONG LOW >3SIGMA";
                    else if (v > c.U2) status[i] = "HIGH WARNING >2SIGMA";
                    else if (v < c.L2) status[i] = "LOW WARNING >2SIGMA";
                    else status[i] = "NORMAL";
                }
                else if (tool == "1.5IQR" || tool == "3.0IQR")
                {
                    if (v > c.U3I) status[i] = "EXTREME HIGH >3IQR";
                    else if (v < c.L3I) status[i] = "EXTREME LOW >3IQR";
                    else if (v > c.U15) status[i] = "HIGH OUTLIER >1.5IQR";
                    else if (v < c.L15) status[i] = "LOW OUTLIER >1.5IQR";
                    else status[i] = "NORMAL";
                }
                else status[i] = "DATA ERROR";
            }
            return status;
        }

        // ======================================================
        // Helpers
        // ======================================================

        // Active Window: เดือนแรกที่ยอด > 0 ถึงเดือนที่ 24 | ไม่มียอดเลย -> Ttl = 0
        private static Win BuildWin(double[] m)
        {
            var w = new Win();
            int first = -1;
            for (int i = 0; i < 24; i++)
            {
                if (m[i] > 0.0)
                {
                    w.Active++;
                    if (first < 0) first = i;
                }
            }
            if (w.Active == 0) return w;

            w.Ttl = 24 - first;
            w.W = new double[w.Ttl];
            Array.Copy(m, first, w.W, 0, w.Ttl);

            double avg = w.W.Average();
            double sd = w.Ttl < 2 || IsConstant(w.W) ? 0.0 : SampleSd(w.W, avg);   // ค่าคงที่ -> 0 พอดี กัน rounding error ของ double

            // Excel Template เก็บ avg / sd เป็นค่าปัด 4 ตำแหน่ง และสูตรถัดไป (z-score ของ Skew/Kurt/JB, เพดาน Sigma, CV) อ้างค่าที่ปัดแล้ว
            // (ยืนยันกับเวกเตอร์ทดสอบในสเปก: ถ้าใช้ค่าไม่ปัดจะคลาดที่ทศนิยมตำแหน่งที่ 4)
            w.Avg = (double)Round4(avg);
            w.Sd = (double)Round4(sd);
            w.Cv = w.Avg == 0.0 ? 0.0 : w.Sd / w.Avg;
            return w;
        }

        private static double SampleSd(double[] x, double mean)
        {
            double sumSq = 0.0;
            foreach (double v in x) sumSq += (v - mean) * (v - mean);
            return Math.Sqrt(sumSq / (x.Length - 1));
        }

        private static bool IsConstant(double[] x)
        {
            for (int i = 1; i < x.Length; i++)
                if (x[i] != x[0]) return false;
            return true;
        }

        // Sample Skewness / Excess Kurtosis / JB บน Window โดย z = (y - mean) / sd ใช้ mean และ sd ที่ปัด 4 ตำแหน่งแล้ว (ตามสเปก)
        // สูตรเดียวกับ NormalityEngine.ComputeStatistics (FM-10, FM-11, FM-12) ต่างแค่ค่า mean / sd ที่นำเข้า | เรียกเมื่อ n >= 12 และ sd != 0 เท่านั้น
        private static (double Skewness, double Kurtosis, double JbScore, double PValue) SkewKurtJb(double[] w, double mean, double sd)
        {
            int n = w.Length;
            double sumZ3 = 0.0, sumZ4 = 0.0;
            foreach (double v in w)
            {
                double z = (v - mean) / sd;
                double z2 = z * z;
                sumZ3 += z2 * z;
                sumZ4 += z2 * z2;
            }

            double skew = (n / ((n - 1.0) * (n - 2.0))) * sumZ3;
            double kurt = ((n * (n + 1.0)) / ((n - 1.0) * (n - 2.0) * (n - 3.0))) * sumZ4
                          - (3.0 * (n - 1.0) * (n - 1.0)) / ((n - 2.0) * (n - 3.0));

            double termSkew = (skew * (n - 2.0)) / Math.Sqrt(n * (n - 1.0));
            double termKurt = (((kurt * (n - 2.0) * (n - 3.0)) / (n - 1.0)) - 6.0) / (n + 1.0);
            double jb = (n / 6.0) * (termSkew * termSkew + (termKurt * termKurt) / 4.0);

            return (skew, kurt, jb, Math.Exp(-jb / 2.0));
        }

        // CV^2 ของขนาดความต้องการ เฉพาะงวดที่ยอด > 0: (SUMSQ - SUM^2/n)/(n-1) / (SUM/n)^2 | n < 2 -> 0
        private static double CalculateCv2(double[] m, int active)
        {
            if (active < 2) return 0.0;
            double sum = 0.0, sumSq = 0.0;
            foreach (double v in m)
            {
                if (v > 0.0) { sum += v; sumSq += v * v; }
            }
            double mean = sum / active;
            double variance = Math.Max(0.0, (sumSq - sum * sum / active) / (active - 1));
            return mean == 0.0 ? 0.0 : variance / (mean * mean);
        }

        // CORREL(M1:M12, M13:M24) | ผลรวมปีใดเป็น 0 หรือ SD ปีใดเป็น 0 -> 0
        private static decimal Lag12Plain(double[] m)
        {
            double[] y1 = Slice(m, 0, 12);
            double[] y2 = Slice(m, 12, 12);
            if (y1.Sum() == 0.0 || y2.Sum() == 0.0 || IsConstant(y1) || IsConstant(y2)) return 0m;
            return Round4(Pearson(y1, y2));
        }

        // CORREL(ปี, {1..12}) | ยอด > 0 น้อยกว่า 2 งวด หรือ SD = 0 -> 0
        private static decimal YearTrend(double[] m, int offset)
        {
            double[] y = Slice(m, offset, 12);
            if (y.Count(v => v > 0.0) < 2 || IsConstant(y)) return 0m;
            double[] t = new double[12];
            for (int i = 0; i < 12; i++) t[i] = i + 1;
            return Round4(Pearson(t, y));
        }

        private static double Pearson(double[] x, double[] y)
        {
            int n = x.Length;
            double xBar = x.Average(), yBar = y.Average();
            double sxy = 0.0, sxx = 0.0, syy = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - xBar, dy = y[i] - yBar;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            if (sxx == 0.0 || syy == 0.0) return 0.0;
            double r = sxy / Math.Sqrt(sxx * syy);
            if (double.IsNaN(r)) return 0.0;
            return Math.Max(-1.0, Math.Min(1.0, r));
        }

        private static double[] Slice(double[] m, int start, int length)
        {
            double[] r = new double[length];
            Array.Copy(m, start, r, 0, length);
            return r;
        }

        private static double[] ToDouble(decimal[] m)
        {
            double[] r = new double[m.Length];
            for (int i = 0; i < m.Length; i++) r[i] = (double)m[i];
            return r;
        }

        // ปัด 4 ตำแหน่ง AwayFromZero เฉพาะตอนเก็บลง Record | NaN/Infinity -> 0
        private static decimal Round4(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0m;
            return Math.Round((decimal)v, 4, MidpointRounding.AwayFromZero);
        }

        private static decimal? Round4(double? v)
        {
            return v.HasValue ? Round4(v.Value) : (decimal?)null;
        }
    }

}
