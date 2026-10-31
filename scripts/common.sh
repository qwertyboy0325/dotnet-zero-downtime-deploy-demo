# 共用設定與函式（被 start.sh / deploy.sh / stop.sh source）
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
LB=http://127.0.0.1:8080
SLOTS=("a:5001" "b:5002")
mkdir -p run logs releases

log() { echo "[$(date +%H:%M:%S)] $*"; }

# 每個版本發佈到自己的目錄 → 永遠不覆寫正在被載入的 DLL
publish_app() {
  local v=$1
  if [[ -d releases/$v ]]; then log "releases/$v 已存在，直接使用（回滾也走這條）"; return; fi
  log "發佈 $v → releases/$v"
  dotnet publish src/MyApp -c Release -o "releases/$v" -p:InformationalVersion="$v" --nologo -v q >/dev/null
}

wait_healthy() {
  local port=$1
  for _ in $(seq 1 60); do
    curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  log "port $port 30 秒內沒有變健康"; return 1
}

start_instance() {
  local slot=$1 port=$2 v=$3
  nohup dotnet "releases/$v/MyApp.dll" --urls "http://127.0.0.1:$port" >"logs/$slot.log" 2>&1 &
  echo $! >"run/$slot.pid"
  wait_healthy "$port"
  log "[$slot] $v 已啟動 (pid $(cat run/$slot.pid), port $port)"
}

stop_instance() {
  local slot=$1 port=$2
  [[ -f run/$slot.pid ]] || return 0
  local pid; pid=$(cat "run/$slot.pid")
  curl -fsS -X POST "http://127.0.0.1:$port/admin/shutdown" >/dev/null 2>&1 || true
  for _ in $(seq 1 70); do kill -0 "$pid" 2>/dev/null || { rm -f "run/$slot.pid"; return 0; }; sleep 0.5; done
  log "[$slot] 優雅關機逾時，強制結束"; kill -9 "$pid" 2>/dev/null || true; rm -f "run/$slot.pid"
}
