using System;

namespace DRP_Project.src.Cleansing
{

    // PBI-06 (SCRUM-17): Sigma Capping Engine (2-Sigma / 3-Sigma)
    // ตัดยอดขายส่วนเกินของ SKU สายแจกแจงปกติ (IsNormal == true จาก PBI-05) เพื่อป้องกันการสั่งซื้อล้นคลัง
    // สูตร FM-13 CV, FM-14 Sigma Capping | ส่งผลลัพธ์ต่อให้ PBI-08 (Cleansed Dataset Finalize Engine)
    // ไม่ยุ่งกับฐานข้อมูล (เกณฑ์ CV_THRESHOLD และ SIGMA_CAP_MULTIPLIER ที่อ่านจาก M_LOV ส่งเข้ามาทางพารามิเตอร์)

    // ผลลัพธ์ต่อ 1 SKU (Outbound Contract ของ PBI-06 -> PBI-08)
    public class CleansedDemandSeries
    {
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";

        public decimal[] OriginalDemand24M { get; set; } = new decimal[24];   // ยอดแท้จริงก่อนตัด (คัดลอกมา ไม่อ้างอิงอาเรย์เดียวกับ upstream)
        public decimal[] CleansedDemand24M { get; set; } = new decimal[24];   // ยอดหลังตัดเพดาน FM-14 ขั้นที่ 2.4
        public decimal[] QtyReduced24M { get; set; } = new decimal[24];       // ยอดที่ถูกตัดทอน (>= 0)

        public string CleansingMethod { get; set; } = "";   // '2SIGMA' หรือ '3SIGMA'
        public decimal CapLimit { get; set; }               // เพดานบน ปัดเศษ 4 ตำแหน่ง

        // Metadata ส่งต่อจาก PBI-05 ให้ PBI-08 (ไม่แก้ค่า)
        public int HistoryMonths { get; set; }
        public decimal AvgDemand { get; set; }
        public decimal Sd { get; set; }
        public decimal Skewness { get; set; }
        public decimal Kurtosis { get; set; }
        public decimal JbPValue { get; set; }
    }

    public static class SigmaCappingEngine
    {
        // ค่า Default เมื่อ M_LOV ไม่พบหรือแปลงค่าไม่ได้ (DEC-PBI06-02)
        public const decimal DefaultCvThreshold = 0.500000m;          // CV_THRESHOLD: CV <= ค่านี้ -> 2SIGMA
        public const decimal DefaultSigmaCapMultiplier = 3.000000m;   // SIGMA_CAP_MULTIPLIER: ตัวคูณของ 3SIGMA

        // ตัวคูณของ 2SIGMA คงที่ตามมาตรฐานสถิติระฆังคว่ำ (ไม่อ่านจาก M_LOV)
        public const decimal TwoSigmaMultiplier = 2.0m;

        public const string Method2Sigma = "2SIGMA";
        public const string Method3Sigma = "3SIGMA";

        // ตัดยอดทุก SKU: คืนผล 1 รายการต่อ 1 SKU (ผู้เรียกส่งเฉพาะ IsNormal == true มาจาก PBI-05)
        public static List<CleansedDemandSeries> CapAll(IEnumerable<SkuNormalityResult> normalResults, decimal cvThreshold, decimal sigmaCapMultiplier)
        {
            var results = new List<CleansedDemandSeries>();
            foreach (var r in normalResults)
            {
                results.Add(Cap(r, cvThreshold, sigmaCapMultiplier));
            }
            return results;
        }

