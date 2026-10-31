# 端對端驗證（CI 會跑）：壓測進行中依序
#   1. 部署 v2            → 應成功
#   2. 部署壞掉的 v3      → 應失敗並自動回滾，目前版本維持 v2
#   3. 回滾到 v1          → 應成功
# 全程壓測不可有任何失敗請求。另外示範：Windows 上執行中的 DLL 確實無法覆寫。
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $env:DEPLOY_ROOT) { $env:DEPLOY_ROOT = Join-Path $PWD '.e2e' }
$env:HEALTH_TIMEOUT_SEC = '10'
$D = $env:DEPLOY_ROOT
$LB = 'http://127.0.0.1:8080'
if (Test-Path $D) { Remove-Item -Recurse -Force $D }
function Fail($msg) { Write-Host "FAIL: $msg"; & "$PSScriptRoot\stop.ps1"; exit 1 }

try {
    dotnet build src\LoadTester -c Release --nologo -v q | Out-Null
    & "$PSScriptRoot\start.ps1" -Version v1

    # 反例示範：直接覆寫執行中的 DLL 會失敗
    try { [IO.File]::OpenWrite("$D\releases\v1\MyApp.dll").Dispose(); Write-Host "注意：releases\v1\MyApp.dll 執行中仍可寫入（非 Windows？）" }
    catch { Write-Host "確認：執行中的 releases\v1\MyApp.dll 被鎖住無法覆寫 → $($_.Exception.Message)" }

    $lt = Start-Process dotnet -ArgumentList 'src\LoadTester\bin\Release\net8.0\LoadTester.dll', "$LB/", '60', '20' `
        -RedirectStandardOutput "$D\logs\loadtest.log" -RedirectStandardError "$D\logs\loadtest.err.log" -NoNewWindow -PassThru
    $null = $lt.Handle  # 先取 Handle，結束後才拿得到 ExitCode
    Start-Sleep 3

    Write-Host '== 1. 部署 v2 =='
    & "$PSScriptRoot\deploy.ps1" -Version v2
    if ($LASTEXITCODE -ne 0) { Fail '部署 v2 失敗' }

    Write-Host '== 2. 部署壞掉的版本（預期自動回滾）=='
    $env:PUBLISH_ARGS = '-p:SimulateBrokenHealth=true'
    & "$PSScriptRoot\deploy.ps1" -Version v3-broken
    $brokenExit = $LASTEXITCODE
    $env:PUBLISH_ARGS = $null
    if ($brokenExit -eq 0) { Fail '壞掉的版本居然部署成功' }
    if ((Get-Content "$D\run\current").Trim() -ne 'v2') { Fail '目前版本應維持 v2' }
    if (@(Invoke-RestMethod "$LB/lb/status" | Where-Object state -eq 'enabled').Count -ne 2) { Fail '兩台都應在 LB 上' }
    if (-not (Invoke-RestMethod "$LB/").StartsWith('v2')) { Fail '回滾後應由 v2 回應' }

    Write-Host '== 3. 回滾到 v1 =='
    & "$PSScriptRoot\deploy.ps1" -Version v1
    if ($LASTEXITCODE -ne 0) { Fail '回滾 v1 失敗' }

    Write-Host '== 等壓測結束 =='
    $lt.WaitForExit()
    Get-Content "$D\logs\loadtest.log" | Select-Object -Last 4
    if ($lt.ExitCode -ne 0) { Fail '壓測有失敗請求' }
    Write-Host 'PASS: 部署、自動回滾、手動回滾過程中 0 個失敗請求'
}
finally {
    & "$PSScriptRoot\stop.ps1" | Out-Null
}
