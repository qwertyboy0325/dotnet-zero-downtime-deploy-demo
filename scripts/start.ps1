# 第一次啟動：發佈 v1、起兩個實例、起 LB
param([string]$Version = 'v1')
. "$PSScriptRoot\common.ps1"
Publish-App $Version
foreach ($s in $Slots) { Start-Instance $s.Name $s.Port $Version }
dotnet publish src\LoadBalancer -c Release -o build\lb --nologo -v q | Out-Null
$lb = Start-Process dotnet -ArgumentList 'build\lb\LoadBalancer.dll' `
    -RedirectStandardOutput logs\lb.log -RedirectStandardError logs\lb.err.log -NoNewWindow -PassThru
Set-Content run\lb.pid $lb.Id
for ($i = 0; $i -lt 40; $i++) { try { Invoke-RestMethod "$LB/lb/status" | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }
Set-Content run\current $Version
Log "LB 已啟動：$LB"
Invoke-RestMethod "$LB/lb/status" | Format-Table
