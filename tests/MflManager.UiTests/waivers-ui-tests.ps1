param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'
$windows = winapp ui list-windows -a $AppPid --json | ConvertFrom-Json
$mainWindow = ($windows | Where-Object { $_.title -ne 'PopupHost' } | Select-Object -First 1).hwnd
$failures = 0
winapp ui invoke NavWaivers -w $mainWindow
foreach ($control in @('PendingWaiverClaims', 'RankedWaiverPlayers', 'RankingPositionFilter', 'WaiverAddPlayer', 'WaiverBidAmount', 'WaiverDropPlayer', 'SubmitWaiverBid')) {
    winapp ui wait-for $control -w $mainWindow -t 2000
    if ($LASTEXITCODE -ne 0) { $failures++ }
}
winapp ui wait-for 'WaiverProcessingDateHeader' -w $mainWindow -t 2000
if ($LASTEXITCODE -ne 0) { $failures++ }
winapp ui wait-for 'WaiverCopiesHeader' -w $mainWindow -t 2000
if ($LASTEXITCODE -ne 0) { $failures++ }
winapp ui screenshot -w $mainWindow -o artifacts/waivers-final.png
# Navigation is read-only; never invoke the submission button in a live test.
winapp ui invoke NavMyTeam -w $mainWindow
winapp ui wait-for LineupWeekSelector -w $mainWindow -t 2000
if ($LASTEXITCODE -ne 0) { $failures++ }
winapp ui invoke NavStandings -w $mainWindow
winapp ui wait-for CurrentPageResults -w $mainWindow -t 2000
if ($LASTEXITCODE -ne 0) { $failures++ }
winapp ui invoke NavWaivers -w $mainWindow
Write-Output "Waiver UI verification failures: $failures"
if ($failures -gt 0) { exit 1 }