        // ตัดยอด 1 SKU: CV (FM-13) -> เลือกวิธี -> เพดาน (FM-14) -> ตัดยอดรายเดือน -> ตรวจ State Invariant
        public static CleansedDemandSeries Cap(SkuNormalityResult r, decimal cvThreshold, decimal sigmaCapMultiplier)
        {
            if (!r.IsNormal)
                throw new ArgumentException($"PBI-06 รับเฉพาะ SKU ที่ IsNormal = true ({r.WarehouseCode}|{r.ItemId})");

            if (r.Demand24M == null || r.Demand24M.Length != 24)
                throw new ArgumentException($"Demand24M ต้องมี 24 งวด ({r.WarehouseCode}|{r.ItemId})");

            // FM-13: CV แล้วเลือกวิธีตัดยอด (เทียบด้วย CV เต็มความละเอียด ยังไม่ปัดเศษ)
            decimal cv = CalculateCv(r.AvgDemand, r.Sd);
            string method = SelectMethod(cv, cvThreshold);

            // FM-14 ขั้นที่ 2.1-2.3: เพดานบน
            decimal capLimit = CalculateCapLimit(r.AvgDemand, r.Sd, method, sigmaCapMultiplier);

            // FM-14 ขั้นที่ 2.4: ตัดยอดรายเดือน t = 1..24 (index 0..23)
            var original = (decimal[])r.Demand24M.Clone();   // คัดลอก กันอาเรย์ชนกับ candidate เดิม
            var cleansed = new decimal[24];
            var reduced = new decimal[24];

            for (int t = 0; t < 24; t++)
            {
                if (original[t] > capLimit)
                {
                    // เกินเพดาน -> ตัดลงมาเท่าเพดานพอดี
                    cleansed[t] = capLimit;
                    reduced[t] = original[t] - capLimit;
                }
                else
                {
                    // ไม่เกินเพดาน (รวมกรณีเท่าเพดานพอดี) -> คงยอดเดิม 100%
                    cleansed[t] = original[t];
                    reduced[t] = 0m;
                }

                // State Invariant: cleansed + qty_reduced == original ทุกงวด
                if (cleansed[t] + reduced[t] != original[t])
                    throw new InvalidOperationException($"State Invariant ผิดพลาด งวด {t + 1} ({r.WarehouseCode}|{r.ItemId})");
            }

            return new CleansedDemandSeries
            {
                WarehouseCode = r.WarehouseCode,
                ItemId = r.ItemId,
                OriginalDemand24M = original,
                CleansedDemand24M = cleansed,
                QtyReduced24M = reduced,
                CleansingMethod = method,
                CapLimit = capLimit,
                HistoryMonths = r.HistoryMonths,
                AvgDemand = r.AvgDemand,
                Sd = r.Sd,
                Skewness = r.Skewness,
                Kurtosis = r.Kurtosis,
                JbPValue = r.JbPValue
            };
        }

        // FM-13: Coefficient of Variation: cv = sd / avg_demand (ยอดเฉลี่ย = 0 -> cv = 0 กัน Divide by Zero)
        public static decimal CalculateCv(decimal avgDemand, decimal sd)
        {
            if (avgDemand == 0m) return 0m;
            return sd / avgDemand;
        }

        // FM-13 ขั้นที่ 2.3: cv <= threshold -> '2SIGMA' มิฉะนั้น '3SIGMA' (เท่าเกณฑ์พอดี = 2SIGMA)
        public static string SelectMethod(decimal cv, decimal cvThreshold)
        {
            return cv <= cvThreshold ? Method2Sigma : Method3Sigma;
        }

        // FM-14 ขั้นที่ 2.1-2.3: cap_limit = avg_demand + multiplier × sd ปัดเศษ 4 ตำแหน่ง (AwayFromZero)
        // 2SIGMA ใช้ 2.0 คงที่ | 3SIGMA ใช้ sigmaCapMultiplier จาก M_LOV (Default 3.0)
        public static decimal CalculateCapLimit(decimal avgDemand, decimal sd, string method, decimal sigmaCapMultiplier)
        {
            decimal multiplier = method == Method2Sigma ? TwoSigmaMultiplier : sigmaCapMultiplier;
            decimal allowance = multiplier * sd;
            return Math.Round(avgDemand + allowance, 4, MidpointRounding.AwayFromZero);
        }
    }

}
