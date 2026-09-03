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
Test-UI 'Refresh exists' { winapp ui wait-for 'RefreshAllButton' -a $AppPid -t 3000 }
Test-UI 'Player search exists' { winapp ui wait-for 'GlobalPlayerSearch' -a $AppPid -t 3000 }
Test-UI 'Navigate Players' { winapp ui invoke 'NavPlayers' -a $AppPid }
Test-UI 'Results remain visible' { winapp ui wait-for 'CurrentPageResults' -a $AppPid -t 3000 }
Test-UI 'Search accepts keyboard input' {
    winapp ui set-value 'GlobalPlayerSearch' 'DET' -a $AppPid
    winapp ui wait-for 'GlobalPlayerSearch' -a $AppPid --value 'DET' -t 2000
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
