using DRP_Project.src.Cleansing;
using DRP_Project.src.NetDemand;
using DRP_Project.Tests;
using Microsoft.Data.SqlClient;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DRP_Project
{
    // ======================================================================
    // DRP Outlier Cleansing Engine: จุดเริ่มต้นของโปรแกรม (Console App)
    // ลำดับ: ต่อ DB -> STEP 1 NetDemand -> STEP 2 Gate 1/2 -> STEP 3 Normality -> STEP 4 Sigma -> STEP 5 IQR -> STEP 6 Finalize
    // Argument: --warehouse <รหัสคลัง> (ไม่ใส่ = ทุกคลัง) | --period <yyyyMM> (ไม่ใส่ = งวดล่าสุดใน Staging)
    // Program.cs ควบคุมลำดับขั้นตอนเท่านั้น: สูตรอยู่ใน Engine | งาน SQL อยู่ใน Repository / NetDemandPreprocessor | สรุปผลหน้าจออยู่ใน CleansingPipeline
    // Log: Console + ไฟล์ logs/drp-yyyyMMdd.log | Exit Code: 0 = สำเร็จ, 1 = Argument ผิด, 2 = ประมวลผลล้มเหลว
    // ======================================================================
    class Program
    {
        private static string? targetWarehouseCode = null;   // กรองเฉพาะคลังนี้ (null = ทุกคลัง)
        private static string? targetPeriod = null;          // งวดที่ต้องการประมวลผล รูปแบบ yyyyMM (null = ค่าเริ่มต้นของ NetDemand)

        // Exit Code: ให้ Scheduler (Task Scheduler / SQL Agent) แยกผลการรันได้
        private const int ExitSuccess = 0;
        private const int ExitArgumentError = 1;   // Argument ผิดรูปแบบ
        private const int ExitRuntimeError = 2;    // ประมวลผลล้มเหลว (ดูรายละเอียดใน logs/)

        private static string currentStep = "Startup";   // ขั้นที่กำลังทำ ใช้ระบุจุดที่ล้มเหลวตอน catch ใน Main

        static int Main(string[] args)
        {
            // โหมด Unit Test: ตรวจก่อนต่อฐานข้อมูล แล้วจบโปรแกรมทันที (ใช้ Console ล้วน ไม่เปิด Logger)
            // รัน Unit Test (ไม่ต่อฐานข้อมูล): --test-netdemand | --test-gate1 | --test-gate2 | --test-normality | --test-sigma | --test-iqr | --test-finalize
            if (args.Contains("--test-netdemand")) { NetDemandTests.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-gate1")) { Gate1.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-gate2")) { Gate2.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-normality")) { NormalityTests.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-sigma")) { SigmaCappingTests.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-iqr")) { IqrCappingTests.RunUnitTests(); return ExitSuccess; }
            if (args.Contains("--test-finalize")) { FinalizeTests.RunUnitTests(); return ExitSuccess; }

            // เปิด Logger หลังตรวจโหมด Test เพราะโหมด Test ใช้ Console ล้วนและไม่ต่อฐานข้อมูล
            InitLogger();
            // จับเวลารวมทั้ง run (เวลาของแต่ละขั้นดูจาก Timestamp ใน log)
            var stopwatch = Stopwatch.StartNew();

            // Fail-fast: ขั้นใดล้มเหลว ให้หยุดทั้ง run เพราะ Save เป็นแบบลบแล้วเขียนใหม่ทั้งชุด การข้ามบาง SKU จะทำให้ข้อมูลหายเงียบ
            try
            {
                if (!TryParseArgs(args)) return ExitArgumentError;   // Argument ผิดรูปแบบ -> จบโปรแกรม (log ไว้แล้วใน TryParseArgs)

                Log.Information("เริ่มประมวลผล Warehouse={Warehouse} Period={Period}", targetWarehouseCode ?? "ALL", targetPeriod ?? "DEFAULT");

                currentStep = "เชื่อมต่อฐานข้อมูล";
                Log.Information(">>> {Step}", currentStep);
                // Connection ตัวเดียวใช้ร่วมทุก Step | using ปิดให้อัตโนมัติเมื่อออกจาก try (ทั้งกรณีสำเร็จและเกิด exception)
                using SqlConnection connection = ConnectData();

                // ===== STEP 1: NetDemand Preprocessing =====
                // เตรียมยอดความต้องการสุทธิรายเดือนลงตาราง DRP_FCT_SKU_MONTHLY_DEMAND_PREP
                currentStep = "STEP 1: NetDemand Preprocessing";
                Log.Information(">>> {Step}", currentStep);
                NetDemandPreprocessor.Run(connection, targetPeriod, targetWarehouseCode);

                // ===== STEP 2: Cleansing Gate 1/2 (PBI-03, PBI-04) =====
                // โหลดเกณฑ์จาก M_LOV และยอดขาย 24 เดือนต่อ SKU แล้วคัดกรอง KEEP / PENDING
                currentStep = "STEP 2: Cleansing Gate 1/2";
                Log.Information(">>> {Step}", currentStep);
                List<SkuCleansingCandidate> candidates = CleansingPipeline.RunGates(connection, targetWarehouseCode);

                // ===== STEP 3: Normality Testing (PBI-05) =====
                // เฉพาะ SKU ที่ยัง PENDING: แยกสาย Normal -> PBI-06 Sigma / Non-normal -> PBI-07 IQR
                currentStep = "STEP 3: Normality Testing";
                Log.Information(">>> {Step}", currentStep);
                var (normal, nonNormal) = CleansingPipeline.RunNormality(connection, candidates);   // nonNormal -> PBI-07 IQR Capping

                // ===== STEP 4: Sigma Capping (PBI-06) =====
                // เฉพาะสาย Normal: ตัดยอดส่วนเกินด้วย 2SIGMA / 3SIGMA แล้วส่งต่อ PBI-08
                currentStep = "STEP 4: Sigma Capping";
                Log.Information(">>> {Step}", currentStep);
                List<CleansedDemandSeries> cleansed = CleansingPipeline.RunSigmaCapping(connection, normal);

                // ===== STEP 5: IQR Capping (PBI-07) =====
                // เฉพาะสาย Non-normal: ตัดยอดส่วนเกินด้วย 1.5IQR / 3.0IQR (หรือ KEEP เมื่อ IQR = 0) แล้วส่งต่อ PBI-08
                currentStep = "STEP 5: IQR Capping";
                Log.Information(">>> {Step}", currentStep);
                List<CleansedDemandSeries> cleansedIqr = CleansingPipeline.RunIqrCapping(connection, nonNormal);

                // ===== STEP 6: Cleansed Dataset Finalize (PBI-08) =====
                // รวม KEEP (Gate 1/2) + Sigma (PBI-06) + IQR (PBI-07) -> คำนวณสถิติสะอาดใหม่
                // บันทึกประวัติรายเดือนลง DRP_FCT_SKU_OUTLIER_CLEANSING แล้วส่ง DTO ต่อให้ Stage 1.2 (PBI-09 ถึง PBI-12)
                currentStep = "STEP 6: Cleansed Dataset Finalize";
                Log.Information(">>> {Step}", currentStep);
                // DTO ผลลัพธ์สุดท้ายของ Stage 1.1 | ยังไม่มี Stage 1.2 มารับต่อ จึงเก็บไว้ในตัวแปรก่อน
                List<CleansedSkuDemandDto> cleansedSkuDtos = CleansingPipeline.RunFinalize(connection, targetWarehouseCode, candidates, cleansed, cleansedIqr);

                Log.Information("ประมวลผลเสร็จสิ้น ใช้เวลา {Seconds:N1} วินาที", stopwatch.Elapsed.TotalSeconds);
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                // Console แสดงสาเหตุสั้นๆ | ไฟล์ log เก็บ Stack Trace เต็ม
                Log.Fatal(ex, "ประมวลผลล้มเหลวที่ขั้น [{Step}]: {Reason}", currentStep, ex.Message);
                return ExitRuntimeError;
            }
            finally
            {
                Log.CloseAndFlush();   // ต้องเรียกเสมอ เพื่อให้ข้อความท้ายสุดถูกเขียนลงไฟล์
            }
        }


        // ======================================================
        // ตั้งค่า Serilog: Console (อ่านง่าย) + ไฟล์รายวัน logs/drp-yyyyMMdd.log (เก็บ 7 วัน)
        // RunId = รหัสสั้นต่อการรัน 1 ครั้ง ใช้ไล่ log เมื่อรันหลายครั้ง/หลายคลังในวันเดียวกัน
        // ห้าม log connection string / password
        // ======================================================
        private static void InitLogger()
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "logs", "drp-.log");
            string runId = Guid.NewGuid().ToString("N")[..8];

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()   // Debug (รายละเอียดราย SKU) ปิดไว้เป็นค่าเริ่มต้น
                .Enrich.WithProperty("RunId", runId)

                // Console: ไม่แสดง Stack Trace เพื่อให้อ่านง่าย (สาเหตุอยู่ในบรรทัด Fatal แล้ว)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}")

                // ไฟล์: มี RunId และ Stack Trace เต็ม | ชื่อไฟล์ลงท้ายด้วย "-" เพื่อให้ Serilog ต่อวันที่ (yyyyMMdd) ให้อัตโนมัติ
                .WriteTo.File(
                    path: logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),   // BOM ให้ Notepad เปิดภาษาไทยถูก
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{RunId}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }


        // ======================================================
        // อ่าน Argument: --warehouse <รหัสคลัง>, --period <yyyyMM>
        // คืน false ถ้า --period รูปแบบไม่ถูกต้อง (ตรวจโดยลองแปลง yyyyMM + "01" เป็นวันที่)
        // ======================================================
        private static bool TryParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warehouse" && i + 1 < args.Length)
                {
                    // --warehouse <รหัสคลัง>: ประมวลผลเฉพาะคลังนี้
                    targetWarehouseCode = args[++i];
                }
                else if (args[i] == "--period" && i + 1 < args.Length)
                {
                    // --period <yyyyMM>: งวดสุดท้ายของช่วง 24 เดือน (ตรวจรูปแบบด้วยการลองแปลงเป็นวันที่)
                    targetPeriod = args[++i];
                    if (!DateTime.TryParseExact(targetPeriod + "01", "yyyyMMdd", null, DateTimeStyles.None, out _))
                    {
                        Log.Error("--period ต้องเป็นรูปแบบ yyyyMM เช่น 202609 (ได้รับ: {Period})", targetPeriod);
                        return false;
                    }
                }
                else
                {
                    // Argument ที่ไม่รู้จัก หรือ flag ที่ขาดค่าตามหลัง -> แจ้งวิธีใช้แล้วจบโปรแกรม
                    Log.Error("ไม่รู้จัก Argument: {Argument}", args[i]);
                    Log.Information("Usage: NetDemandPreprocessingEngine.exe --warehouse CMPMH --period 202609");
                    return false;
                }
            }
            return true;
        }


        // ======================================================
        // ฟังก์ชันสำหรับเชื่อมต่อฐานข้อมูลและดึงข้อมูล - config.xml
        // ======================================================
        static SqlConnection ConnectData()
        {
            // อ่านค่าการเชื่อมต่อจาก config.xml ที่อยู่ข้างไฟล์ exe
            string xmlPath = Path.Combine(AppContext.BaseDirectory, "config.xml");
            if (!File.Exists(xmlPath))
            {
                throw new FileNotFoundException($"ไม่พบไฟล์ config.xml ที่ {xmlPath}", xmlPath);
            }

            XDocument doc = XDocument.Load(xmlPath);   // XML ผิดรูปแบบ -> XmlException (มีเลขบรรทัด) ลอยขึ้นไปที่ Main

            // ตรวจค่าที่ต้องมีทั้ง 4 ตัวพร้อมกัน: ขาดหรือว่าง -> แจ้งชื่อที่ขาดทั้งหมดในครั้งเดียว (แทน NullReferenceException)
            string[] requiredKeys = { "Server", "Database", "UserId", "Password" };
            string[] missingKeys = requiredKeys.Where(k => string.IsNullOrWhiteSpace(doc.Root?.Element(k)?.Value)).ToArray();
            if (missingKeys.Length > 0)
            {
                throw new InvalidOperationException($"config.xml ไม่มีค่าหรือเป็นค่าว่าง: {string.Join(", ", missingKeys)}");
            }

            // ตรวจแล้วว่ามีค่าครบ จึงใช้ ! ได้อย่างปลอดภัย
            string server = doc.Root!.Element("Server")!.Value;
            string database = doc.Root!.Element("Database")!.Value;
            string userId = doc.Root!.Element("UserId")!.Value;
            string password = doc.Root!.Element("Password")!.Value;

            // log ก่อน Open: ถ้าต่อไม่ได้ จะรู้ว่าพยายามต่อที่ไหน (ห้ามใส่ password)
            Log.Information("เชื่อมต่อฐานข้อมูล Server={Server} Database={Database}", server, database);

            // สร้าง Connection String แล้วเปิดการเชื่อมต่อ (TrustServerCertificate=True: ข้ามการตรวจใบรับรองของ Server)
            string connectionString = $"Server={server};Database={database};User ID={userId};Password={password};TrustServerCertificate=True;";
            SqlConnection connection = new SqlConnection(connectionString);
            connection.Open();   // ต่อไม่ได้ -> SqlException ลอยขึ้นไปที่ Main (โปรแกรมจบ ไม่มีทรัพยากรค้าง)
            return connection;
        }
    }
}