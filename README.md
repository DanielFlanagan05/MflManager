# MFL Manager

MFL Manager is a Windows 11 desktop application for managing MyFantasyLeague teams through MFL's documented 2026 API. It uses .NET 10, WinUI 3, CommunityToolkit.Mvvm, dependency injection, typed `HttpClient` clients, SQLite caching, Windows Credential Locker, a named-pipe MCP broker, and an embedded Codex Luna process.

## Build

Prerequisites: .NET SDK 10, Developer Mode, WinApp CLI 0.6+, and the WinUI templates. The Microsoft `win-dev-skills` v0.3.0 plugin is the project UI workflow.

```powershell
dotnet restore MflManager.slnx
dotnet build MflManager.slnx -c Debug
dotnet test MflManager.slnx -c Debug --no-build
```

To launch the packaged WinUI app after enabling Developer Mode:

```powershell
winapp run src/MflManager.App/MflManager.App.csproj
```

The app never places an MFL password or `MFL_USER_ID` cookie in URLs, logs, SQLite, CLI output, or agent tool results. Session cookies are stored only in Windows Credential Locker.

The sign-in dialog can optionally load the `username` and `password` fields from the 1Password item `Myfantasyleague` using the locally installed and authenticated `op` CLI. The values remain in memory only long enough to submit the normal login request.

See [architecture](docs/architecture.md), [security](docs/security.md), [testing](docs/testing.md), and the [capability matrix](docs/mfl-capability-matrix.md).
FantasyPros rankings are optional. Set `FANTASYPROS_API_KEY` in the process environment before launching the app; rankings are loaded for QB/RB/WR/TE/K/DEF and used to order the MFL Players and Free Agents lists. The Rankings tab shows the consensus list. Keys are sent only in the HTTPS `x-api-key` header and are never persisted.
