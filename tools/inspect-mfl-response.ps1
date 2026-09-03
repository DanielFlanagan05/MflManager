param(
    [Parameter(Mandatory)] [string]$Path
)

$ErrorActionPreference = 'Stop'
$text = Get-Content -LiteralPath $Path -Raw
$redacted = $text `
    -replace '(?i)(MFL_USER_ID["''=:\s]+)[^"'';\s<]+', '$1[REDACTED]' `
    -replace '(?i)(PASSWORD["''=:\s]+)[^"'';&\s<]+', '$1[REDACTED]' `
    -replace '(?i)(<email>).*?(</email>)', '$1[REDACTED]$2' `
    -replace '(?i)("email"\s*:\s*").*?(")', '$1[REDACTED]$2'

try {
    $json = $redacted | ConvertFrom-Json -Depth 100
    $json | ConvertTo-Json -Depth 12
}
catch {
    try { [xml]$xml = $redacted; $xml.OuterXml }
    catch { $redacted }
}
