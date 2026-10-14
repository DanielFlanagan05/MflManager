param([Parameter(Mandatory)][int]$AppPid)

$ErrorActionPreference = 'Continue'
$pass = 0
$fail = 0
$results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = 'PASS' }
        } else {
            $script:fail++
            $script:results += @{ name = $Name; status = 'FAIL'; detail = "$output" }
        }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = 'FAIL'; detail = "$_" }
    }
}

Test-UI 'Primary navigation exists' { winapp ui wait-for 'PrimaryNavigation' -a $AppPid -t 3000 }
Test-UI 'League selector exists' { winapp ui wait-for 'LeagueSelector' -a $AppPid -t 3000 }
Test-UI 'Navigate Dashboard' { winapp ui invoke 'NavDashboard' -a $AppPid }
Test-UI 'Refresh exists' { winapp ui wait-for 'RefreshAllButton' -a $AppPid -t 3000 }
Test-UI 'Player search exists' { winapp ui wait-for 'GlobalPlayerSearch' -a $AppPid -t 3000 }
Test-UI 'Navigate Players' { winapp ui invoke 'NavPlayers' -a $AppPid }
Test-UI 'Results remain visible' { winapp ui wait-for 'CurrentPageResults' -a $AppPid -t 3000 }
Test-UI 'Search remains available on Players' { winapp ui wait-for 'GlobalPlayerSearch' -a $AppPid -t 3000 }
Test-UI 'Navigate My Team' { winapp ui invoke 'NavMyTeam' -a $AppPid }
Test-UI 'My Team lineup panel loaded' { winapp ui wait-for 'LineupWeekSelector' -a $AppPid -t 3000 }
Test-UI 'Lineup week selector loaded' { winapp ui wait-for 'LineupWeekSelector' -a $AppPid -t 3000 }
Test-UI 'Lineup submit action loaded' { winapp ui wait-for 'SubmitLineupButton' -a $AppPid -t 3000 }
Test-UI 'Starting lineup points column loaded' { winapp ui wait-for 'MyTeamStarterPointsHeader' -a $AppPid -t 3000 }
Test-UI 'Bench points column loaded' { winapp ui wait-for 'MyTeamBenchPointsHeader' -a $AppPid -t 3000 }
Test-UI 'Navigate League Rankings' { winapp ui invoke 'NavLeagueRankings' -a $AppPid }
Test-UI 'League Rankings panel loaded' { winapp ui wait-for 'LeagueRankingsList' -a $AppPid -t 3000 }
Test-UI 'League Rankings list loaded' { winapp ui wait-for 'LeagueRankingsList' -a $AppPid -t 3000 }
Test-UI 'Scoped League Rankings refresh works' {
    winapp ui invoke 'RefreshLeagueRankingsButton' -a $AppPid
    winapp ui wait-for 'LeagueRankingsStatus' -a $AppPid --value 'Week' --contains -t 15000
}
Test-UI 'Open a ranked team and return' {
    Start-Sleep -Milliseconds 500
    $tree = winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json
    $team = $tree.windows.elements | Where-Object { $_.type -eq 'ListItem' -and -not $_.automationId } | Select-Object -First 1
    if (-not $team) { throw 'No League Rankings team row was exposed to UI Automation.' }
    winapp ui invoke $team.name -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Could not open team '$($team.name)'." }
    winapp ui wait-for 'TeamViewBackButton' -a $AppPid -t 10000
    if ($LASTEXITCODE -ne 0) { throw 'Team View did not open.' }
    winapp ui invoke 'TeamViewBackButton' -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw 'Team View Back button could not be invoked.' }
    winapp ui wait-for 'LeagueRankingsList' -a $AppPid -t 15000
}
Test-UI 'Navigate Agent' { winapp ui invoke 'NavAgent' -a $AppPid }
Test-UI 'Terminal panel loaded' { winapp ui wait-for 'CodexTerminal' -a $AppPid -t 3000 }
Test-UI 'Agent start control loaded' { winapp ui wait-for 'StartAgentButton' -a $AppPid -t 3000 }
Test-UI 'Agent action history loaded' { winapp ui wait-for 'AgentActionHistory' -a $AppPid -t 3000 }

$elements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
$missing = @($elements | Where-Object {
    $_.type -match 'Button|TextBox|ComboBox|CheckBox|List|TabItem|Edit' -and
    $_.name -notmatch 'Minimize|Maximize|Close|System' -and
    $_.className -notmatch 'PickerHost|#32770|CabinetWClass' -and
    -not $_.automationId
})
if ($missing.Count -eq 0) {
    $pass++
    $results += @{ name = 'Interactive controls have AutomationId'; status = 'PASS' }
} else {
    $fail++
    $results += @{ name = 'AutomationId coverage'; status = 'FAIL'; detail = (($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ') }
}

winapp ui screenshot -a $AppPid -o (Join-Path $PSScriptRoot 'test-screenshot.png') 2>$null
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'test-results.json') -Encoding utf8NoBOM
Write-Output "Passed: $pass | Failed: $fail"
if ($fail -gt 0) { exit 1 }
