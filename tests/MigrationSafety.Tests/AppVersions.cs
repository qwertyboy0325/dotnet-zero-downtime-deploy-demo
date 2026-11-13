using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MigrationSafety.Tests;

// 情境：要把 Customers.Name 改名成 FullName。
// 每個「程式版本」都有自己的 EF Core model，就像線上同時跑著不同版本的 DLL。

public abstract class AppVersion(string name)
{
    public string Name { get; } = name;

    // 模擬這個版本在線上會做的事：寫入一筆、讀出全部名字
    public abstract void Insert(SqliteConnection conn, string customerName);
    public abstract IReadOnlyList<string?> ReadNames(SqliteConnection conn);

    public override string ToString() => Name;

    protected static TDb Open<TDb>(SqliteConnection conn) where TDb : DbContext =>
        (TDb)Activator.CreateInstance(typeof(TDb), new DbContextOptionsBuilder<TDb>().UseSqlite(conn).Options)!;
}

public static class Versions
{
    /// <summary>原始版本：只認得 Name</summary>
    public static readonly AppVersion V1 = new V1App();
    /// <summary>Expand：兩個欄位都寫，仍讀 Name</summary>
    public static readonly AppVersion V2 = new V2App(readFullName: false, "V2");
    /// <summary>切換讀取：兩個欄位都寫，改讀 FullName</summary>
    public static readonly AppVersion V3 = new V2App(readFullName: true, "V3");
    /// <summary>停寫舊欄位：model 裡已經沒有 Name</summary>
    public static readonly AppVersion V4 = new V4App();
}

// ---- V1 ----
public class CustomerV1
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class DbV1(DbContextOptions<DbV1> o) : DbContext(o)
{
    public DbSet<CustomerV1> Customers => Set<CustomerV1>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<CustomerV1>().ToTable("Customers");
}

class V1App() : AppVersion("V1")
{
    public override void Insert(SqliteConnection conn, string n)
    {
        using var db = Open<DbV1>(conn);
        db.Customers.Add(new CustomerV1 { Name = n });
        db.SaveChanges();
    }

    public override IReadOnlyList<string?> ReadNames(SqliteConnection conn)
    {
        using var db = Open<DbV1>(conn);
        return db.Customers.Select(c => (string?)c.Name).ToList();
    }
}

// ---- V2 / V3：同一個 model，差在讀哪個欄位 ----
public class CustomerV2
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? FullName { get; set; }
}

public class DbV2(DbContextOptions<DbV2> o) : DbContext(o)
{
    public DbSet<CustomerV2> Customers => Set<CustomerV2>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<CustomerV2>().ToTable("Customers");
}

class V2App(bool readFullName, string name) : AppVersion(name)
{
    public override void Insert(SqliteConnection conn, string n)
    {
        using var db = Open<DbV2>(conn);
        db.Customers.Add(new CustomerV2 { Name = n, FullName = n }); // 雙寫
        db.SaveChanges();
    }

    public override IReadOnlyList<string?> ReadNames(SqliteConnection conn)
    {
        using var db = Open<DbV2>(conn);
        return readFullName
            ? db.Customers.Select(c => c.FullName).ToList()
            : db.Customers.Select(c => c.Name).ToList();
    }
}

// ---- V4 ----
public class CustomerV4
{
    public int Id { get; set; }
    public string? FullName { get; set; }
}

public class DbV4(DbContextOptions<DbV4> o) : DbContext(o)
{
    public DbSet<CustomerV4> Customers => Set<CustomerV4>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<CustomerV4>().ToTable("Customers");
}

class V4App() : AppVersion("V4")
{
    public override void Insert(SqliteConnection conn, string n)
    {
        using var db = Open<DbV4>(conn);
        db.Customers.Add(new CustomerV4 { FullName = n });
        db.SaveChanges();
    }

    public override IReadOnlyList<string?> ReadNames(SqliteConnection conn)
    {
        using var db = Open<DbV4>(conn);
        return db.Customers.Select(c => c.FullName).ToList();
    }
}
