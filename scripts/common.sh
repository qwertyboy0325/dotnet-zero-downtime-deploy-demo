# 共用設定與函式（被 start.sh / deploy.sh / stop.sh source）
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
SRC=$PWD
# 部署產物（releases/run/logs）放的位置；CI/CD 時要放在原始碼工作區外面，避免 checkout 清掉執行中的版本
D=${DEPLOY_ROOT:-$SRC}
LB=http://127.0.0.1:8080
SLOTS=("a:5001" "b:5002")
HEALTH_TIMEOUT_SEC=${HEALTH_TIMEOUT_SEC:-30}
mkdir -p "$D/run" "$D/logs" "$D/releases"

log() { echo "[$(date +%H:%M:%S)] $*"; }

# 每個版本發佈到自己的目錄 → 永遠不覆寫正在被載入的 DLL
# PUBLISH_ARGS 可帶額外的 publish 參數（e2e 用它做出一個壞掉的版本）
publish_app() {
  local v=$1
  if [[ -d $D/releases/$v ]]; then log "releases/$v 已存在，直接使用（回滾也走這條）"; return; fi
  log "發佈 $v → releases/$v"
  # shellcheck disable=SC2086
  dotnet publish "$SRC/src/MyApp" -c Release -o "$D/releases/$v" -p:InformationalVersion="$v" ${PUBLISH_ARGS:-} --nologo -v q >/dev/null
}

wait_healthy() {
  local port=$1
  for _ in $(seq 1 $((HEALTH_TIMEOUT_SEC * 2))); do
    curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  log "port $port ${HEALTH_TIMEOUT_SEC} 秒內沒有變健康"; return 1
}

start_instance() {
  local slot=$1 port=$2 v=$3
  nohup dotnet "$D/releases/$v/MyApp.dll" --urls "http://127.0.0.1:$port" >"$D/logs/$slot.log" 2>&1 &
  echo $! >"$D/run/$slot.pid"
  wait_healthy "$port" || return 1
  log "[$slot] $v 已啟動 (pid $(cat "$D/run/$slot.pid"), port $port)"
}

stop_instance() {
  local slot=$1 port=$2
  [[ -f $D/run/$slot.pid ]] || return 0
  local pid; pid=$(cat "$D/run/$slot.pid")
  curl -fsS -X POST "http://127.0.0.1:$port/admin/shutdown" >/dev/null 2>&1 || true
  for _ in $(seq 1 70); do kill -0 "$pid" 2>/dev/null || { rm -f "$D/run/$slot.pid"; return 0; }; sleep 0.5; done
  log "[$slot] 優雅關機逾時，強制結束"; kill -9 "$pid" 2>/dev/null || true; rm -f "$D/run/$slot.pid"
}
