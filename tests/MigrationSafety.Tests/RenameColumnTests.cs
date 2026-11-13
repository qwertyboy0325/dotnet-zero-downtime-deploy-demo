using Xunit.Abstractions;
using static MigrationSafety.Tests.Migrations;
using static MigrationSafety.Tests.Versions;

namespace MigrationSafety.Tests;

/// <summary>
/// 把 Customers.Name 改名成 FullName：一個正確的 expand/contract 計畫，和六種經典錯誤。
///
/// 反例測試是「預期會抓到問題」的測試：它們通過，代表模擬器確實攔得住這種錯誤。
/// 換到真實專案，你會把自己的部署計畫丟進同樣的檢查，期待的是 0 個 violation。
/// </summary>
public class RenameColumnTests(ITestOutputHelper output)
{
    // 正確做法：拆成 5 個 migration、4 次部署，每一步都相容前後版本
    static readonly Step[] SafePlan =
    [
        M1_Initial, new Deploy(V1),
        M2_AddFullName,          // expand：新欄位允許 NULL，V1 不受影響
        new Deploy(V2),          // 雙寫，仍讀 Name
        new CloseRollbackWindow(), // V1 已全部下線，不會再有只寫 Name 的程式
        M3_Backfill,             // 舊資料補到 FullName
        new Deploy(V3),          // 改讀 FullName，仍雙寫 → 回滾到 V2 也沒問題
        M4_RelaxName,            // Name 改成允許 NULL，V4 才能不寫它
        new Deploy(V4),          // 停寫 Name
        new CloseRollbackWindow(), // 確定不會再回到 V3
        M5_DropName,             // contract：最後才刪
    ];

    [Fact]
    public void Safe_expand_contract_plan_keeps_every_version_working()
    {
        var sim = RolloutSimulator.Run(SafePlan);
        sim.Log.ForEach(output.WriteLine);
        Assert.Empty(sim.Violations);
    }

    [Fact]
    public void Bad_renaming_column_in_one_step_breaks_the_running_version()
    {
        var sim = RolloutSimulator.Run(M1_Initial, new Deploy(V1), Bad_RenameColumn, new Deploy(V4));
        AssertCaught(sim, "[直接 RENAME COLUMN] 線上版本 V1 壞掉");
    }

    [Fact]
    public void Bad_expand_and_contract_in_the_same_release_breaks_the_running_version()
    {
        var sim = RolloutSimulator.Run(M1_Initial, new Deploy(V1), M2_AddFullName, M3_Backfill, M5_DropName);
        AssertCaught(sim, "[M5 刪除 Name] 線上版本 V1 壞掉");
    }

    [Fact]
    public void Bad_switching_reads_without_backfill_loses_old_rows()
    {
        // 不會報錯，但舊資料的 FullName 是 NULL → 畫面上的名字消失
        var sim = RolloutSimulator.Run(M1_Initial, new Deploy(V1), M2_AddFullName, new Deploy(V2),
            new CloseRollbackWindow(), new Deploy(V3));
        AssertCaught(sim, "[部署 V3] 新版本 V3 讀到");
    }

    [Fact]
    public void Bad_stopping_writes_while_old_column_is_still_NOT_NULL_breaks_inserts()
    {
        var sim = RolloutSimulator.Run(M1_Initial, new Deploy(V1), M2_AddFullName, new Deploy(V2),
            new CloseRollbackWindow(), M3_Backfill, new Deploy(V3), new Deploy(V4));
        AssertCaught(sim, "[部署 V4] 新版本 V4 壞掉", "NOT NULL");
    }

    [Fact]
    public void Bad_skipping_the_read_switch_release_makes_rollback_lose_data()
    {
        // V2 → V4 直接跳：V4 只寫 FullName，回滾到還在讀 Name 的 V2 就看不到新資料
        var sim = RolloutSimulator.Run(M1_Initial, new Deploy(V1), M2_AddFullName, new Deploy(V2),
            new CloseRollbackWindow(), M3_Backfill, M4_RelaxName, new Deploy(V4));
        AssertCaught(sim, "[部署 V4] 回滾目標 V2 讀到");
    }

    [Fact]
    public void Bad_dropping_column_inside_the_rollback_window_makes_rollback_impossible()
    {
        // 跟 SafePlan 一樣，只差最後沒有先關閉回滾窗口
        var plan = SafePlan.Where((s, i) => !(s is CloseRollbackWindow && i == SafePlan.Length - 2)).ToArray();
        var sim = RolloutSimulator.Run(plan);
        AssertCaught(sim, "[M5 刪除 Name] 回滾目標 V3 壞掉");
    }

    void AssertCaught(RolloutSimulator sim, params string[] expected)
    {
        sim.Log.ForEach(output.WriteLine);
        sim.Violations.ForEach(v => output.WriteLine("  ✗ " + v));
        Assert.Contains(sim.Violations, v => expected.All(v.Contains));
    }
}
