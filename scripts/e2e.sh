#!/usr/bin/env bash
# 端對端驗證（CI 會跑）：壓測進行中依序
#   1. 部署 v2            → 應成功
#   2. 部署壞掉的 v3      → 應失敗並自動回滾，目前版本維持 v2
#   3. 回滾到 v1          → 應成功
# 全程壓測不可有任何失敗請求。
set -euo pipefail
cd "$(dirname "$0")/.."
export DEPLOY_ROOT=${DEPLOY_ROOT:-$PWD/.e2e}
export HEALTH_TIMEOUT_SEC=10
LB=http://127.0.0.1:8080
rm -rf "$DEPLOY_ROOT"
trap './scripts/stop.sh >/dev/null 2>&1 || true' EXIT

dotnet build src/LoadTester -c Release --nologo -v q >/dev/null
./scripts/start.sh v1

dotnet src/LoadTester/bin/Release/net8.0/LoadTester.dll "$LB/" 60 20 >"$DEPLOY_ROOT/logs/loadtest.log" 2>&1 &
LT=$!
sleep 3

echo "== 1. 部署 v2 =="
./scripts/deploy.sh v2

echo "== 2. 部署壞掉的版本（預期自動回滾）=="
if PUBLISH_ARGS="-p:SimulateBrokenHealth=true" ./scripts/deploy.sh v3-broken; then
  echo "FAIL: 壞掉的版本居然部署成功"; exit 1
fi
[[ $(cat "$DEPLOY_ROOT/run/current") == v2 ]] || { echo "FAIL: 目前版本應維持 v2"; exit 1; }
[[ $(curl -fsS "$LB/lb/status" | grep -o '"enabled"' | wc -l) -eq 2 ]] || { echo "FAIL: 兩台都應在 LB 上"; exit 1; }
[[ $(curl -fsS "$LB/") == v2* ]] || { echo "FAIL: 回滾後應由 v2 回應"; exit 1; }

echo "== 3. 回滾到 v1 =="
./scripts/deploy.sh v1

echo "== 等壓測結束 =="
rc=0; wait $LT || rc=$?
cat "$DEPLOY_ROOT/logs/loadtest.log" | tail -4
[[ $rc -eq 0 ]] && echo "PASS: 部署、自動回滾、手動回滾過程中 0 個失敗請求" || echo "FAIL: 壓測有失敗請求"
exit $rc
