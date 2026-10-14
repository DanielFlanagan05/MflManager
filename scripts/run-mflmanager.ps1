$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\MflManager.App\MflManager.App.csproj"
$appOutput = Join-Path $repoRoot "src\MflManager.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"

$dotnetCommand = Get-Command dotnet -ErrorAction Stop | Select-Object -First 1
$dotnetExe = (Resolve-Path $dotnetCommand.Source).Path

Push-Location $repoRoot
try {
    # Keep the project's self-contained default. A framework-dependent apphost
    # cannot reliably discover the runtime when Windows activates the AUMID.
    # Pass the value globally so executable project references use the same mode.
    & $dotnetExe build $appProject -c Debug -p:Platform=x64 -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    winapp run $appOutput
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
