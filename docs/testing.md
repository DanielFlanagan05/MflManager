# Testing

`LineupSelectionTests` exercises the My Team observable selectors directly: existing starters swap with slot-local options, bench selections replace one slot, empty-slot moves clear the original slot, and FLEX swaps honor position eligibility. It also verifies unique player IDs and a single bench-update callback after a swap.

`TransactionTests` covers singleton/array transaction responses, Unix timestamps, add-only and drop-only moves, comma-separated multiple drops, blind bids including zero dollars, exclusion of pending requests, resolved team/player names, and newest-first ordering.

Positional ranking tests cover all six CSV sources, required fields, daily cache claims, integrity checks, stale fallback, conservative identity matching and position-filtered ordering. Run `node --test tools/ffb-qb-rankings-extractor.test.mjs` for the shared rendered-table extractor. The ranking runner needs Node.js, Playwright and installed Edge; `MFL_MANAGER_NODE` may specify an absolute Node executable. The desktop app also discovers Node on PATH, the standard Node installation, or the installed bundled development runtime. CSVs and metadata live under the app's local `MflManager/rankings/{season}` cache (Windows redirects this under the package LocalCache for packaged launches), never the repository. Failed source attempts are not repeated that calendar day; a missing executable is a pre-launch failure, not a source scrape.

`dotnet test MflManager.slnx -c Debug` runs deterministic non-live tests. Unit tests cover singleton/array JSON, player/franchise parsing, capability visibility, draft/lineup/waiver parameter generation, and secret redaction behavior. Contract tests consume synthetic fixtures for empty/singleton collections, owner/commissioner shapes, expired sessions, 429, host changes, mutation rejection inside HTTP 200, and an all-endpoint response audit. The audit asserts HTTP 200 handling for discovery/export/draft clients, parses every response type used by refresh, and writes raw plus parsed values to `api-response-audit.log` under the contract test output directory before reading it back. Integration tests exercise SQLite snapshot/journal round trips.

For live refresh performance diagnosis, inspect the repository-local daily log. `MflExportClient` entries separate network time from scheduling wait, while the `MflRefreshService` completion entry reports total duration and the five slowest section tasks. This keeps timing diagnostics to one response line plus one refresh summary and avoids logging response bodies.

League Rankings tests cover weekly score parsing, descending overall-points ordering, displayed rank assignment, cached franchise-name mapping, and identification of the signed-in franchise. UI contracts require the scoped refresh button, four aligned columns, and the dedicated virtualized list.

Team View tests cover selected-franchise weekly lineup and player-score parsing, the scoped `mfl_status`/`rosters`/`weeklyResults` request set, shared My Team slot and bench ordering, read-only controls, rankings-row navigation, and back navigation. My Team contracts verify separate starter and bench points and status columns, and parser tests verify that its lineup request reuses the weekly response's per-player scores. Status tests cover MFL injury response parsing, compact codes, clearing an expired designation on a scoped refresh, and a stale marker when that read fails. The launcher contract prevents `run-mflmanager.ps1` from overriding the project's self-contained build default.

Weekly-results parser coverage also verifies that genuine zero scores are retained while player records without a numeric score are omitted, preventing missing fields from appearing as false zero-point results.

My Team live-score regression coverage uses synthetic responses matching the observed `weeklyResults` id/status-only shape and `liveScoring.franchise.players.player` nesting. Tests cover singleton/array shapes, selected-franchise isolation, starter/bench scores, zero and negative points, missing/non-numeric scores, explicit week validation, bounded validation retries, full-refresh persistence, and retention of the complete weekly starter list. An HTTP contract verifies league-host score requests with `W`, `JSON=1`, and `DETAILS=1`, plus public season-host injury retrieval and compact injury-code mapping.

`ByeWeekTests` covers season validation, singleton/array NFL-team bye data, malformed week values, week changes, missing team data, and `BYE` after every existing supported status. Full-refresh tests verify stale fallback, the HTTP contract checks public bye-export routing, and SQLite integration verifies cached bye weeks survive a round trip.

`PlayerStatusToneTests` verifies yellow/questionable, orange/doubtful, and red/unavailable badge classification, including red precedence for `Q BYE` and `D BYE`. My Team uses a shared rounded badge for starters and bench players, collapses empty statuses, and supplies Light/Dark/HighContrast resources. High contrast uses system colors while preserving the status text and outline.

`WeeklyPointsTests` covers wrong-week, missing-week, malformed and failed responses; a single validation retry followed by an exception; validation of reused refresh bodies; retention of verified points through the roster-status lineup fallback; legacy caches without score-week provenance; and preservation of the original cached score week after a failed refresh. SQLite integration coverage verifies score-week provenance survives persistence. Week fallback tests cover Tuesday midnight in Eastern time, season bounds, API precedence and historical seasons.

Live reads may run only when `MFL_TEST_LEAGUE`, `MFL_TEST_SEASON`, and `MFL_TEST_HOST` are set. Live mutations additionally require `MFL_ALLOW_TEST_MUTATIONS=1`; never point those variables at a production league.

WinUI automation requires Developer Mode and WinApp CLI. Build and launch through `winapp`, then run the one-process script in `tests/MflManager.UiTests/ui-tests.json`. It covers login dialog, league selection, refresh status/partial failure, player filtering, navigation, confirmations, keyboard shortcuts, Light/Dark/High Contrast, terminal lifecycle, and visible refresh after an agent mutation.

`LunaPromptTests` verifies dynamic season, Eastern date, Tuesday NFL-week rollover, freshness cutoff and offseason guidance. `tests/MflManager.UiTests/luna-ui-tests.ps1 -AppPid <pid>` exercises the signed-in Agent page, default-off draft toggle, on/off round trip, Luna startup and a live read-only chat request. This optional smoke test uses the user's existing ChatGPT sign-in and consumes model usage; it never signs out or submits MFL changes. Inspect the rendered answer as well as the connection state: a completed turn alone does not prove tool access.

`AgentCliTests` verifies denied named-pipe access, disconnected brokers, timeouts, EOF and malformed responses return sanitized errors instead of unhandled exceptions. MCP errors preserve request ids and the bridge processes the next request without retrying the failed call. Malformed input returns a JSON-RPC parse error, notifications do not connect, and invalid numeric CLI arguments return a usage error.

`TeamNeedsTests` verifies empty and bye-affected slots, questionable starters, FLEX bench eligibility, exclusion of unavailable/IR/taxi replacements, requested-week status changes, unknown versus empty lineups, freshness flags, enriched typed roster output, read-only tool metadata, and league/week validation. Luna prompt coverage checks the playable-lineup priority, needs-first pickup workflow, backup planning, bye checking, and advice-only mutation boundary.

Before release:

```powershell
dotnet restore MflManager.slnx
dotnet build MflManager.slnx -c Release
dotnet test MflManager.slnx -c Release --no-build
dotnet list MflManager.slnx package --vulnerable --include-transitive
```

Run `winui-code-review`, `winui-ui-testing`, and `winui-packaging`; inspect the produced MSIX and run the secret audit described in `docs/security.md`.
