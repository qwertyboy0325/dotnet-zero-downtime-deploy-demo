# 第一次啟動：發佈 v1、起兩個實例、起 LB
param([string]$Version = 'v1')
. "$PSScriptRoot\common.ps1"
Publish-App $Version
foreach ($s in $Slots) { if (-not (Start-Instance $s.Name $s.Port $Version)) { throw "[$($s.Name)] 啟動失敗" } }
dotnet publish "$Src\src\LoadBalancer" -c Release -o "$D\build\lb" --nologo -v q | Out-Null
$lb = Start-Process dotnet -ArgumentList "`"$D\build\lb\LoadBalancer.dll`"" `
    -RedirectStandardOutput "$D\logs\lb.log" -RedirectStandardError "$D\logs\lb.err.log" -NoNewWindow -PassThru
Set-Content "$D\run\lb.pid" $lb.Id
for ($i = 0; $i -lt 40; $i++) { try { Invoke-RestMethod "$LB/lb/status" | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }
Set-Content "$D\run\current" $Version
Log "LB 已啟動：$LB"
Invoke-RestMethod "$LB/lb/status" | Format-Table
