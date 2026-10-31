# 負載平衡 + 滾動更新：不停站部署 C# 程式的示範

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
| `.github/workflows/` | `ci.yml`（每次 push 跑 e2e）、`deploy.yml`（推 tag 自動部署） |

## 執行方式（Windows）

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

反例：如果直接 `dotnet publish -o 正在執行的目錄`，Windows 會報 *"The process cannot access the file ... because it is being used by another process"*。

## 實測結果（macOS，.NET 8，20 個併發）

```
v1 → v2 → 回滾 v1，總共 30 秒：成功 3381，失敗 0，版本分佈 v1=2776, v2=605
```

## 完全自動化（GitHub Actions）

```
push / PR ──▶ CI (ci.yml)：Linux + Windows 各跑一次 e2e
                 壓測中：部署 v2 → 部署壞版本（應自動回滾）→ 回滾 v1，0 失敗才通過

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

## 搬到正式環境時要注意

- **LB 換成現成產品**：IIS ARR、Nginx、HAProxy、Azure Application Gateway / AWS ALB 都有「摘除 → 等連線處理完 → 加回」的功能，部署流程完全相同，只是 API 換成它們的。
- **容量**：一次摘除一台時，剩下的機器要扛得住全部流量。如果扛不住，可以改成「先在新的 port 起新版，加入 LB 之後再摘除舊版」（多一台就是藍綠部署）。
- **相容性**：滾動更新期間，新舊版本會同時在線，DB schema 和 API 都要向下相容（先加欄位、後刪欄位）。
- **Session**：不要把 session 存在程序記憶體裡（改用 Redis 或分散式快取），否則切換時使用者會被登出。
- **權限**：`/admin/shutdown` 只允許 loopback 呼叫，LB 的 `/lb/*` 在正式環境也要加上驗證，或只綁定內網。
