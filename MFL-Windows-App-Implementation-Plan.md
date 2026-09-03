# MFL Windows Application — Implementation Plan

## 1. Objective

Build a polished Windows-native fantasy football management application written in C# and based entirely around MyFantasyLeague’s documented API.

The application must:

- Authenticate with an MFL user account.
- Discover and switch between leagues accessible to that account.
- Display essentially all league and franchise information exposed by the MFL API.
- Perform every owner-authorized management action supported by the API.
- Expose commissioner-only functionality when the authenticated account has commissioner permissions.
- Include an embedded Codex terminal running `gpt-5.6-luna`.
- Let that Codex agent manage the user’s team through documented local tools.
- Refresh the application after every successful mutation so the user can verify MFL accepted the change.
- Provide a modern, keyboard-and-mouse-friendly WinUI interface.

Use the current [Microsoft WinDev Skills repository](https://github.com/microsoft/win-dev-skills) throughout UI development. The plugin specifically targets WinUI 3 and Windows App SDK development.

## 2. Required technology

Use:

- C# and .NET 10, or the newest stable supported .NET release.
- WinUI 3.
- Current stable Windows App SDK.
- MVVM using `CommunityToolkit.Mvvm`.
- `Microsoft.Extensions.DependencyInjection`.
- `Microsoft.Extensions.Hosting`.
- `HttpClientFactory`.
- `System.Text.Json` with tolerant custom converters.
- SQLite for local cached league data and operation history.
- Windows Credential Locker for MFL credentials/session information.
- MSIX for installation and distribution.
- xUnit or NUnit for non-UI tests.
- WinApp CLI for building, running, inspecting, and testing the application.

Pin package versions after selecting current stable releases. Target Windows 11 x64 initially without preventing future ARM64 support.

## 3. WinDev skill setup

Before creating UI code:

1. Install the `microsoft/win-dev-skills` plugin.
2. Run the `winui-setup` skill.
3. Verify .NET SDK 8.0.100 or newer, WinApp CLI 0.6 or newer, Developer Mode, and Git.
4. Invoke `winui-design` before authoring major XAML pages.
5. Use `winapp find-ui` to research the correct WinUI controls before implementing each page.
6. Use `winui-dev-workflow` during development, `winui-code-review` before completing each milestone, `winui-ui-testing` for UI automation, and `winui-packaging` for MSIX delivery.

Do not substitute WPF, UWP, Electron, MAUI, or a web frontend.

## 4. Solution structure

```text
MflManager/
├── AGENTS.md
├── README.md
├── docs/
│   ├── architecture.md
│   ├── mfl-api-reference.md
│   ├── mfl-capability-matrix.md
│   ├── agent-playbook.md
│   ├── security.md
│   └── testing.md
├── src/
│   ├── MflManager.App/
│   ├── MflManager.Application/
│   ├── MflManager.Domain/
│   ├── MflManager.MflApi/
│   ├── MflManager.Persistence/
│   ├── MflManager.AgentServer/
│   ├── MflManager.AgentCli/
│   └── MflManager.Terminal/
├── tests/
│   ├── MflManager.UnitTests/
│   ├── MflManager.IntegrationTests/
│   ├── MflManager.ContractTests/
│   └── MflManager.UiTests/
└── tools/
    ├── refresh-api-reference.ps1
    └── inspect-mfl-response.ps1
```

Responsibilities:

- `App`: WinUI pages, controls, navigation, dialogs and view models.
- `Application`: use cases, refresh coordination and mutation orchestration.
- `Domain`: league, player, roster, draft, waiver and transaction models.
- `MflApi`: authentication, endpoints, DTOs, parsing and HTTP behavior.
- `Persistence`: SQLite snapshot/cache and operation journal.
- `AgentServer`: local MCP server exposing safe MFL tools to Codex.
- `AgentCli`: human- and agent-friendly command-line interface.
- `Terminal`: embedded pseudo-terminal and Codex process management.

Do not put API calls directly in view models.

## 5. MFL authentication and sessions

MFL authorization is cookie-based:

1. Send credentials through HTTPS `POST` to `https://api.myfantasyleague.com/{season}/login`.
2. Include `USERNAME`, `PASSWORD`, and `XML=1`.
3. Parse the returned XML status object.
4. Extract the `MFL_USER_ID` cookie.
5. Use that cookie on all protected calls.
6. Never place the cookie in URLs or logs.
7. Store persistent secrets using Windows Credential Locker.
8. Provide explicit sign-out and “forget saved account” actions.

MFL API keys are read-only and cannot authorize changes. Mutations require the authenticated cookie.

After login, call `myleagues` to obtain league IDs, seasons and league hosts. Do not assume every league uses the same `wwwXX` host. Do not hardcode 2026: create a `SeasonContext` and permit historical read-only browsing.

## 6. MFL API client architecture

Create:

```csharp
IMflAuthenticationClient
IMflExportClient
IMflImportClient
IMflDraftClient
IMflLeagueDiscoveryClient
IMflCapabilityService
IMflRefreshService
IMflMutationCoordinator
```

Use a typed `HttpClient` with automatic cookie handling, correlation IDs, cancellation, timeouts, `429` handling, safe-read retries only, no automatic mutation retries, redacted logging, per-league host resolution, JSON where available, and XML where required.

MFL may return a single object where an array normally appears. Implement reusable single-object-or-array converters.

```csharp
MflResult<T>
{
    bool IsSuccess;
    T? Value;
    string? MflErrorCode;
    string? UserMessage;
    HttpStatusCode? StatusCode;
    string CorrelationId;
}
```

Do not treat HTTP 200 alone as mutation success. Parse MFL’s response body.

References: [MFL API general documentation](https://api.myfantasyleague.com/2026/api_info) and [request reference](https://api.myfantasyleague.com/2026/api_info?STATE=details).

## 7. Capability inventory

Create `docs/mfl-capability-matrix.md` from the current API reference. For each endpoint record its command category, request type, read/mutation status, owner and commissioner access, required parameters, response format, corresponding UI, agent tool, implementation status, and contract-test status.

Cover at minimum:

### League data

- League configuration, rules and scoring.
- Franchises and authorized owner data.
- Rosters, standings, weekly results, schedules and matchups.
- Transactions, accounting where authorized, calendar, polls and message board.
- NFL schedule and player information.

### Player research

- Complete player database.
- Free agents and availability.
- Injuries, rankings, projections, ADP/AAV and watch list where exposed.

### Team management

- Weekly lineup and tiebreakers.
- FCFS add/drop.
- Standard, conditional and blind-bid waivers.
- IR and taxi-squad moves.
- Keeper selections, watch list and trade bait.

### Drafting

- Draft configuration/results and current on-the-clock information.
- Live pick submission.
- Personal queue through `myDraftList`.
- Draft comments.
- Commissioner pause/resume/skip/undo when authorized.

### Trades

- Pending trades.
- Propose, accept, reject and revoke.
- Players, current/future picks and blind-bid dollars where supported.

Implement all documented owner-accessible functionality. Show commissioner functions only when authorized. Hide or disable unsupported league features with an explanation.

## 8. Draft implementation

Use `/{season}/live_draft` with:

```text
L=<league_id>
CMD=DRAFT
PLAYER_PICK=<player_id>
ROUND=<round>
PICK=<pick-within-round>
JSON=1
```

Rules:

- Submit only when the current franchise is on the clock.
- Verify round, pick and franchise immediately before submission.
- Never reuse stale draft state.
- Disable submission while in flight.
- Confirm player, NFL team, position and pick number.
- Refresh all after success.
- If submission succeeds but verification refresh fails, clearly distinguish those states.

Use `/import?TYPE=myDraftList` with comma-separated `PLAYERS` for the draft queue. Do not use `import?TYPE=draftResults` for live drafting; it is intended for wholesale offline-draft imports.

## 9. Refresh architecture

Implement one central method used by the UI, CLI and agent:

```csharp
Task<RefreshReport> RefreshAllAsync(
    LeagueContext league,
    RefreshReason reason,
    CancellationToken cancellationToken);
```

Refresh league configuration/rules, franchises, rosters, lineup, standings, matchups, free agents, transactions, waivers, trades, draft state/queue, IR, taxi squad, keepers, messages, polls and calendar as applicable. Refresh static global data only when stale unless force-refresh is selected.

Run safe reads concurrently with bounded concurrency. Show per-section progress, preserve last-good data on partial failure, mark stale sections, return a success/failure report, publish `SnapshotUpdated`, and update observable state on the UI thread.

Every UI or agent mutation follows:

```text
Validate → submit mutation → parse MFL result
  → if rejected: display error and preserve state
  → if accepted: journal mutation → RefreshAllAsync
  → publish updated snapshot → refresh all visible pages
```

Prevent overlapping mutations with a per-league lock.

## 10. User interface

Use a developer-tool/dashboard silhouette with a left `NavigationView`:

- Dashboard
- My Team
- Players
- Draft
- Waivers
- Trades
- Matchups
- Standings
- League
- Messages
- Agent
- Settings

### Dashboard

Show league/franchise, record, standing, current matchup, roster warnings, pending waivers/trades, draft clock, last refresh and a global Refresh button.

### My Team

Separate roster, weekly lineup, bench, IR, taxi squad and keepers. Drag/drop may be supplementary, but explicit keyboard-accessible controls are authoritative.

### Players

Use `AutoSuggestBox`, position/team/status filters, ownership, sorting, a detail pane, add/drop, waiver, watch-list and draft-queue actions, and virtualized results.

### Draft

Use a responsive three-pane layout for draft board/history, player search, and personal queue/current pick. Provide keyboard shortcuts but require confirmation for submission.

### Waivers

Support FCFS, priority/conditional requests, blind bids, reordering, replace/append semantics, and final review.

### Agent

Show embedded Codex terminal, connection state, tool-server state, agent action history, refresh/verification state, and stop/restart controls.

### Styling and accessibility

Use semantic theme resources rather than hardcoded literals. Use blues for primary/information, greens for success/availability, black/charcoal for dark surfaces, and gray for secondary surfaces. Keep text contrast legible. Implement Light, Dark and High Contrast dictionaries, respect system preference, and use visible labels, tooltips, focus indicators, accessible names and automation IDs.

## 11. Keyboard and mouse support

- `Ctrl+R`: refresh all.
- `Ctrl+K`: global player search.
- `Ctrl+1…9`: major-page navigation.
- `Ctrl+Tab`: advance work areas.
- `F5`: refresh current page.
- `Alt+Left`: back.
- `Enter`: open selected item.
- `Space`: toggle selection where appropriate.
- `Delete`: destructive action only after confirmation.
- `Ctrl+Shift+A`: Agent tab.

Do not override standard terminal shortcuts while terminal focus is active. Persist window position/size, league, page, sorting/filtering, theme and optional terminal settings.

## 12. Embedded Codex Luna terminal

Treat terminal embedding as an early technical spike.

Use Windows ConPTY and a maintained VT renderer compatible with WinUI 3. If no renderer passes the spike, use WebView2 plus xterm.js only for terminal rendering while retaining process control and application logic in C#. Support resizing, Unicode, ANSI colors, selection, copy/paste and keyboard input. Use Cascadia Mono or Cascadia Code.

Launch Codex in the repository root:

```powershell
codex --model gpt-5.6-luna --cd "<repository-root>"
```

Verify Codex installation, permit normal Codex sign-in without capturing its credentials, start the local MFL tool server first, and ensure the repository is trusted. Consult the current [Codex CLI reference](https://developers.openai.com/codex/developer-commands).

## 13. Agent-friendly MFL access

Do not make the agent construct raw `curl` commands. Create a local C# MCP server with strongly typed tools:

```text
mfl_list_leagues
mfl_select_league
mfl_refresh_all
mfl_get_dashboard
mfl_get_league_settings
mfl_get_standings
mfl_get_matchups
mfl_get_roster
mfl_get_lineup
mfl_search_players
mfl_get_free_agents
mfl_set_lineup
mfl_add_drop
mfl_submit_waiver_claims
mfl_submit_blind_bids
mfl_move_to_ir
mfl_move_from_ir
mfl_promote_taxi_player
mfl_demote_taxi_player
mfl_get_draft_state
mfl_set_draft_queue
mfl_submit_draft_pick
mfl_get_pending_trades
mfl_propose_trade
mfl_respond_to_trade
mfl_get_capabilities
```

The server must:

- Use a local named pipe or authenticated loopback connection.
- Never reveal MFL passwords or cookies to the model.
- Reuse `IMflMutationCoordinator`.
- Return structured results.
- Require explicit league context.
- Validate parameters.
- Offer preview/dry-run modes for complex mutations.
- Trigger full refresh after successful mutations.
- Notify the desktop application of mutation and refresh events.

Do not expose unrestricted arbitrary HTTP mutations. Provide a companion CLI using the same services, such as:

```powershell
mfl-agent leagues
mfl-agent roster
mfl-agent players search "Amon-Ra"
mfl-agent draft queue add <player-id>
mfl-agent draft pick <player-id>
mfl-agent refresh
```

## 14. Repository agent instructions

Create a root `AGENTS.md` documenting purpose, architecture, build/test commands, authentication, secret-handling rules, league selection, MCP tools, mutation safety, refresh-after-mutation behavior, roster versus weekly lineup semantics, live-draft validation, typed-tool usage, confirmation rules, WinUI skill usage, and relevant documentation.

Create `docs/agent-playbook.md` with examples. For “Start Gibbs over Montgomery this week,” the agent should read league/week, fetch roster/lineup, resolve IDs, validate requirements, preview, obtain confirmation where configured, set the lineup, await refresh, fetch the refreshed lineup, and report MFL’s verified state.

## 15. Security and mutation safety

- Reads require no confirmation.
- Draft picks, add/drops, waiver submissions, trade actions and commissioner actions require confirmation by default.
- Users may disable individual confirmation categories in Settings; the agent cannot disable its own policy.
- Show the exact mutation before confirmation.
- Journal sanitized mutations without secrets.
- Record timestamp, league, franchise, operation, sanitized request, result and verification status.
- Use `ContentDialog` for destructive or irreversible operations.
- Agent access is available only while the app or local broker is running.

## 16. Testing strategy

### Unit tests

Test login XML parsing, single-object-or-array conversion, player/franchise resolution, draft/lineup validation, waiver serialization, trade assets, cookie redaction, capabilities and refresh state reduction.

### Contract tests

Use sanitized MFL fixtures covering empty collections, singleton objects, private-league errors, owner/commissioner responses, expired cookies, rate limiting, mutation rejection within HTTP 200, and league-host changes.

### Integration tests

Use a disposable MFL test league when possible. Never mutate a production league unless an explicit opt-in environment variable is set.

### UI tests

Cover login, league selection, refresh and partial failure, player filtering, lineups, waivers, draft queue, confirmations, keyboard navigation, all themes, terminal lifecycle, and agent mutations causing visible refresh.

## 17. Delivery phases

### Phase 0: research and spikes

- Install WinDev skills.
- Build capability matrix.
- Prove authentication/discovery, one export and a harmless test mutation.
- Prove ConPTY/Codex terminal and MCP-to-app IPC.
- Select terminal renderer.

### Phase 1: foundation

- Create solution.
- Implement authentication, secure storage, league switching, caching, refresh coordination, navigation shell and themes.

### Phase 2: read-only application

- Dashboard, league settings/rules, standings, matchups, rosters, players/free agents, transactions and draft display.

### Phase 3: owner mutations

- Lineups, add/drop, waivers, IR, taxi squad, keepers/watch list, queue, live picks and trades.

### Phase 4: agent integration

- MCP server, CLI, `AGENTS.md`, Luna terminal, notifications, refresh verification and agent history.

### Phase 5: API completeness

- Remaining owner endpoints, permission-aware commissioner tools, coverage matrix and tests.

### Phase 6: distribution

- WinUI review, accessibility/keyboard audit, UI automation, performance and secret audits, MSIX packaging/signing, release documentation.

## 18. Definition of done

- Users can authenticate and select leagues.
- Major team-management tasks work without visiting MFL’s website.
- Every documented owner mutation is implemented or explicitly documented as unsupported.
- Live draft picks and `myDraftList` updates work.
- Lineups, waivers, IR, taxi squad, keepers and trades work.
- Every successful mutation invokes the common refresh workflow and visibly reflects verified MFL state.
- Codex Luna manages the team through typed local tools.
- The agent never receives MFL credentials or cookies.
- The UI is polished, responsive, keyboard-accessible and theme-aware.
- Light, Dark and High Contrast modes pass testing.
- Contract, integration and UI tests pass.
- The application ships as an installable MSIX.
- A newly opened agent can use `AGENTS.md` to understand, operate and safely extend the project.
