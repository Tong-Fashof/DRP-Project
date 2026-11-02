namespace DRP_Project.src.Cleansing
{

    // PBI-07 (SCRUM-18): IQR Capping Engine (1.5 IQR / 3.0 IQR) - Tukey's IQR Outlier Engine
    // ตัดยอดขายส่วนเกินของ SKU สายแจกแจงไม่ปกติ (IsNormal == false จาก PBI-05) ด้วยสถิติเชิงอันดับที่ทนต่อค่าผิดปกติ
    // สูตร FM-15 Tukey's IQR Capping Thresholds | ส่งผลลัพธ์ต่อให้ PBI-08 (Cleansed Dataset Finalize Engine)
    // ใช้ DTO CleansedDemandSeries ตัวเดียวกับ PBI-06 (13 ฟิลด์ ประกาศไว้ใน SigmaCappingEngine.cs)
    // ไม่ยุ่งกับฐานข้อมูล (เกณฑ์ SKEW_MODERATE_LIMIT และ KURT_MODERATE_LIMIT ที่อ่านจาก M_LOV ส่งเข้ามาทางพารามิเตอร์)

    public static class IqrCappingEngine
    {
        // ค่า Default เมื่อ M_LOV ไม่พบหรือแปลงค่าไม่ได้ (DEC-PBI07-02)
        public const decimal DefaultSkewModerateLimit = 1.000000m;   // SKEW_MODERATE_LIMIT: |skew| <= ค่านี้ (และ kurt ผ่านด้วย) -> 1.5IQR
        public const decimal DefaultKurtModerateLimit = 3.000000m;   // KURT_MODERATE_LIMIT: |kurt| <= ค่านี้ (และ skew ผ่านด้วย) -> 1.5IQR

        // ตัวคูณคงที่ตามมาตรฐาน Tukey's Outlier (ไม่อ่านจาก M_LOV: ตัด IQR_CAP_MULTIPLIER ออกตาม DEC-PBI07-02)
        public const decimal Multiplier15 = 1.5m;
        public const decimal Multiplier30 = 3.0m;

        public const string MethodKeep = "KEEP";        // Zero IQR Protection
        public const string Method15Iqr = "1.5IQR";     // เหวี่ยงปานกลาง
        public const string Method30Iqr = "3.0IQR";     // เหวี่ยงรุนแรง

        // ตัดยอดทุก SKU: คืนผล 1 รายการต่อ 1 SKU (ผู้เรียกส่งเฉพาะ IsNormal == false มาจาก PBI-05)
        public static List<CleansedDemandSeries> CapAll(IEnumerable<SkuNormalityResult> nonNormalResults, decimal skewModerateLimit, decimal kurtModerateLimit)
        {
            var results = new List<CleansedDemandSeries>();
            foreach (var r in nonNormalResults)
            {
                results.Add(Cap(r, skewModerateLimit, kurtModerateLimit));
            }
            return results;
        }

        // ตัดยอด 1 SKU: Q1/Q3 (ขั้น 2.1-2.2) -> IQR + Guard (2.3) -> เลือกวิธี (2.4) -> เพดาน (2.5) -> ตัดยอดรายเดือน (2.6) -> ตรวจ State Invariant
        public static CleansedDemandSeries Cap(SkuNormalityResult r, decimal skewModerateLimit, decimal kurtModerateLimit)
        {
            if (r.IsNormal)
                throw new ArgumentException($"PBI-07 รับเฉพาะ SKU ที่ IsNormal = false ({r.WarehouseCode}|{r.ItemId})");

            if (r.Demand24M == null || r.Demand24M.Length != 24)
                throw new ArgumentException($"Demand24M ต้องมี 24 งวด ({r.WarehouseCode}|{r.ItemId})");

            var original = (decimal[])r.Demand24M.Clone();   // คัดลอก กันอาเรย์ชนกับ candidate เดิม (ห้ามแก้ยอดต้นฉบับ)
            var cleansed = new decimal[24];
            var reduced = new decimal[24];

            // FM-15 ขั้นที่ 2.1-2.2: เรียงลำดับแล้วหา Q1 (25%) และ Q3 (75%) แบบ PERCENTILE.INC
            // เรียงบนสำเนาแยก เพื่อให้ original คงลำดับเดือนไว้
            var sorted = (decimal[])original.Clone();
            Array.Sort(sorted);
            decimal q1 = PercentileInc(sorted, 0.25m);
            decimal q3 = PercentileInc(sorted, 0.75m);

            // FM-15 ขั้นที่ 2.3: IQR และ Guard Clause (เทียบหลังปัด 4 ตำแหน่ง ให้ตรงกับ 0.0000 ในสเปก)
            decimal iqr = q3 - q1;
            bool isZeroIqr = Math.Round(iqr, 4, MidpointRounding.AwayFromZero) == 0m;

            string method;
            decimal capLimit;

            if (isZeroIqr)
            {
                // KEEP: ไม่ตัดยอดใดๆ คงยอดเดิมทุกงวด และ CapLimit = 0.0000
                // (แยก branch ชัดเจน ไม่ใช้ min(demand, capLimit) เพราะ CapLimit = 0 จะทำให้ยอดสะอาดเป็น 0 หมด)
                method = MethodKeep;
                capLimit = 0.0000m;

                for (int t = 0; t < 24; t++)
                {
                    cleansed[t] = original[t];
                    reduced[t] = 0m;
                }
            }
            else
            {
                // FM-15 ขั้นที่ 2.4-2.5: เลือกวิธีจากความเบ้/ความโด่ง แล้วคำนวณเพดานบน
                method = SelectMethod(r.Skewness, r.Kurtosis, skewModerateLimit, kurtModerateLimit);
                capLimit = CalculateCapLimit(q3, iqr, method);

                // FM-15 ขั้นที่ 2.6: ตัดยอดรายเดือน t = 1..24 (index 0..23)
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
                }
            }

            // State Invariant: cleansed + qty_reduced == original ทุกงวด (ตรวจทั้งสอง branch)
            for (int t = 0; t < 24; t++)
            {
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

        // PERCENTILE.INC ของ Excel: pos = p * (n - 1) แล้วประมาณค่าเชิงเส้นระหว่างดัชนี floor(pos) กับ floor(pos) + 1
        // sorted ต้องเรียงจากน้อยไปมากแล้ว | n = 24: Q1 pos = 5.75, Q3 pos = 17.25
        // ใช้ decimal ล้วน (0.25 * 23 = 5.75 ตรงตัว ไม่มีความคลาดเคลื่อนแบบ double)
        public static decimal PercentileInc(decimal[] sorted, decimal p)
        {
            int n = sorted.Length;
            if (n == 0) return 0m;

            decimal pos = p * (n - 1);
            int lo = (int)Math.Floor(pos);
            decimal frac = pos - lo;

            if (lo >= n - 1) return sorted[n - 1];   // p = 1 หรือขอบอาเรย์: ไม่มีดัชนีถัดไปให้ประมาณค่า
            return sorted[lo] + frac * (sorted[lo + 1] - sorted[lo]);
        }

        // FM-15 ขั้นที่ 2.4: เปรียบเทียบความเบ้/ความโด่งกับเกณฑ์ M_LOV
        // |skew| <= SKEW_MODERATE_LIMIT และ |kurt| <= KURT_MODERATE_LIMIT -> '1.5IQR' | มิฉะนั้น '3.0IQR' (เท่าเกณฑ์พอดี = 1.5IQR)
        public static string SelectMethod(decimal skewness, decimal kurtosis, decimal skewModerateLimit, decimal kurtModerateLimit)
        {
            bool moderate = Math.Abs(skewness) <= skewModerateLimit && Math.Abs(kurtosis) <= kurtModerateLimit;
            return moderate ? Method15Iqr : Method30Iqr;
        }

        // FM-15 ขั้นที่ 2.5: cap_limit = Q3 + (multiplier x IQR) ปัดเศษ 4 ตำแหน่ง (AwayFromZero)
        // 1.5IQR ใช้ 1.5 คงที่ | 3.0IQR ใช้ 3.0 คงที่ | KEEP -> 0.0000
        public static decimal CalculateCapLimit(decimal q3, decimal iqr, string method)
        {
            if (method == MethodKeep) return 0.0000m;

            decimal multiplier = method == Method15Iqr ? Multiplier15 : Multiplier30;
            decimal allowance = multiplier * iqr;
            return Math.Round(q3 + allowance, 4, MidpointRounding.AwayFromZero);
        }
    }

}