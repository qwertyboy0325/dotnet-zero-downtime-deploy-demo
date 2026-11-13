using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MigrationSafety.Tests;

/// <summary>
/// 最危險的一類：不會報錯，資料卻被讀錯。
/// 新版本加了一個 enum 值並寫進 DB，舊版本（滾動更新中或回滾後）讀到不認得的值。
/// 這種問題「沒有例外」，所以只檢查「有沒有 throw」的測試抓不到，要檢查語意。
/// </summary>
public class EnumValueTests : IDisposable
{
    readonly SqliteConnection _conn = new("Data Source=:memory:");

    public EnumValueTests()
    {
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Orders (Id INTEGER PRIMARY KEY AUTOINCREMENT, Status INTEGER NOT NULL)";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void Bad_old_version_silently_reads_an_enum_value_it_does_not_know()
    {
        using (var v2 = OrdersDb<StatusV2>.Open(_conn))
        {
            v2.Orders.Add(new Order<StatusV2> { Status = StatusV2.Refunded });
            v2.SaveChanges();
        }

        using var v1 = OrdersDb<StatusV1>.Open(_conn);
        var status = v1.Orders.Single().Status;     // 不會 throw！

        Assert.False(Enum.IsDefined(status));         // 但拿到的是 (StatusV1)2，不是任何已知值
        Assert.Equal("未知狀態", DescribeV1(status));  // 舊版的 switch 只能落到 default，業務邏輯就錯了
    }

    [Fact]
    public void Safe_ship_read_support_for_the_new_value_before_any_version_writes_it()
    {
        // 正確做法也是 expand/contract：先部署「認得 Refunded 但不會寫它」的 V1.5，
        // 等所有實例都換成 V1.5，才部署會寫 Refunded 的 V2。這樣不管回滾到哪一版都讀得懂。
        using (var v2 = OrdersDb<StatusV2>.Open(_conn))
        {
            v2.Orders.Add(new Order<StatusV2> { Status = StatusV2.Refunded });
            v2.SaveChanges();
        }

        using var v15 = OrdersDb<StatusV1_5>.Open(_conn);
        Assert.Equal(StatusV1_5.Refunded, v15.Orders.Single().Status);
    }

    static string DescribeV1(StatusV1 s) => s switch
    {
        StatusV1.Pending => "待付款",
        StatusV1.Paid => "已付款",
        _ => "未知狀態",
    };
}

public enum StatusV1 { Pending = 0, Paid = 1 }
public enum StatusV1_5 { Pending = 0, Paid = 1, Refunded = 2 } // 只認得，不會寫
public enum StatusV2 { Pending = 0, Paid = 1, Refunded = 2 }

public class Order<TStatus> where TStatus : struct, Enum
{
    public int Id { get; set; }
    public TStatus Status { get; set; }
}

public class OrdersDb<TStatus>(DbContextOptions o) : DbContext(o) where TStatus : struct, Enum
{
    public DbSet<Order<TStatus>> Orders => Set<Order<TStatus>>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Order<TStatus>>().ToTable("Orders");

    public static OrdersDb<TStatus> Open(SqliteConnection conn) =>
        new(new DbContextOptionsBuilder<OrdersDb<TStatus>>().UseSqlite(conn).Options);
}
