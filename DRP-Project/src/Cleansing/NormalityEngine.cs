namespace DRP_Project.src.Cleansing
{

    // PBI-05 (SCRUM-16): Normality Testing Engine
    // ทดสอบว่ายอดขายแจกแจงปกติหรือไม่ เพื่อเลือกวิธีตัดยอด: Normal -> PBI-06 Sigma, Non-normal -> PBI-07 IQR
    // รับเฉพาะ SKU ที่ยัง PENDING หลัง Gate 1 + Gate 2 | สูตร FM-10 Skewness, FM-11 Kurtosis, FM-12 JB และ P-Value
    // ไม่ยุ่งกับฐานข้อมูล (เกณฑ์ที่อ่านจาก M_LOV ส่งเข้ามาทางพารามิเตอร์)

    // ผลลัพธ์ต่อ 1 SKU
    public class SkuNormalityResult
    {
        public string WarehouseCode { get; set; } = "";
        public string ItemId { get; set; } = "";
        public decimal[] Demand24M { get; set; } = new decimal[24];   // อ้างอิงอาเรย์เดียวกับ candidate (ไม่ได้คัดลอก)
        public int HistoryMonths { get; set; }

        public decimal AvgDemand { get; set; }   // FM-06 คิดจาก 24 งวดเต็ม ส่งต่อให้ PBI-06/07
        public decimal Sd { get; set; }          // FM-07 คิดจาก 24 งวดเต็ม ส่งต่อให้ PBI-06

        public decimal Skewness { get; set; }    // FM-10 ความเบ้ (+ เบ้ขวา, - เบ้ซ้าย)
        public decimal Kurtosis { get; set; }    // FM-11 ความโด่งส่วนเกิน (+ แหลม/หางหนา)
        public decimal JbScore { get; set; }     // FM-12 ยิ่งใกล้ 0 ยิ่งปกติ
        public decimal JbPValue { get; set; }    // FM-12 = exp(-JbScore/2) ยิ่งสูงยิ่งปกติ
        public bool IsNormal { get; set; }       // ผ่าน Triple Gate ครบ 3 ข้อ = true
    }

    public static class NormalityEngine
    {
        // ค่า Default เมื่อ M_LOV ไม่พบหรือแปลงค่าไม่ได้
        public const decimal DefaultPValueThreshold = 0.050000m;   // JB_PVALUE_THRESHOLD: ผ่านเมื่อ p >= ค่านี้
        public const decimal DefaultSkewLimit = 0.500000m;         // SKEW_NORMAL_LIMIT:   ผ่านเมื่อ |skew| <= ค่านี้
        public const decimal DefaultKurtLimit = 1.000000m;         // KURT_NORMAL_LIMIT:   ผ่านเมื่อ |kurt| <= ค่านี้

        // ประวัติขั้นต่ำที่ทดสอบได้ (Rules!B4) ตัวป้องกันสำรอง เพราะ Gate 1 ล็อก SKU ประวัติ < 12 เป็น KEEP ไปก่อนแล้ว
        private const int MinHistoryMonths = 12;

        // ประเมินทุก SKU: คืนผล 1 รายการต่อ 1 candidate (ผู้เรียกกรองเฉพาะ PENDING มาก่อน)
        public static List<SkuNormalityResult> EvaluateAll(IEnumerable<SkuCleansingCandidate> pendingCandidates, decimal pValueThreshold, decimal skewLimit, decimal kurtLimit)
        {
            var results = new List<SkuNormalityResult>();
            foreach (var c in pendingCandidates)
            {
                results.Add(Evaluate(c, pValueThreshold, skewLimit, kurtLimit));
            }
            return results;
        }

        // ประเมิน 1 SKU: คำนวณสถิติ -> ตัดสิน Triple Gate -> สร้างผลลัพธ์
        public static SkuNormalityResult Evaluate(SkuCleansingCandidate c, decimal pValueThreshold, decimal skewLimit, decimal kurtLimit)
        {
            if (c.Demand24M == null || c.Demand24M.Length != 24)
                throw new ArgumentException($"Demand24M ต้องมี 24 งวด ({c.warehouse_code}|{c.item_id})");

            // ใช้ HistoryMonths และ Sd ที่ Gate 1 คำนวณไว้ (ต้องรัน CleansingCenterAll ก่อน)
            var st = ComputeStatistics(c.Demand24M, c.HistoryMonths, c.Sd);

            // Triple Gate: ต้องผ่านครบทั้ง 3 ข้อ (ใช้ >= และ <= ผ่านเมื่อเท่าเกณฑ์พอดี)
            // เทียบด้วยค่า double ก่อนปัดเศษ (แบบ Excel)
            bool isNormal =
                st.PValue >= (double)pValueThreshold &&
                Math.Abs(st.Skewness) <= (double)skewLimit &&
                Math.Abs(st.Kurtosis) <= (double)kurtLimit;

            return new SkuNormalityResult
            {
                WarehouseCode = c.warehouse_code,
                ItemId = c.item_id,
                Demand24M = c.Demand24M,
                HistoryMonths = c.HistoryMonths,
                AvgDemand = CleansingEngine.CalculateAvgDemand(c.Demand24M),   // FM-06 บน 24 งวด
                Sd = c.Sd,
                Skewness = Round4(st.Skewness),
                Kurtosis = Round4(st.Kurtosis),
                JbScore = Round4(st.JbScore),
                JbPValue = Round4(st.PValue),
                IsNormal = isNormal
            };
        }

        // แยกสาย Normal -> PBI-06, Non-normal -> PBI-07 (ต้องครบทุก SKU ไม่ซ้ำ)
        public static (List<SkuNormalityResult> Normal, List<SkuNormalityResult> NonNormal) Partition(List<SkuNormalityResult> results)
        {
            var normal = results.Where(r => r.IsNormal).ToList();
            var nonNormal = results.Where(r => !r.IsNormal).ToList();
            if (normal.Count + nonNormal.Count != results.Count)
                throw new InvalidOperationException("Partition ผิดพลาด: จำนวน SKU ไม่ครบ");
            return (normal, nonNormal);
        }

        // FM-10, FM-11, FM-12 บน Active Window (double เต็มความละเอียด ยังไม่ปัดเศษ)
        // demand = ยอดขาย 24 งวด (index 0 = เดือน 1), historyMonths = n (FM-03), sd = SD 24 งวด (FM-07)
        public static (double Skewness, double Kurtosis, double JbScore, double PValue) ComputeStatistics(decimal[] demand, int historyMonths, decimal sd)
        {
            // 2.1 ข้อมูลไม่พอหรือยอดนิ่งสนิท -> ค่าปลอดภัย skew = 0, kurt = 0, JB = 0, p = 1 (ผลคือ IsNormal = true)
            if (historyMonths < MinHistoryMonths || sd == 0m) return (0.0, 0.0, 0.0, 1.0);

            // 2.2 Active Window = เดือนที่ 25-n ถึง 24 (ไม่นับเลข 0 ก่อนเริ่มขาย) เช่น n = 15 -> index 9..23
            int n = Math.Min(historyMonths, 24);
            int start = 24 - n;

            // ค่าเฉลี่ย ȳ = Σy / n
            double sum = 0.0;
            for (int i = 0; i < n; i++) sum += (double)demand[start + i];
            double yBar = sum / n;

            // SD ตัวอย่างของ Window s = √(Σ(y-ȳ)² / (n-1))  (ต่างจาก sd 24 งวดที่รวมเลข 0 นำหน้า)
            double sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = (double)demand[start + i] - yBar;
                sumSq += d * d;
            }
            double s = Math.Sqrt(sumSq / (n - 1));

            // กันหารศูนย์: ยอดเท่ากันทุกเดือนใน Window (sd 24 งวดอาจไม่เป็น 0 เพราะมีเลข 0 นำหน้า)
            if (s == 0.0 || double.IsNaN(s)) return (0.0, 0.0, 0.0, 1.0);

            // z = (y-ȳ)/s แล้วรวมกำลัง 3 และ 4
            double sumZ3 = 0.0, sumZ4 = 0.0;
            for (int i = 0; i < n; i++)
            {
                double z = ((double)demand[start + i] - yBar) / s;
                double z2 = z * z;
                sumZ3 += z2 * z;
                sumZ4 += z2 * z2;
            }

            // FM-10 Skewness = n/((n-1)(n-2)) × Σz³   (เขียน n - 1.0 เพื่อกัน integer division)
            double skewFactor = (double)n / ((n - 1.0) * (n - 2.0));
            double skewness = skewFactor * sumZ3;

            // FM-11 Excess Kurtosis = n(n+1)/((n-1)(n-2)(n-3)) × Σz⁴ - 3(n-1)²/((n-2)(n-3))
            double kurtFactor1 = (n * (n + 1.0)) / ((n - 1.0) * (n - 2.0) * (n - 3.0));
            double kurtOffset = (3.0 * (n - 1.0) * (n - 1.0)) / ((n - 2.0) * (n - 3.0));
            double kurtosis = kurtFactor1 * sumZ4 - kurtOffset;

            // FM-12 JB (สูตรเซลล์ AI2): jb = (n/6) × (term_skew² + term_kurt²/4), p = exp(-jb/2)
            double termSkew = (skewness * (n - 2.0)) / Math.Sqrt(n * (n - 1.0));
            double termSkewSq = termSkew * termSkew;
            double termKurt = (((kurtosis * (n - 2.0) * (n - 3.0)) / (n - 1.0)) - 6.0) / (n + 1.0);
            double termKurtQuarter = (termKurt * termKurt) / 4.0;
            double jbScore = (n / 6.0) * (termSkewSq + termKurtQuarter);
            double pValue = Math.Exp(-jbScore / 2.0);

            return (skewness, kurtosis, jbScore, pValue);
        }

        // ปัด 4 ตำแหน่งแบบ AwayFromZero เฉพาะตอนเก็บลง DTO (ห้ามปัดระหว่างคำนวณ) NaN/Infinity คืน 0
        public static decimal Round4(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0m;
            return Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero);
        }
    }

}
