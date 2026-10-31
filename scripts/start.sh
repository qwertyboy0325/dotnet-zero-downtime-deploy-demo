#!/usr/bin/env bash
# 第一次啟動：發佈 v1、起兩個實例、起 LB
source "$(dirname "$0")/common.sh"
V=${1:-v1}
publish_app "$V"
for s in "${SLOTS[@]}"; do start_instance "${s%%:*}" "${s##*:}" "$V"; done
dotnet publish src/LoadBalancer -c Release -o build/lb --nologo -v q >/dev/null
nohup dotnet build/lb/LoadBalancer.dll >logs/lb.log 2>&1 &
echo $! >run/lb.pid
for _ in $(seq 1 40); do curl -fsS "$LB/lb/status" >/dev/null 2>&1 && break; sleep 0.5; done
echo "$V" >run/current
log "LB 已啟動：$LB  狀態：$(curl -fsS $LB/lb/status)"
