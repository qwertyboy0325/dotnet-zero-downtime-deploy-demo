# 滾動更新：一次換一台，LB 先摘除 → 等請求收完 → 關舊版 → 起新版 → 健康後加回
param([Parameter(Mandatory)][string]$Version)
. "$PSScriptRoot\common.ps1"
Publish-App $Version

foreach ($s in $Slots) {
    $slot = $s.Name; $port = $s.Port
    Log "[$slot] 從 LB 摘除"
    Invoke-RestMethod -Method Post "$LB/lb/drain/$slot" | Out-Null

    # 等 LB 上這台的進行中請求歸零（最多 30 秒）
    for ($i = 0; $i -lt 60; $i++) {
        if ([int](Invoke-RestMethod "$LB/lb/inflight/$slot") -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Log "[$slot] 進行中請求已清空，關閉舊版"
    Stop-Instance $slot $port

    Start-Instance $slot $port $Version
    Invoke-RestMethod -Method Post "$LB/lb/enable/$slot" | Out-Null
    Log "[$slot] 已加回 LB"
}

Set-Content run\current $Version
Log "部署完成：目前版本 $Version"
Invoke-RestMethod "$LB/lb/status" | Format-Table
