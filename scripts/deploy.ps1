# 滾動更新：一次換一台，LB 先摘除 → 等請求收完 → 關舊版 → 起新版 → 健康後加回
# 新版起不來時自動回滾這一台到原版本，並停止後續部署（exit 1）
param([Parameter(Mandatory)][string]$Version)
. "$PSScriptRoot\common.ps1"
$prev = if (Test-Path "$D\run\current") { (Get-Content "$D\run\current").Trim() } else { $null }
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

    if (-not (Start-Instance $slot $port $Version)) {
        Log "[$slot] $Version 健康檢查失敗，回滾到 $prev"
        Stop-Instance $slot $port
        if ($prev -and (Start-Instance $slot $port $prev)) {
            Invoke-RestMethod -Method Post "$LB/lb/enable/$slot" | Out-Null
            Log "[$slot] 已回滾到 $prev 並加回 LB"
        }
        Log "部署 $Version 失敗，已中止"
        exit 1
    }
    Invoke-RestMethod -Method Post "$LB/lb/enable/$slot" | Out-Null
    Log "[$slot] 已加回 LB"
}

Set-Content "$D\run\current" $Version
Log "部署完成：目前版本 $Version"
Invoke-RestMethod "$LB/lb/status" | Format-Table
exit 0  # 明確回傳成功；否則 $LASTEXITCODE 會殘留上一個原生指令的值
