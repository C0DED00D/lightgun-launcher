# Report on recent game sessions (recorded by the launcher in app\sessions, see launcher\Session.cs).
#   powershell -File lastsession.ps1            -> the newest session
#   powershell -File lastsession.ps1 -count 5   -> the newest 5, oldest first
#   powershell -File lastsession.ps1 -all       -> full session.log instead of the highlights
#   -sessions <dir>                             -> another sessions folder (default <launcher root>\app\sessions)
param([int]$count = 1, [switch]$all, [string]$sessions = (Join-Path $PSScriptRoot '..\..\app\sessions'))
$base = $sessions
if (-not (Test-Path $base)) { "no sessions recorded yet"; exit }
$dirs = Get-ChildItem $base -Directory | Sort-Object Name | Select-Object -Last $count
foreach ($d in $dirs) {
  "==================== $($d.Name)"
  $sum = Join-Path $d.FullName 'summary.txt'
  if (Test-Path $sum) { Get-Content $sum } else { "(no summary: session still running or the launcher was killed)" }
  $log = Join-Path $d.FullName 'session.log'
  if (Test-Path $log) {
    "---- events"
    $lines = Get-Content $log
    if (-not $all) {
      # Highlights: everything except routine screenshot/copy lines and repeated identical events.
      $lines = $lines | Where-Object { $_ -notmatch 'copied emulators' }
    }
    $lines
  }
  $shots = Get-ChildItem $d.FullName -Filter *.jpg
  "---- $($shots.Count) screenshots in $($d.FullName)"
  $shots | Where-Object { $_.Name -notmatch 'periodic' } | Select-Object -First 40 | ForEach-Object { "  $($_.Name)" }
  "---- emulator logs: " + ((Get-ChildItem $d.FullName -File | Where-Object { $_.Extension -ne '.jpg' -and $_.Name -notin 'session.log', 'summary.txt' } | ForEach-Object Name) -join ', ')
}
