# 負載平衡 + 滾動更新：不停站部署 C# 程式的示範

**繁體中文** | [English](README.en.md)

[![CI](https://github.com/qwertyboy0325/dotnet-zero-downtime-deploy-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/qwertyboy0325/dotnet-zero-downtime-deploy-demo/actions/workflows/ci.yml)

## 為什麼有這個專案

這是為了一位認識的朋友做的示範。他剛好在問：「C# 要不停站發佈，但 DLL 被正在執行的程式鎖住、沒辦法覆蓋，該怎麼辦？」與其只用文字解釋，不如做一個能實際跑起來的小專案，直接看到整個過程。

所以這個 repo 的目的是**說明觀念**，不是拿來直接上線的框架。程式刻意寫得精簡，方便閱讀；要搬到正式環境，請看最後一節的注意事項。

**一句話結論**：不要覆蓋正在執行的 DLL。每個版本發佈到自己的目錄，再透過負載平衡器一台一台換過去。

## 架構

```
             ┌──────────────┐
  使用者 ──▶ │ LoadBalancer │  :8080  (YARP，含 /lb/drain、/lb/enable 管理 API)
             └──────┬───────┘
          ┌─────────┴─────────┐
          ▼                   ▼
   MyApp 實例 a :5001    MyApp 實例 b :5002
   releases/v1/MyApp.dll releases/v1/MyApp.dll
```

## 專案內容

| 路徑 | 說明 |
|---|---|
| `src/MyApp` | 被部署的程式。`/` 回傳版本與 pid，`/health` 供健康檢查用，`/admin/shutdown` 觸發優雅關機 |
| `src/LoadBalancer` | 用 YARP 寫的負載平衡器，提供 API 讓部署腳本摘除或加回實例 |
| `src/LoadTester` | 壓測工具，持續送請求並統計失敗數，用來證明部署過程沒有中斷 |
| `scripts/*.ps1` | Windows 用的啟動 / 部署 / 停止腳本 |
| `scripts/*.sh` | macOS / Linux 用的同一套腳本 |
| `scripts/e2e.*` | 端對端測試：壓測中部署、自動回滾、手動回滾，0 失敗才算通過 |
| `tests/MigrationSafety.Tests` | DB migration 相容性測試：用測試示範 expand/contract 和經典錯誤 |
| `.github/workflows/` | `ci.yml`（每次 push 跑 e2e）、`deploy.yml`（推 tag 自動部署） |

## 執行方式

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
.\scripts\start.ps1                    # 發佈 v1，起兩個實例和 LB
dotnet run --project src\LoadTester    # 另開一個視窗持續壓測（Ctrl+C 結束）
.\scripts\deploy.ps1 v2                # 滾動更新到 v2
.\scripts\deploy.ps1 v1                # 回滾（releases\v1 已存在，不必重新編譯）
.\scripts\stop.ps1
```

macOS / Linux 的用法相同，把 `.ps1` 換成 `.sh` 即可。

## 部署流程（deploy 腳本一次只處理一台）

1. `dotnet publish` 輸出到 **新的目錄** `releases/v2/`
2. `POST /lb/drain/a`：LB 不再把新請求分給 a
3. 輪詢 `GET /lb/inflight/a`，等 a 手上的請求數歸零
4. `POST /admin/shutdown`：a 優雅關機（Kestrel 會等進行中的請求做完）
5. 用 `releases/v2/MyApp.dll` 在原本的 port 啟動新版，等 `/health` 回 200
6. `POST /lb/enable/a`，加回 LB
7. 對 b 重複同樣的步驟

整個過程中始終至少有一台在接流量，所以使用者不會遇到錯誤。

## 為什麼不會遇到 DLL 被鎖住

Windows 上正在被載入的 DLL 不能覆寫。這套流程**從來不覆蓋正在執行的檔案**：

- 新版發佈到 `releases/v2/`，舊版程序繼續鎖著 `releases/v1/`，兩者互不干擾。
- 舊程序結束之後，`releases/v1/` 的鎖自然解除，可以清掉，也可以保留作為回滾用。

反例：如果直接 `dotnet publish -o 正在執行的目錄`，Windows 會報 *"The process cannot access the file ... because it is being used by another process"*。`e2e.ps1` 會在 Windows 上實際示範這個錯誤。

## 實測結果

GitHub Actions 上的 e2e（.NET 8，20 個併發，60 秒內依序部署 v2 → 部署壞版本並自動回滾 → 回滾 v1）：

| 平台 | 成功 | 失敗 | 版本分佈 |
|---|---|---|---|
| ubuntu-latest | 6863 | 0 | v1=4860, v2=2003 |
| windows-latest | 6583 | 0 | v1=4582, v2=2001 |

壞掉的版本一個請求都沒有接到。

## 完全自動化（GitHub Actions）

```
push / PR ──▶ CI (ci.yml)：Linux + Windows 各跑一次
                 1. migration 相容性測試（見下方「資料庫 Migration」）
                 2. e2e，壓測中：部署 v2 → 部署壞版本（應自動回滾）→ 回滾 v1，0 失敗才通過

git tag v1.2.0 && git push --tags
        └──▶ Deploy (deploy.yml)：先跑 CI → 人工核准（production environment）
                 → 目標主機上的 self-hosted runner 執行 deploy.ps1 滾動更新
                 → 新版健康檢查失敗就自動回滾該台，workflow 標示失敗
```

**自動回滾**：`deploy` 腳本在新版 `/health` 失敗時，會把那一台換回原本的版本，加回 LB 並中止部署（exit 1）。後面的機器不會再被更新。

**本機跑 e2e**：

```bash
./scripts/e2e.sh        # macOS / Linux
.\scripts\e2e.ps1       # Windows（另外會示範執行中的 DLL 確實被鎖住、無法覆寫）
```

### 啟用自動部署（一次性設定）

1. 在目標 Windows 主機安裝 [self-hosted runner](https://docs.github.com/actions/hosting-your-own-runners)，加上 label `zero-downtime-demo`，並裝好 .NET 8 SDK。
2. Repo → Settings → Secrets and variables → Actions → **Variables**：
   - `DEPLOY_ENABLED` = `true`（沒設的話 deploy job 會直接跳過，tag push 不會卡住）
   - `DEPLOY_ROOT` = `D:\apps\zero-downtime-demo`。**必須放在 runner 工作區外面**，否則下次 checkout 會清掉正在執行的 `releases\`。
3. （建議）Settings → Environments 建立 `production`，設定 required reviewers，部署前要有人核准。
4. 之後只要推 tag 就會部署；要回滾就到 Actions → Deploy → Run workflow，填舊版本號（例如 `v1.1.0`）。

> runner 在 job 結束時會殺掉該 job 啟動的程序，所以 workflow 把 `RUNNER_TRACKING_ID` 設成空字串，讓實例繼續存活。正式環境更穩的做法是把實例註冊成 Windows Service（例如用 `sc.exe` 或 NSSM），讓 deploy 腳本改成切換 service 的 binPath。
>
> `deploy.yml` 目前沒有在真正的 self-hosted runner 上跑過；CI 驗證的是它呼叫的同一套 `deploy.ps1`。

## 資料庫 Migration：用測試學相容性

程式可以一台一台換，**資料庫卻只有一份**。滾動更新期間 v1 和 v2 同時連同一個 DB，回滾時舊版也要跑在新 schema 上。所以每個 migration 都必須**同時相容線上版本和回滾目標**。

`tests/MigrationSafety.Tests` 用可以實際執行的測試來說明這件事。情境是「把 `Customers.Name` 改名成 `FullName`」：

- [`AppVersions.cs`](tests/MigrationSafety.Tests/AppVersions.cs)：V1～V4 四個程式版本，各自有自己的 EF Core model，就像線上同時跑著不同版本的 DLL。
- [`Migrations.cs`](tests/MigrationSafety.Tests/Migrations.cs)：M1～M5 每一步的 SQL，附上 SQL Server 的對照寫法。
- [`RolloutSimulator.cs`](tests/MigrationSafety.Tests/RolloutSimulator.cs)：照部署計畫一步一步執行。每跑完一個 migration 或部署，就讓「線上版本」和「回滾目標」實際讀寫一次，並檢查有沒有讀到空值。

### 正確做法：Expand / Contract

| 步驟 | 內容 | 為什麼安全 |
|---|---|---|
| M2 | 新增 `FullName`，**允許 NULL** | V1 的 INSERT 不知道這個欄位，也不會失敗 |
| V2 | 兩個欄位都寫，仍讀 `Name` | 回滾到 V1 也看得到資料 |
| M3 | 回填舊資料 | 要在只寫 `Name` 的 V1 全部下線後才做 |
| V3 | 改讀 `FullName`，仍然雙寫 | 回滾到 V2 也沒問題 |
| M4 | `Name` 改成允許 NULL | V4 不寫 `Name` 了，不放寬的話 INSERT 會失敗 |
| V4 | 只寫 `FullName` | — |
| M5 | **最後才** DROP `Name` | 要等回滾窗口過了，確定不會再回到 V3 |

### 每個測試教一件事

| 測試 | 學到什麼 |
|---|---|
| `Safe_expand_contract_plan_keeps_every_version_working` | 上面的計畫，每一步 0 個問題 |
| `Bad_renaming_column_in_one_step_breaks_the_running_version` | 直接 `RENAME COLUMN` / `sp_rename`，線上的 V1 當場壞掉 |
| `Bad_expand_and_contract_in_the_same_release_breaks_the_running_version` | 新增和刪除放在同一次發佈，舊版來不及下線 |
| `Bad_switching_reads_without_backfill_loses_old_rows` | 忘了回填：**不會報錯**，但舊資料的名字變成空的 |
| `Bad_stopping_writes_while_old_column_is_still_NOT_NULL_breaks_inserts` | 停寫舊欄位之前，沒先把它改成允許 NULL |
| `Bad_skipping_the_read_switch_release_makes_rollback_lose_data` | 跳過「改讀新欄位」那一版，回滾後看不到新版寫入的資料 |
| `Bad_dropping_column_inside_the_rollback_window_makes_rollback_impossible` | 還可能回滾時就 DROP，回滾目標直接壞掉 |
| `Bad_old_version_silently_reads_an_enum_value_it_does_not_know` | 新增 enum 值：舊版**不會 throw**，卻默默走錯邏輯 |
| `Safe_ship_read_support_for_the_new_value_before_any_version_writes_it` | enum 也要 expand/contract：先發「認得但不寫」的版本 |

`Bad_` 開頭的測試是「**預期會抓到問題**」的測試。它們通過，代表這種錯誤確實攔得住。

```bash
dotnet test tests/MigrationSafety.Tests --logger "console;verbosity=detailed"
```

`detailed` 會印出每個計畫的步驟和抓到的問題，例如：

```
 deploy   V4（V2 與 V4 同時在線）
   ✗ [部署 V4] 回滾目標 V2 讀到 1 筆空值（資料遺失或沒有 backfill）
```

CI 每次 push 都會在 Linux 和 Windows 上跑這組測試。migration 不相容的話，就不會繼續跑部署的 e2e。

### 套用到真實專案

這裡為了好讀，把四個版本放在同一個專案裡。真實專案常見的做法是：

1. CI checkout **上一個 release tag** 的程式
2. 對測試 DB 套用**這次 PR 的 migration**
3. 用上一版的整合測試跑一次。失敗就代表這個 migration 會讓滾動更新或回滾出事

另外幾點這組測試沒辦法示範，但同樣重要：

- **鎖表**：SQLite 看不出鎖定問題。在 SQL Server / PostgreSQL 上，改型別或建索引沒加 `ONLINE` 會鎖住大表；回填要分批做。
- **不要在程式啟動時跑 migration**（`Database.Migrate()`）。多台同時啟動會搶著跑。改成部署前的獨立步驟，例如 `dotnet ef migrations script --idempotent` 或 migrations bundle。
- **權限**：程式的 DB 帳號不該有 `ALTER` / `DROP` 權限。migration 用另一個帳號，只在 CI/CD 的 migration 步驟使用。
- **Down migration 通常會丟資料**，實務上多半採用 roll forward（再發一版修正）。

## 搬到正式環境時要注意

- **LB 換成現成產品**：IIS ARR、Nginx、HAProxy、Azure Application Gateway / AWS ALB 都有「摘除 → 等連線處理完 → 加回」的功能，部署流程完全相同，只是 API 換成它們的。
- **容量**：一次摘除一台時，剩下的機器要扛得住全部流量。如果扛不住，可以改成「先在新的 port 起新版，加入 LB 之後再摘除舊版」（多一台就是藍綠部署）。
- **相容性**：滾動更新期間，新舊版本會同時在線，DB schema 和 API 都要向下相容（見上方「資料庫 Migration」）。
- **Session**：不要把 session 存在程序記憶體裡（改用 Redis 或分散式快取），否則切換時使用者會被登出。
- **權限**：`/admin/shutdown` 只允許 loopback 呼叫，LB 的 `/lb/*` 在正式環境也要加上驗證，或只綁定內網。

## 授權

[MIT](LICENSE)
