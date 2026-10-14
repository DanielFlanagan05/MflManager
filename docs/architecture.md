# Architecture

The app is a layered WinUI 3 solution. `MflManager.Domain` contains immutable contexts, snapshots, player/roster/draft/trade models, capabilities, and typed mutations. `MflManager.Application` defines authentication, export/import/draft, persistence, refresh, state, capability, confirmation, and mutation contracts. It contains no UI or transport code.

`MflManager.MflApi` provides the cookie handler, correlation IDs, tolerant JSON/XML parsing, singleton-or-array converter, safe-read backoff, host-aware typed clients, common refresh reducer, and mutation coordinator. A read can retry; a mutation cannot. A `SemaphoreSlim` per season/league prevents concurrent mutations. After MFL accepts a mutation, the coordinator journals it and invokes `RefreshAllAsync`.

`MflManager.Persistence` stores one last-good JSON snapshot per season/league and a sanitized append-only operation journal in SQLite WAL mode. Cookies are never stored there. The WinUI process supplies Credential Locker implementations for cookie and account metadata.

`MflManager.AgentServer` implements MCP JSON-RPC over a current-user-only named pipe. It shares the desktop process's state and application services, requires explicit league context, exposes typed tools only, and defaults mutations to preview. `MflManager.AgentCli` is a human/agent-friendly pipe client. `MflManager.Terminal` resolves the standalone Codex CLI, reads the active account identity through the official app-server `account/read` surface, runs Luna through `codex exec --json`, resumes the same Codex thread for follow-up prompts, and relays sanitized agent output to the Agent page. Interactive ChatGPT authentication stays in the Codex CLI/browser flow; the app never receives Codex credentials.

`MflManager.App` is the composition root and WinUI shell. View models use application interfaces; no view model constructs MFL HTTP calls. A `NavigationView` hosts keyboard-accessible Dashboard, My Team, Players, Draft, Waivers, Trades, Matchups, Standings, League, Messages, Agent, and Settings work areas.

## Refresh state flow

Safe sections run concurrently with a six-request bound. Successful sections replace cached values. Failed sections preserve last-good values and receive a stale/error marker. The finished snapshot is committed once, published through `ILeagueState`, and observed by the UI on its dispatcher queue.
