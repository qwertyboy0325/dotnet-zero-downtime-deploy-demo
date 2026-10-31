# 共用設定與函式（被 start.ps1 / deploy.ps1 / stop.ps1 dot-source）
$ErrorActionPreference = 'Stop'
$Src = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# 部署產物（releases/run/logs）放的位置；CI/CD 時要放在原始碼工作區外面，避免 checkout 清掉執行中的版本
$D = if ($env:DEPLOY_ROOT) { $env:DEPLOY_ROOT } else { $Src }
$LB = 'http://127.0.0.1:8080'
$Slots = @(@{ Name = 'a'; Port = 5001 }, @{ Name = 'b'; Port = 5002 })
$HealthTimeoutSec = if ($env:HEALTH_TIMEOUT_SEC) { [int]$env:HEALTH_TIMEOUT_SEC } else { 30 }
New-Item -ItemType Directory -Force "$D\run", "$D\logs", "$D\releases" | Out-Null

function Log($msg) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $msg" }

# 每個版本發佈到自己的目錄 → 永遠不覆寫正在被載入的 DLL
# $env:PUBLISH_ARGS 可帶額外的 publish 參數（e2e 用它做出一個壞掉的版本）
function Publish-App($v) {
    if (Test-Path "$D\releases\$v") { Log "releases\$v 已存在，直接使用（回滾也走這條）"; return }
    Log "發佈 $v → releases\$v"
    $extra = if ($env:PUBLISH_ARGS) { $env:PUBLISH_ARGS -split ' ' } else { @() }
    dotnet publish "$Src\src\MyApp" -c Release -o "$D\releases\$v" -p:InformationalVersion=$v @extra --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish 失敗" }
}

function Test-Healthy($port) {
    for ($i = 0; $i -lt $HealthTimeoutSec * 2; $i++) {
        try { Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true }
        catch { Start-Sleep -Milliseconds 500 }
    }
    Log "port $port $HealthTimeoutSec 秒內沒有變健康"
    return $false
}

# 啟動實例；健康檢查通過回傳 $true
function Start-Instance($slot, $port, $v) {
    # Start-Process 不會幫含空白的路徑加引號，要自己包
    $p = Start-Process dotnet -ArgumentList "`"$D\releases\$v\MyApp.dll`"", '--urls', "http://127.0.0.1:$port" `
        -RedirectStandardOutput "$D\logs\$slot.log" -RedirectStandardError "$D\logs\$slot.err.log" -NoNewWindow -PassThru
    Set-Content "$D\run\$slot.pid" $p.Id
    if (-not (Test-Healthy $port)) { return $false }
    Log "[$slot] $v 已啟動 (pid $($p.Id), port $port)"
    return $true
}

function Stop-Instance($slot, $port) {
    if (-not (Test-Path "$D\run\$slot.pid")) { return }
    $procId = [int](Get-Content "$D\run\$slot.pid")
    try { Invoke-WebRequest -Method Post "http://127.0.0.1:$port/admin/shutdown" -UseBasicParsing -TimeoutSec 2 | Out-Null } catch {}
    try { Wait-Process -Id $procId -Timeout 35 -ErrorAction Stop }
    catch [System.TimeoutException] { Log "[$slot] 優雅關機逾時，強制結束"; Stop-Process -Id $procId -Force }
    catch {}  # 程序已經不在
    Remove-Item "$D\run\$slot.pid"
}
