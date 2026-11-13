namespace MigrationSafety.Tests;

// 每個 migration 就是一段 SQL。註解附上 SQL Server 的寫法，方便對照。
public static class Migrations
{
    public static readonly Migrate M1_Initial = new("M1 建表",
        "CREATE TABLE Customers (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)");

    /// <summary>Expand：新增欄位一定要允許 NULL（或有預設值），舊版 INSERT 才不會失敗</summary>
    public static readonly Migrate M2_AddFullName = new("M2 新增 FullName (NULL)",
        "ALTER TABLE Customers ADD COLUMN FullName TEXT NULL");

    /// <summary>Backfill：舊資料補到新欄位。正式環境要分批做，避免長交易鎖表</summary>
    public static readonly Migrate M3_Backfill = new("M3 回填 FullName",
        "UPDATE Customers SET FullName = Name WHERE FullName IS NULL");

    /// <summary>
    /// 放寬舊欄位：V4 不再寫 Name，所以 Name 必須先允許 NULL。
    /// SQL Server 只要一行：ALTER TABLE Customers ALTER COLUMN Name nvarchar(200) NULL
    /// SQLite 不支援改 nullability，只能重建表。
    /// </summary>
    public static readonly Migrate M4_RelaxName = new("M4 Name 改成允許 NULL",
        """
        CREATE TABLE Customers_new (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NULL, FullName TEXT NULL);
        INSERT INTO Customers_new (Id, Name, FullName) SELECT Id, Name, FullName FROM Customers;
        DROP TABLE Customers;
        ALTER TABLE Customers_new RENAME TO Customers;
        """);

    /// <summary>Contract：確定沒有任何版本（包含回滾目標）還在用 Name，才能刪</summary>
    public static readonly Migrate M5_DropName = new("M5 刪除 Name",
        "ALTER TABLE Customers DROP COLUMN Name");

    // ---- 反例 ----

    /// <summary>直接改名（SQL Server：sp_rename）。一步到位，舊版當場壞掉</summary>
    public static readonly Migrate Bad_RenameColumn = new("直接 RENAME COLUMN",
        "ALTER TABLE Customers RENAME COLUMN Name TO FullName");
}
