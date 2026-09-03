param(
    [int]$Season = (Get-Date).Year,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\mfl-api-endpoints.txt')
)

$ErrorActionPreference = 'Stop'
$uri = "https://api.myfantasyleague.com/$Season/api_info?STATE=details&PRINTER=1"
$response = Invoke-WebRequest -Uri $uri -UseBasicParsing
$matches = [regex]::Matches($response.Content, '<td[^>]*><b>([^<]+)</b></td>')
$names = $matches | ForEach-Object { [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value).Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_]+$' } | Sort-Object -Unique
$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
@("# MFL $Season API request names", "# Retrieved $([DateTimeOffset]::UtcNow.ToString('O')) from $uri", '') + $names | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Wrote $($names.Count) request names to $OutputPath"
