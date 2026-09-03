# Testing

`dotnet test MflManager.slnx -c Debug` runs deterministic non-live tests. Unit tests cover singleton/array JSON, player/franchise parsing, capability visibility, draft/lineup/waiver parameter generation, and secret redaction behavior. Contract tests consume synthetic fixtures for empty/singleton collections, owner/commissioner shapes, expired sessions, 429, host changes, mutation rejection inside HTTP 200, and an all-endpoint response audit. The audit asserts HTTP 200 handling for discovery/export/draft clients, parses every response type used by refresh, and writes raw plus parsed values to `api-response-audit.log` under the contract test output directory before reading it back. Integration tests exercise SQLite snapshot/journal round trips.

Live reads may run only when `MFL_TEST_LEAGUE`, `MFL_TEST_SEASON`, and `MFL_TEST_HOST` are set. Live mutations additionally require `MFL_ALLOW_TEST_MUTATIONS=1`; never point those variables at a production league.

WinUI automation requires Developer Mode and WinApp CLI. Build and launch through `winapp`, then run the one-process script in `tests/MflManager.UiTests/ui-tests.json`. It covers login dialog, league selection, refresh status/partial failure, player filtering, navigation, confirmations, keyboard shortcuts, Light/Dark/High Contrast, terminal lifecycle, and visible refresh after an agent mutation.

Before release:

```powershell
dotnet restore MflManager.slnx
dotnet build MflManager.slnx -c Release
dotnet test MflManager.slnx -c Release --no-build
dotnet list MflManager.slnx package --vulnerable --include-transitive
```

Run `winui-code-review`, `winui-ui-testing`, and `winui-packaging`; inspect the produced MSIX and run the secret audit described in `docs/security.md`.
