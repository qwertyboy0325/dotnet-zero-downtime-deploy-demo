#!/usr/bin/env bash
# 滾動更新：一次換一台，LB 先摘除 → 等請求收完 → 關舊版 → 起新版 → 健康後加回
source "$(dirname "$0")/common.sh"
V=${1:?用法: deploy.sh <版本，例如 v2>}
publish_app "$V"

for s in "${SLOTS[@]}"; do
  slot=${s%%:*}; port=${s##*:}
  log "[$slot] 從 LB 摘除"
  curl -fsS -X POST "$LB/lb/drain/$slot" >/dev/null

  # 等 LB 上這台的進行中請求歸零（最多 30 秒）
  for _ in $(seq 1 60); do [[ $(curl -fsS "$LB/lb/inflight/$slot") == 0 ]] && break; sleep 0.5; done
  log "[$slot] 進行中請求已清空，關閉舊版"
  stop_instance "$slot" "$port"

  start_instance "$slot" "$port" "$V"
  curl -fsS -X POST "$LB/lb/enable/$slot" >/dev/null
  log "[$slot] 已加回 LB"
done

echo "$V" >run/current
log "部署完成：目前版本 $V  狀態：$(curl -fsS $LB/lb/status)"
