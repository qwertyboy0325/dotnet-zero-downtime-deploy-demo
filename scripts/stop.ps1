. "$PSScriptRoot\common.ps1"
foreach ($s in $Slots) { Stop-Instance $s.Name $s.Port }
if (Test-Path "$D\run\lb.pid") { Stop-Process -Id ([int](Get-Content "$D\run\lb.pid")) -ErrorAction SilentlyContinue; Remove-Item "$D\run\lb.pid" }
Log '全部已停止'
