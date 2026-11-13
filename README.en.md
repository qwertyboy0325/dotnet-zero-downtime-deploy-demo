# Load Balancing + Rolling Updates: Zero-Downtime Deployment for C# Apps

[繁體中文](README.md) | **English**

[![CI](https://github.com/qwertyboy0325/dotnet-zero-downtime-deploy-demo/actions/workflows/ci.yml/badge.svg)](https://github.com/qwertyboy0325/dotnet-zero-downtime-deploy-demo/actions/workflows/ci.yml)

## Why this project exists

I built this as a demo for someone I know who happened to ask: *"How do I deploy a C# app without downtime when the running process keeps its DLLs locked so I can't overwrite them?"* Rather than only explaining it in words, I made a small project you can actually run and watch.

So the goal of this repo is to **illustrate the idea**, not to be a production-ready framework. The code is deliberately minimal and easy to read; see the last section before taking it to production.

**TL;DR:** never overwrite a running DLL. Publish every version to its own folder, then swap instances one at a time behind a load balancer.

## Architecture

```
             ┌──────────────┐
  Clients ──▶│ LoadBalancer │  :8080  (YARP, plus /lb/drain and /lb/enable admin APIs)
             └──────┬───────┘
          ┌─────────┴─────────┐
          ▼                   ▼
   MyApp instance a :5001  MyApp instance b :5002
   releases/v1/MyApp.dll   releases/v1/MyApp.dll
```

## What's in the repo

| Path | Description |
|---|---|
| `src/MyApp` | The app being deployed. `/` returns its version and pid, `/health` is for health checks, `/admin/shutdown` triggers a graceful shutdown |
| `src/LoadBalancer` | A YARP-based load balancer with an admin API the deploy scripts use to take instances out of and back into rotation |
| `src/LoadTester` | Sends requests continuously and counts failures, to prove the deployment causes no interruption |
| `scripts/*.ps1` | Start / deploy / stop scripts for Windows |
| `scripts/*.sh` | The same scripts for macOS / Linux |
| `scripts/e2e.*` | End-to-end test: deploy, auto-rollback and manual rollback under load; passes only with 0 failures |
| `tests/MigrationSafety.Tests` | DB migration compatibility tests: expand/contract and the classic mistakes, taught through tests |
| `.github/workflows/` | `ci.yml` (runs e2e on every push), `deploy.yml` (deploys on tag push) |

## Running it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
.\scripts\start.ps1                    # publish v1, start two instances and the LB
dotnet run --project src\LoadTester    # in another window: generate load (Ctrl+C to stop)
.\scripts\deploy.ps1 v2                # rolling update to v2
.\scripts\deploy.ps1 v1                # roll back (releases\v1 already exists, no rebuild)
.\scripts\stop.ps1
```

On macOS / Linux, use the `.sh` scripts instead of `.ps1`.

## Deployment flow (the deploy script handles one instance at a time)

1. `dotnet publish` into a **new folder**, `releases/v2/`
2. `POST /lb/drain/a`: the LB stops sending new requests to a
3. Poll `GET /lb/inflight/a` until a has no requests in flight
4. `POST /admin/shutdown`: a shuts down gracefully (Kestrel finishes in-flight requests)
5. Start the new version from `releases/v2/MyApp.dll` on the same port and wait for `/health` to return 200
6. `POST /lb/enable/a` to put it back into rotation
7. Repeat for b

At least one instance is serving traffic at all times, so users never see an error.

## Why the DLL lock is never a problem

On Windows, a DLL loaded by a running process cannot be overwritten. This flow **never overwrites a running file**:

- The new version goes to `releases/v2/` while the old process keeps its lock on `releases/v1/`; they don't interfere.
- Once the old process exits, the lock on `releases/v1/` is released. Delete it, or keep it for rollbacks.

Counter-example: `dotnet publish -o <the folder that is running>` fails on Windows with *"The process cannot access the file ... because it is being used by another process"*. `e2e.ps1` demonstrates this error on Windows.

## Results

e2e on GitHub Actions (.NET 8, 20 concurrent workers; within 60 s: deploy v2 → deploy a broken build that gets auto-rolled back → roll back to v1):

| Platform | Succeeded | Failed | Responses by version |
|---|---|---|---|
| ubuntu-latest | 6863 | 0 | v1=4860, v2=2003 |
| windows-latest | 6583 | 0 | v1=4582, v2=2001 |

The broken build never served a single request.

## Full automation (GitHub Actions)

```
push / PR ──▶ CI (ci.yml): on both Linux and Windows
                 1. migration compatibility tests (see "Database migrations" below)
                 2. e2e under load: deploy v2 → deploy broken build (auto-rollback) → roll back to v1; must have 0 failures

git tag v1.2.0 && git push --tags
        └──▶ Deploy (deploy.yml): run CI first → manual approval (production environment)
                 → a self-hosted runner on the target host runs deploy.ps1
                 → if the new version fails its health check, that instance is rolled back and the workflow fails
```

**Auto-rollback:** when the new version's `/health` fails, the `deploy` script puts that instance back on the previous version, re-enables it in the LB and aborts (exit 1). The remaining instances are left untouched.

**Run e2e locally:**

```bash
./scripts/e2e.sh        # macOS / Linux
.\scripts\e2e.ps1       # Windows (also shows that a running DLL really is locked)
```

### Enabling automatic deployment (one-time setup)

1. Install a [self-hosted runner](https://docs.github.com/actions/hosting-your-own-runners) on the target Windows host, add the label `zero-downtime-demo`, and install the .NET 8 SDK.
2. Repo → Settings → Secrets and variables → Actions → **Variables**:
   - `DEPLOY_ENABLED` = `true` (if unset, the deploy job is skipped so tag pushes don't hang)
   - `DEPLOY_ROOT` = `D:\apps\zero-downtime-demo`. **It must be outside the runner workspace**, otherwise the next checkout would wipe the running `releases\`.
3. (Recommended) Settings → Environments: create `production` with required reviewers so each deployment needs approval.
4. From then on, pushing a tag deploys it. To roll back, go to Actions → Deploy → Run workflow and enter an older version (e.g. `v1.1.0`).

> The runner kills every process a job started when the job ends, so the workflow sets `RUNNER_TRACKING_ID` to an empty string to keep the instances alive. A sturdier production setup is to register the instances as Windows Services (e.g. with `sc.exe` or NSSM) and have the deploy script switch the service binPath.
>
> `deploy.yml` has not been run on a real self-hosted runner yet; CI verifies the same `deploy.ps1` it calls.

## Database migrations: learning compatibility through tests

You can swap app instances one at a time, but **there is only one database**. During a rolling update v1 and v2 hit the same DB, and after a rollback the old version runs against the new schema. So every migration must be **compatible with both the running version and the rollback target**.

`tests/MigrationSafety.Tests` shows this with tests you can run. The scenario is "rename `Customers.Name` to `FullName`":

- [`AppVersions.cs`](tests/MigrationSafety.Tests/AppVersions.cs): four app versions, V1–V4, each with its own EF Core model, just like different DLL versions running in production at the same time.
- [`Migrations.cs`](tests/MigrationSafety.Tests/Migrations.cs): the SQL for each step M1–M5, with the SQL Server equivalent in comments.
- [`RolloutSimulator.cs`](tests/MigrationSafety.Tests/RolloutSimulator.cs): executes a deployment plan step by step. After every migration or deploy it makes the running version and the rollback target actually read and write, and checks for empty values.

### The right way: expand / contract

| Step | What | Why it's safe |
|---|---|---|
| M2 | Add `FullName`, **nullable** | V1's INSERT doesn't know the column and still succeeds |
| V2 | Write both columns, still read `Name` | Rolling back to V1 still shows the data |
| M3 | Backfill old rows | Only after V1 (which writes `Name` only) is fully retired |
| V3 | Read `FullName`, still write both | Rolling back to V2 is fine |
| M4 | Make `Name` nullable | V4 stops writing `Name`; otherwise its INSERT fails |
| V4 | Write `FullName` only | — |
| M5 | DROP `Name` **last** | Only after the rollback window closes and V3 is no longer a rollback option |

### One lesson per test

| Test | Lesson |
|---|---|
| `Safe_expand_contract_plan_keeps_every_version_working` | The plan above has zero problems at every step |
| `Bad_renaming_column_in_one_step_breaks_the_running_version` | A direct `RENAME COLUMN` / `sp_rename` breaks the running V1 immediately |
| `Bad_expand_and_contract_in_the_same_release_breaks_the_running_version` | Adding and dropping in one release leaves no time to retire the old version |
| `Bad_switching_reads_without_backfill_loses_old_rows` | Forgot the backfill: **no error**, but old rows show empty names |
| `Bad_stopping_writes_while_old_column_is_still_NOT_NULL_breaks_inserts` | Stopped writing the old column before making it nullable |
| `Bad_skipping_the_read_switch_release_makes_rollback_lose_data` | Skipped the "read the new column" release; after rollback, data written by the new version is invisible |
| `Bad_dropping_column_inside_the_rollback_window_makes_rollback_impossible` | Dropped the column while a rollback was still possible; the rollback target breaks |
| `Bad_old_version_silently_reads_an_enum_value_it_does_not_know` | A new enum value: the old version **doesn't throw**, it silently takes the wrong code path |
| `Safe_ship_read_support_for_the_new_value_before_any_version_writes_it` | Enums need expand/contract too: ship a version that understands the value before any version writes it |

Tests starting with `Bad_` **expect a problem to be caught**. They pass when the mistake is detected.

```bash
dotnet test tests/MigrationSafety.Tests --logger "console;verbosity=detailed"
```

`detailed` prints each plan's steps and the problems found, for example:

```
 deploy   V4（V2 與 V4 同時在線）
   ✗ [部署 V4] 回滾目標 V2 讀到 1 筆空值（資料遺失或沒有 backfill）
```

(The test output is in Chinese: "V2 and V4 online at the same time" / "rollback target V2 read 1 empty value (data loss or missing backfill)".)

CI runs these tests on Linux and Windows on every push. If a migration is incompatible, the deployment e2e doesn't run.

### Applying this to a real project

Here all four versions live in one project for readability. A common real-world setup is:

1. CI checks out the code of the **previous release tag**
2. Applies **this PR's migrations** to a test DB
3. Runs the previous version's integration tests against it. A failure means this migration would break a rolling update or a rollback

A few things these tests can't show but matter just as much:

- **Locking:** SQLite doesn't reveal locking issues. On SQL Server / PostgreSQL, changing a column type or building an index without `ONLINE` can lock a large table; backfill in batches.
- **Don't run migrations at app startup** (`Database.Migrate()`). Multiple instances starting together will race. Run them as a separate pre-deploy step, e.g. `dotnet ef migrations script --idempotent` or a migrations bundle.
- **Permissions:** the app's DB account shouldn't have `ALTER` / `DROP` rights. Use a separate account for migrations, only in the CI/CD migration step.
- **Down migrations usually lose data**; in practice, prefer rolling forward (ship another fix).

## Before taking this to production

- **Use a real load balancer:** IIS ARR, Nginx, HAProxy, Azure Application Gateway or AWS ALB all support "drain → wait for connections to finish → re-enable". The flow is identical; only the API calls change.
- **Capacity:** while one instance is drained, the rest must handle all the traffic. If they can't, start the new version on a new port, add it to the LB, then drain the old one (one extra instance = blue-green deployment).
- **Compatibility:** old and new versions run side by side during a rolling update, so the DB schema and APIs must stay backward compatible (see "Database migrations" above).
- **Sessions:** don't keep sessions in process memory (use Redis or another distributed cache), or users get logged out during the switch.
- **Access control:** `/admin/shutdown` only accepts loopback calls; in production, also protect the LB's `/lb/*` endpoints with authentication or bind them to an internal network only.

## License

[MIT](LICENSE)
