using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Serilog;

namespace DRP_Project.src.Ingestion;

// Connection String ของระบบต้นทาง 2 ตัว (สร้างด้วย ConnectionStringBuilder ทั้งคู่ เพราะรหัสผ่านที่มีอักขระพิเศษเช่น ' ; = จะพังถ้าต่อสตริงเอง)
public record IngestionSourceOptions(string SqlServerConnectionString, string MariaDbConnectionString);

// ======================================================================
// อ่านค่าการเชื่อมต่อระบบต้นทางจาก config.xml (ไฟล์เดียวกับ Target DB):
//   <SourceSql>     Server, Database, UserId, Password            (SQL Server: INVENTTABLE, SALESORDER, CUSTTABLE, SALESPFN, fashof_data.dbo.BRANCH)
//   <SourceMariaDb> Server, Port (ไม่ใส่ = 3306), Database, UserId, Password, SslMode (ไม่ใส่ = Preferred)   (MariaDB: md_wishlist, customers)
// รหัสผ่านแทนที่ได้ด้วย Environment Variable (ไม่ต้องเก็บใน config.xml): DRP_SRC_SQL_PASSWORD, DRP_SRC_MARIADB_PASSWORD
// ห้าม log รหัสผ่าน / connection string
// ======================================================================
public static class IngestionConfig
{
    public static IngestionSourceOptions LoadSourceOptions(string xmlPath)
    {
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException($"ไม่พบไฟล์ config.xml ที่ {xmlPath}", xmlPath);

        XDocument doc = XDocument.Load(xmlPath);
        XElement? sqlSection = doc.Root?.Element("SourceSql");
        XElement? mariaSection = doc.Root?.Element("SourceMariaDb");

        // รวบรวมค่าที่ขาดทั้งหมดแล้วแจ้งครั้งเดียว (เหมือน ConnectData ใน Program.cs)
        var missing = new List<string>();

        string sqlServer = Read(sqlSection, "SourceSql", "Server", missing);
        string sqlDatabase = Read(sqlSection, "SourceSql", "Database", missing);
        string sqlUser = Read(sqlSection, "SourceSql", "UserId", missing);
        string sqlPassword = ReadSecret("DRP_SRC_SQL_PASSWORD", sqlSection, "SourceSql", missing);

        string mariaServer = Read(mariaSection, "SourceMariaDb", "Server", missing);
        string mariaDatabase = Read(mariaSection, "SourceMariaDb", "Database", missing);
        string mariaUser = Read(mariaSection, "SourceMariaDb", "UserId", missing);
        string mariaPassword = ReadSecret("DRP_SRC_MARIADB_PASSWORD", mariaSection, "SourceMariaDb", missing);

        if (missing.Count > 0)
            throw new InvalidOperationException($"config.xml ไม่มีค่าหรือเป็นค่าว่าง: {string.Join(", ", missing)}");

        // พอร์ต MariaDB (ค่าเริ่มต้น 3306) และ SslMode (ค่าเริ่มต้น Preferred; แนะนำ Required เมื่อต่อผ่านเครือข่ายสาธารณะ)
        uint mariaPort = 3306;
        string portText = mariaSection?.Element("Port")?.Value?.Trim() ?? "";
        if (portText.Length > 0 && !uint.TryParse(portText, out mariaPort))
            throw new InvalidOperationException($"config.xml: SourceMariaDb/Port ต้องเป็นตัวเลข (ได้รับ: {portText})");

        MySqlSslMode sslMode = MySqlSslMode.Preferred;
        string sslText = mariaSection?.Element("SslMode")?.Value?.Trim() ?? "";
        if (sslText.Length > 0 && !Enum.TryParse(sslText, ignoreCase: true, out sslMode))
            throw new InvalidOperationException($"config.xml: SourceMariaDb/SslMode ไม่ถูกต้อง (ได้รับ: {sslText})");

        // log เฉพาะปลายทาง (ห้ามใส่ password)
        Log.Information("Ingestion Source: SQL Server={SqlServer} Database={SqlDatabase} | MariaDB={MariaServer}:{MariaPort} Database={MariaDatabase}",
            sqlServer, sqlDatabase, mariaServer, mariaPort, mariaDatabase);

        var sqlBuilder = new SqlConnectionStringBuilder
        {
            DataSource = sqlServer,                 // รูปแบบ host,port เช่น 192.168.2.103,5879
            InitialCatalog = sqlDatabase,
            UserID = sqlUser,
            Password = sqlPassword,
            TrustServerCertificate = true,          // เหมือน ConnectData: ข้ามการตรวจใบรับรองของ Server
            ConnectTimeout = 30
        };

        var mariaBuilder = new MySqlConnectionStringBuilder
        {
            Server = mariaServer,
            Port = mariaPort,
            Database = mariaDatabase,
            UserID = mariaUser,
            Password = mariaPassword,
            SslMode = sslMode,
            ConnectionTimeout = 30
        };

        return new IngestionSourceOptions(sqlBuilder.ConnectionString, mariaBuilder.ConnectionString);
    }

    // อ่านค่าใน <section>/<key> | ขาดหรือว่าง -> บันทึกชื่อที่ขาดใน missing แล้วคืน ""
    private static string Read(XElement? section, string sectionName, string key, List<string> missing)
    {
        string value = section?.Element(key)?.Value?.Trim() ?? "";
        if (value.Length == 0) missing.Add($"{sectionName}/{key}");
        return value;
    }

    // รหัสผ่าน: Environment Variable มาก่อน (ถ้าตั้งไว้) มิฉะนั้นอ่านจาก config.xml
    private static string ReadSecret(string environmentVariable, XElement? section, string sectionName, List<string> missing)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;

        string value = section?.Element("Password")?.Value ?? "";   // ไม่ Trim: รหัสผ่านอาจมีช่องว่างจริง
        if (value.Length == 0) missing.Add($"{sectionName}/Password (หรือ Environment Variable {environmentVariable})");
        return value;
    }
}

// ======================================================================
// STEP 0 ของ Program.cs: ต่อ Source จาก config.xml แล้วรัน DemandIngestionEngine บน Target Connection เดียวกับ pipeline
// Program.cs ควบคุมลำดับเท่านั้น: กฎธุรกิจอยู่ใน DemandIngestionEngine | งาน SQL อยู่ใน SqlDrpIngestionRepository
// ======================================================================
public static class IngestionRunner
{
    public static void Run(SqlConnection targetConnection, string mode, string? period, string? warehouseCode)
    {
        string xmlPath = Path.Combine(AppContext.BaseDirectory, "config.xml");
        IngestionSourceOptions options = IngestionConfig.LoadSourceOptions(xmlPath);

        var repository = new SqlDrpIngestionRepository(targetConnection, options);
        var engine = new DemandIngestionEngine(repository);

        // Mode / period / warehouse ตรวจรูปแบบที่ Engine (ArgumentException ก่อนอ่านหรือเขียนข้อมูลใดๆ)
        engine.ExecuteIngestion(new IngestionJobRequest(mode, period, warehouseCode));
    }
}
