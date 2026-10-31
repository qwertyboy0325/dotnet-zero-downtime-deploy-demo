#!/usr/bin/env bash
source "$(dirname "$0")/common.sh"
for s in "${SLOTS[@]}"; do stop_instance "${s%%:*}" "${s##*:}"; done
[[ -f $D/run/lb.pid ]] && { kill "$(cat "$D/run/lb.pid")" 2>/dev/null || true; rm -f "$D/run/lb.pid"; }
log "全部已停止"
