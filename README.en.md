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
push / PR ──▶ CI (ci.yml): e2e on both Linux and Windows
                 under load: deploy v2 → deploy broken build (auto-rollback) → roll back to v1; must have 0 failures

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

## Before taking this to production

- **Use a real load balancer:** IIS ARR, Nginx, HAProxy, Azure Application Gateway or AWS ALB all support "drain → wait for connections to finish → re-enable". The flow is identical; only the API calls change.
- **Capacity:** while one instance is drained, the rest must handle all the traffic. If they can't, start the new version on a new port, add it to the LB, then drain the old one (one extra instance = blue-green deployment).
- **Compatibility:** old and new versions run side by side during a rolling update, so the DB schema and APIs must stay backward compatible (add columns first, remove them later).
- **Sessions:** don't keep sessions in process memory (use Redis or another distributed cache), or users get logged out during the switch.
- **Access control:** `/admin/shutdown` only accepts loopback calls; in production, also protect the LB's `/lb/*` endpoints with authentication or bind them to an internal network only.

## License

[MIT](LICENSE)
