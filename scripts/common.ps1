# 共用設定與函式（被 start.ps1 / deploy.ps1 / stop.ps1 dot-source）
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$LB = 'http://127.0.0.1:8080'
$Slots = @(@{ Name = 'a'; Port = 5001 }, @{ Name = 'b'; Port = 5002 })
New-Item -ItemType Directory -Force run, logs, releases | Out-Null

function Log($msg) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $msg" }

# 每個版本發佈到自己的目錄 → 永遠不覆寫正在被載入的 DLL
function Publish-App($v) {
    if (Test-Path "releases\$v") { Log "releases\$v 已存在，直接使用（回滾也走這條）"; return }
    Log "發佈 $v → releases\$v"
    dotnet publish src\MyApp -c Release -o "releases\$v" -p:InformationalVersion=$v --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish 失敗" }
}

function Wait-Healthy($port) {
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; return }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw "port $port 30 秒內沒有變健康"
}

function Start-Instance($slot, $port, $v) {
    $p = Start-Process dotnet -ArgumentList "releases\$v\MyApp.dll", '--urls', "http://127.0.0.1:$port" `
        -RedirectStandardOutput "logs\$slot.log" -RedirectStandardError "logs\$slot.err.log" -NoNewWindow -PassThru
    Set-Content "run\$slot.pid" $p.Id
    Wait-Healthy $port
    Log "[$slot] $v 已啟動 (pid $($p.Id), port $port)"
}

function Stop-Instance($slot, $port) {
    if (-not (Test-Path "run\$slot.pid")) { return }
    $procId = [int](Get-Content "run\$slot.pid")
    try { Invoke-WebRequest -Method Post "http://127.0.0.1:$port/admin/shutdown" -UseBasicParsing -TimeoutSec 2 | Out-Null } catch {}
    try { Wait-Process -Id $procId -Timeout 35 -ErrorAction Stop }
    catch [System.TimeoutException] { Log "[$slot] 優雅關機逾時，強制結束"; Stop-Process -Id $procId -Force }
    catch {}  # 程序已經不在
    Remove-Item "run\$slot.pid"
}
