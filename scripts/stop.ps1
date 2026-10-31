. "$PSScriptRoot\common.ps1"
foreach ($s in $Slots) { Stop-Instance $s.Name $s.Port }
if (Test-Path run\lb.pid) { Stop-Process -Id ([int](Get-Content run\lb.pid)) -ErrorAction SilentlyContinue; Remove-Item run\lb.pid }
Log '全部已停止'
