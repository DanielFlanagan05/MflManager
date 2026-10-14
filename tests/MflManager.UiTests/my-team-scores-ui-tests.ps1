param(
    [Parameter(Mandatory)][int]$AppPid,
    [string[]]$ExpectedStatusCodes = @(),
    [int]$TimeoutSeconds = 30
)

# Read-only smoke test against an already signed-in app. Never submit a lineup.
$ErrorActionPreference = 'Stop'
function Get-Elements($items) {
    foreach ($item in $items) {
        $item
        Get-Elements $item.children
    }
}

winapp ui invoke NavMyTeam -a $AppPid
if ($LASTEXITCODE -ne 0) { throw 'My Team navigation failed.' }
winapp ui scroll MyTeamScrollViewer -a $AppPid --to top
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    $tree = winapp ui inspect -a $AppPid --depth 14 --json 2>$null | ConvertFrom-Json
    $elements = @(Get-Elements $tree.windows.elements)
    $starterHeader = $elements | Where-Object automationId -eq MyTeamStarterPointsHeader | Select-Object -First 1
    $benchHeader = $elements | Where-Object automationId -eq MyTeamBenchPointsHeader | Select-Object -First 1
    $texts = @($elements | Where-Object type -eq Text)
    $starterScores = @($texts | Where-Object {
        $starterHeader -and $benchHeader -and
        [Math]::Abs($_.x - $starterHeader.x) -lt 24 -and
        $_.y -gt $starterHeader.y -and $_.y -lt $benchHeader.y -and $_.name -match '^-?\d+([.,]\d+)?$'
    })
    $benchScores = @($texts | Where-Object {
        $benchHeader -and [Math]::Abs($_.x - $benchHeader.x) -lt 24 -and
        $_.y -gt $benchHeader.y -and $_.name -match '^-?\d+([.,]\d+)?$'
    })
    if ($starterScores.Count -gt 0 -and $benchScores.Count -gt 0) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)

if ($starterScores.Count -eq 0) { throw 'No numeric starter points are displayed.' }
if ($benchScores.Count -eq 0) { throw 'No numeric bench points are displayed.' }
$statusHeaders = @($elements | Where-Object { $_.automationId -in @('MyTeamStarterStatusHeader', 'MyTeamBenchStatusHeader') })
winapp ui scroll MyTeamScrollViewer -a $AppPid --to bottom
if ($LASTEXITCODE -ne 0) { throw 'Could not scroll the bench into view.' }
$tree = winapp ui inspect -a $AppPid --depth 14 --json 2>$null | ConvertFrom-Json
$elements = @(Get-Elements $tree.windows.elements)
$texts = @($elements | Where-Object type -eq Text)
$scrolledBenchHeader = $elements | Where-Object automationId -eq MyTeamBenchPointsHeader | Select-Object -First 1
$visibleBenchScores = @($texts | Where-Object {
    $scrolledBenchHeader -and [Math]::Abs($_.x - $scrolledBenchHeader.x) -lt 24 -and
    $_.y -gt $scrolledBenchHeader.y -and -not $_.isOffscreen -and $_.name -match '^-?\d+([.,]\d+)?$'
})
if ($visibleBenchScores.Count -eq 0) { throw 'No numeric bench points are displayed after scrolling.' }
foreach ($code in $ExpectedStatusCodes) {
    $found = @($texts | Where-Object {
        $text = $_
        $text.name -eq $code -and @($statusHeaders | Where-Object {
            [Math]::Abs($text.x - $_.x) -lt 24 -and -not $text.isOffscreen
        }).Count -gt 0
    })
    if ($found.Count -eq 0) { throw "Expected injury status $code is not displayed in a status column." }
}
winapp ui scroll MyTeamScrollViewer -a $AppPid --to top
Write-Host "PASS: $($starterScores.Count) numeric starter scores, $($visibleBenchScores.Count) numeric bench scores; $($ExpectedStatusCodes.Count) expected injury codes displayed."
