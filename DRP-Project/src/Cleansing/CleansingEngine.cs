namespace DRP_Project.src.Cleansing
{
    public class SkuCleansingCandidate
    {
        public string warehouse_code { get; set; } = "";
        public string item_id { get; set; } = "";
        public decimal[] Demand24M { get; set; } = new decimal[24];
        public int HistoryMonths { get; set; }
        public int ActiveMonths { get; set; }
        public decimal Adi { get; set; }
        public decimal Sd { get; set; }
        public decimal TrendR { get; set; }     // PBI-04: FM-08 สหสัมพันธ์แนวโน้ม
        public decimal Lag12R { get; set; }     // PBI-04: FM-09 สหสัมพันธ์ฤดูกาลแบบขจัดแนวโน้ม
        public string CleansingStatus { get; set; } = "PENDING";
        public string? CleansingMethod { get; set; }
        public string? CleansingReason { get; set; }
    }

    public static class CleansingEngine
    {
        // ### Property คือข้อมูลหรือคุณสมบัติที่กำหนดไว้ใน SkuCleansingCandidate เพื่อให้สินค้าแต่ละรายการมีข้อมูลเหล่านั้น ###
        /* Property           ใช้เก็บอะไร
          HistoryMonths       จำนวนเดือนที่มีประวัติข้อมูล
          ActiveMonths        จำนวนเดือนที่มี Demand
          Adi                 ค่า Average Demand Interval
          Sd                  ค่า Standard Deviation
          TrendR              ค่าสหสัมพันธ์แนวโน้ม (Pearson r ของยอดขายเทียบเวลา)
          Lag12R              ค่าสหสัมพันธ์ฤดูกาล Lag-12 หลังขจัดแนวโน้ม (Partial Correlation)
          CleansingStatus     สถานะ เช่น KEEP, PENDING
          CleansingMethod     วิธีจัดการข้อมูล
          CleansingReason     เหตุผลที่เลือกสถานะนั้น
          Demand24M           ข้อมูล Demand ย้อนหลัง 24 เดือน */

        private const decimal SeasonalThreshold = 0.5m;
        private const decimal TrendThreshold = 0.5m;
        private const double MaxTrendCorrelation = 0.999999;   // Rules!B21: ป้องกันตัวหาร Partial Correlation เป็นศูนย์

        // PBI-03 -> PBI-04: ทุก SKU ผ่าน Gate 1 ก่อน เฉพาะที่ยัง PENDING จึงส่งต่อ Gate 2
        // (SKU ที่ KEEP จาก Gate 1 ถูกล็อก ไม่เข้า Gate 2)
        public static void CleansingCenterAll(List<SkuCleansingCandidate> list, decimal adiThreshold, decimal protectTrendSeason)
        {
            foreach (var c in list)
            {
                GateEvaluate_1(c, adiThreshold);

                if (c.CleansingStatus == "PENDING")
                {
                    GateEvaluate_2(c, protectTrendSeason);
                }
            }
        }

        // Gate 1 (PBI-03): ตรวจสอบ History, ADI, SD เพื่อคัดกรองสินค้าที่ไม่เหมาะสม
        public static void GateEvaluate_1(SkuCleansingCandidate c, decimal adiThreshold)
        {
            c.HistoryMonths = CalculateHistoryMonths(c.Demand24M);
            c.ActiveMonths = CalculateActiveMonths(c.Demand24M);
            c.Adi = CalculateAdi(c.ActiveMonths);
            c.Sd = CalculateSd(c.Demand24M);

            // กำหนดข้อความเหตุผลตามลำดับเงื่อนไขก่อนหลัง(DEC - PBI03 - 02, อ้างอิง FM - 03, FM - 05, FM - 07):
            // หาก HistoryMonths< 12 → 'History < 12M'(FM - 03)
            // หาก Adi > ADI_THRESHOLD → 'Intermittent Demand (ADI > 1.32)'(FM - 05)
            // หาก Sd == 0.0000m → 'Zero Variance (SD = 0)'(FM - 07)
            // กรณี PENDING → null
            if (c.HistoryMonths < 12)
            {
                c.CleansingReason = "History < 12M";
            }
            else if (c.Adi > adiThreshold)
            {
                c.CleansingReason = "Intermittent Demand (ADI > 1.32)";
            }
            else if (c.Sd == 0m)
            {
                c.CleansingReason = "Zero Variance (SD = 0)";
            }
            else
            {
                c.CleansingReason = null;
            }


            if (c.CleansingReason != null)
            {
                c.CleansingStatus = "KEEP";
                c.CleansingMethod = "KEEP";
            }
            else
            {
                c.CleansingStatus = "PENDING";
                c.CleansingMethod = null;
            }
        }

        // Gate 2 (PBI-04): คุ้มครองสินค้าตามฤดูกาลและสินค้าที่มีแนวโน้มชัดเจนผ่าน Protect Flag
        // คำนวณ TrendR และ Lag12R ให้ครบทุก SKU เสมอ แม้ PROTECT_TREND_SEASON != 1 (ธงปิดแค่ข้ามการตัดสินเป็น KEEP)
        public static void GateEvaluate_2(SkuCleansingCandidate c, decimal protectTrendSeason)
        {
            c.TrendR = CalculateTrendR(c.Demand24M, c.HistoryMonths, c.Sd);
            c.Lag12R = CalculateLag12R(c.Demand24M, c.HistoryMonths);

            string? reason = null;

            if (protectTrendSeason == 1m)
            {
                // ลำดับความสำคัญตาม DEC-PBI11: ฤดูกาลก่อนแนวโน้ม
                if (c.HistoryMonths >= 24 && c.Lag12R >= SeasonalThreshold)
                {
                    reason = "Seasonal Protected (Lag-12 r >= 0.5)";
                }
                else if (Math.Abs(c.TrendR) >= TrendThreshold)
                {
                    reason = "Trend Protected (|r| >= 0.5)";
                }
            }

            c.CleansingReason = reason;
            if (reason != null)
            {
                c.CleansingStatus = "KEEP";
                c.CleansingMethod = "KEEP";
            }
            else
            {
                c.CleansingStatus = "PENDING";
                c.CleansingMethod = null;
            }
        }

        // FM-03: History Months - จำนวนเดือนประวัตินับจากเริ่มขายจริง: n
        public static int CalculateHistoryMonths(decimal[] demand)
        {
            int firstActiveMonth = 0;
            for (int i = 0; i < 24; i++)
            {
                if (demand[i] > 0m)
                {
                    firstActiveMonth = i + 1;
                    break;
                }
            }

            if (firstActiveMonth == 0) return 0;
            return 24 - firstActiveMonth + 1;
        }

        // FM-04: Active Months - จำนวนเดือนที่มีการขายจริง
        public static int CalculateActiveMonths(decimal[] demand)
        {
            int count = 0;
            for (int i = 0; i < 24; i++)
            {
                if (demand[i] > 0m) count++;
            }
            return count;
        }

        // FM-05: Average Demand Interval (ADI) - ช่วงห่างคำสั่งซื้อเฉลี่ย: ADI
        public static decimal CalculateAdi(int activeMonths)
        {
            if (activeMonths == 0) return 0m;
            return 24.0m / activeMonths;
        }

        // FM-06: Average Monthly Demand - ยอดขายเฉลี่ยต่อเดือน
        public static decimal CalculateAvgDemand(decimal[] demand)
        {
            decimal sum = 0m;
            for (int i = 0; i < 24; i++)
            {
                sum += demand[i];
            }

            return sum / 24.0m;
        }

        // FM-07: Sample Standard Deviation [SD (sample, n-1 = 23)] - ส่วนเบี่ยงเบนมาตรฐานของยอดขายต่อเดือน (SD)
        public static decimal CalculateSd(decimal[] demand)
        {
            decimal avg = CalculateAvgDemand(demand);
            decimal sumSqDiff = 0m;
            for (int i = 0; i < 24; i++)
            {
                decimal diff = demand[i] - avg;
                sumSqDiff += diff * diff;
            }

            double variance = (double)(sumSqDiff / 23.0m);
            //return Math.Round((decimal)Math.Sqrt(Math.Max(0.0, variance)), 4);
            return (decimal)Math.Sqrt(Math.Max(0.0, variance));
        }

        // FM-08: Trend Pearson Correlation - สหสัมพันธ์ของแนวโน้ม (ยอดขายเทียบลำดับเวลา บน Active Window)
        public static decimal CalculateTrendR(decimal[] demand, int historyMonths, decimal sd)
        {
            // ขั้นที่ 2.1: history < 12 หรือยอดนิ่งสนิท -> 0
            if (historyMonths < 12 || sd == 0m) return 0m;

            // ขั้นที่ 2.2: Active Window k = historyMonths, t = (25-k)..24, y = demand[25-k .. 24]
            int k = Math.Min(historyMonths, 24);
            int start = 24 - k;                         // index เริ่มต้น (0-based)
            if (demand.Skip(start).Take(k).Distinct().Count() == 1) return 0m;   // ยอดเท่ากันทุกงวดในช่วง (SD = 0)

            var t = new double[k];
            var y = new double[k];
            for (int i = 0; i < k; i++)
            {
                t[i] = start + i + 1;                   // เดือนที่ (1-based)
                y[i] = (double)demand[start + i];
            }

            // ขั้นที่ 2.3 - 2.5
            return Math.Round((decimal)Correl(t, y), 4, MidpointRounding.AwayFromZero);
        }

        // FM-09: Detrended Seasonal Correlation (Lag-12 r) - Partial Correlation ขจัดอิทธิพลของเวลา
        public static decimal CalculateLag12R(decimal[] demand, int historyMonths)
        {
            // ขั้นที่ 2.1 เงื่อนไขที่ 1-3
            if (historyMonths < 24) return 0m;
            if (demand.Take(12).Distinct().Count() == 1) return 0m;              // ปีที่ 1 นิ่งสนิท
            if (demand.Skip(12).Take(12).Distinct().Count() == 1) return 0m;     // ปีที่ 2 นิ่งสนิท

            var y1 = new double[12];
            var y2 = new double[12];
            var t1 = new double[12];
            var t2 = new double[12];
            for (int i = 0; i < 12; i++)
            {
                y1[i] = (double)demand[i];
                y2[i] = (double)demand[12 + i];
                t1[i] = i + 1;
                t2[i] = 13 + i;
            }

            // ขั้นที่ 2.2: ใช้ double เต็มความละเอียดตลอดการคำนวณตัวแปรย่อย
            double r12 = Correl(y1, y2);
            double r1t = Correl(y1, t1);
            double r2t = Correl(y2, t2);

            // ขั้นที่ 2.1 เงื่อนไขที่ 4
            if (Math.Abs(r1t) >= MaxTrendCorrelation || Math.Abs(r2t) >= MaxTrendCorrelation) return 0m;

            // ขั้นที่ 2.3: Partial Correlation
            double numerator = r12 - (r1t * r2t);
            double denominator = Math.Sqrt((1.0 - r1t * r1t) * (1.0 - r2t * r2t));
            if (denominator <= 0.0) return 0m;

            // ขั้นที่ 2.4: Clamp [-1, 1] แล้วปัดเศษ 4 ตำแหน่งตอนเก็บผลลัพธ์
            double raw = numerator / denominator;
            return Math.Round((decimal)Math.Max(-1.0, Math.Min(1.0, raw)), 4, MidpointRounding.AwayFromZero);
        }

        // CORREL(X, Y): Pearson r (ตามสูตร FM-08 ขั้นที่ 2.3-2.5) คืน 0 ถ้า sum_x2 หรือ sum_y2 เป็นศูนย์
        private static double Correl(double[] x, double[] y)
        {
            int n = x.Length;
            double xBar = 0.0, yBar = 0.0;
            for (int i = 0; i < n; i++)
            {
                xBar += x[i];
                yBar += y[i];
            }
            xBar /= n;
            yBar /= n;
            double sumXY = 0.0, sumX2 = 0.0, sumY2 = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - xBar;
                double dy = y[i] - yBar;
                sumXY += dx * dy;
                sumX2 += dx * dx;
                sumY2 += dy * dy;
            }

            if (sumX2 == 0.0 || sumY2 == 0.0) return 0.0;

            double r = sumXY / Math.Sqrt(sumX2 * sumY2);
            if (double.IsNaN(r)) return 0.0;
            return Math.Max(-1.0, Math.Min(1.0, r));
        }
    }

}
