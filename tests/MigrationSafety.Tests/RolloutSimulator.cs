using Microsoft.Data.Sqlite;

namespace MigrationSafety.Tests;

// 部署計畫的步驟
public abstract record Step;
/// <summary>跑一個 migration</summary>
public record Migrate(string Description, string Sql) : Step;
/// <summary>滾動部署到某個程式版本（部署期間新舊版本同時在線）</summary>
public record Deploy(AppVersion Version) : Step;
/// <summary>宣告不會再回滾到上一版（例如新版穩定跑了一週）</summary>
public record CloseRollbackWindow : Step;

/// <summary>
/// 模擬「滾動部署 + migration」的過程，檢查每一步之後：
///   - 正在線上的版本還能讀寫
///   - 可能被回滾回去的上一版也還能讀寫，而且讀得懂新版寫入的資料
///   - 讀出來的資料沒有空值（資料遺失 / 忘了 backfill）
/// 這就是 CI 裡「N-1 相容性測試」的核心概念。
/// </summary>
public class RolloutSimulator
{
    public List<string> Violations { get; } = [];
    public List<string> Log { get; } = [];

    public static RolloutSimulator Run(params Step[] plan)
    {
        var sim = new RolloutSimulator();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        AppVersion? running = null, rollbackTarget = null;
        foreach (var step in plan)
        {
            switch (step)
            {
                case Migrate m:
                    sim.Log.Add($"migrate  {m.Description}");
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = m.Sql;
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        sim.Violations.Add($"[{m.Description}] migration 本身失敗：{ex.Message}");
                        continue;
                    }
                    // schema 變了 → 線上版本和回滾目標都要還能用
                    sim.Check(conn, running, "線上版本", m.Description);
                    sim.Check(conn, rollbackTarget, "回滾目標", m.Description);
                    break;

                case Deploy d:
                    sim.Log.Add($"deploy   {d.Version}" + (running is null ? "" : $"（{running} 與 {d.Version} 同時在線）"));
                    // 新版能不能在目前的 schema 上跑
                    sim.Check(conn, d.Version, "新版本", $"部署 {d.Version}");
                    // 新版寫進去的資料，回滾後舊版還讀得懂嗎？
                    sim.Check(conn, running, "回滾目標", $"部署 {d.Version}");
                    rollbackTarget = running;
                    running = d.Version;
                    break;

                case CloseRollbackWindow:
                    sim.Log.Add($"close    不再回滾到 {rollbackTarget}");
                    rollbackTarget = null;
                    break;
            }
        }
        return sim;
    }

    void Check(SqliteConnection conn, AppVersion? v, string role, string after)
    {
        if (v is null) return;
        try
        {
            v.Insert(conn, $"user-by-{v}");
            var names = v.ReadNames(conn);
            var missing = names.Count(string.IsNullOrEmpty);
            if (missing > 0)
                Violations.Add($"[{after}] {role} {v} 讀到 {missing} 筆空值（資料遺失或沒有 backfill）");
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException().Message;
            Violations.Add($"[{after}] {role} {v} 壞掉：{root}");
        }
    }
}
