# MFL Manager agent instructions

## Purpose and architecture

This repository is a .NET 10/WinUI 3 desktop client for MyFantasyLeague. Keep dependencies flowing inward: `Domain` has records and rules; `Application` has interfaces and orchestration contracts; `MflApi` owns HTTP/XML/JSON behavior; `Persistence` owns SQLite; `AgentServer` owns the current-user named-pipe MCP surface; `AgentCli` is a thin pipe client; `Terminal` owns the Codex process; `App` owns WinUI and Credential Locker. Never call MFL directly from a view model.

## Commands

```powershell
dotnet restore MflManager.slnx
dotnet build MflManager.slnx -c Debug
dotnet test MflManager.slnx -c Debug --no-build
```

Use `winapp run src/MflManager.App/MflManager.App.csproj` for packaged launch, `winapp find-ui` or the installed `winui-design` catalogue before major XAML work, `winui-code-review` before a milestone, `winui-ui-testing` for UI automation, and `winui-packaging` for MSIX work. Never run the packaged executable directly. Target x64, not AnyCPU.

## Authentication and secrets

MFL login is HTTPS POST to `/{season}/login` with `USERNAME`, `PASSWORD`, and `XML=1`. Authorization is the `MFL_USER_ID` cookie. Never put credentials or cookies in a URL, log, exception, SQLite row, journal, test fixture, tool result, or Codex prompt. For local testing, credentials may be loaded from a user-managed file outside source control; persist the cookie through Windows Credential Locker. API keys are read-only and cannot authorize mutations. Sign-out clears memory and Credential Locker; “forget” also removes saved account metadata.

## League context

Call `myleagues` after authentication. Select by league id, season, host, franchise id, and commissioner flag. Do not assume every league uses the same host. Do not hardcode the current year. Historical `SeasonContext` values are read-only. Every agent call after selection must repeat `league_id`; reject a mismatch.

## Safe mutation workflow

All UI, CLI, and agent mutations use `IMflMutationCoordinator`:

1. Read fresh state and validate authorization and league rules.
2. Build and show a sanitized `MutationPreview`.
3. Obtain confirmation for draft picks, add/drop, waivers, trade actions, and commissioner actions unless the user disabled that category in Settings.
4. Submit once. Never automatically retry a mutation.
5. Parse the MFL body; HTTP 200 alone is not success.
6. Append a sanitized journal entry.
7. Call the common `RefreshAllAsync` workflow.
8. Read refreshed state and report accepted/refreshed/verified as distinct outcomes.

Reads may retry with bounded backoff. Mutations are serialized per league. Preserve last-good cached sections on partial refresh failures and mark them stale.

## Roster and draft semantics

A roster is season ownership and status (active, IR, taxi). A weekly lineup is the starters, bench, tiebreakers, and backups for one week. Never treat a roster update as a lineup submission.

For a live pick, fetch draft state immediately before submitting. The on-the-clock franchise, round, and within-round pick must match. Submit to `/{season}/live_draft` with `CMD=DRAFT`, `PLAYER_PICK`, `ROUND`, `PICK`, and `JSON=1`. Commissioner picks also use `FRANCHISE_PICK`. Use `import?TYPE=myDraftList` with comma-separated `PLAYERS` for a personal queue. Never use `draftResults` to submit a live pick.

## Typed tools

Use the local named-pipe MCP tools rather than raw curl: `mfl_list_leagues`, `mfl_select_league`, `mfl_refresh_all`, `mfl_get_dashboard`, `mfl_get_league_settings`, `mfl_get_standings`, `mfl_get_matchups`, `mfl_get_roster`, `mfl_get_lineup`, `mfl_search_players`, `mfl_get_free_agents`, `mfl_set_lineup`, `mfl_add_drop`, `mfl_submit_waiver_claims`, `mfl_submit_blind_bids`, `mfl_move_to_ir`, `mfl_move_from_ir`, `mfl_promote_taxi_player`, `mfl_demote_taxi_player`, `mfl_get_draft_state`, `mfl_set_draft_queue`, `mfl_submit_draft_pick`, `mfl_get_pending_trades`, `mfl_propose_trade`, `mfl_respond_to_trade`, and `mfl_get_capabilities`.

Complex mutation tools default to preview. A subsequent call must set `preview=false` and `confirmed=true` when confirmation is required. The agent cannot change confirmation policy. Never add an arbitrary HTTP mutation tool.

## References

Read `docs/mfl-api-reference.md`, `docs/mfl-capability-matrix.md`, `docs/agent-playbook.md`, `docs/security.md`, and `docs/testing.md` before changing their respective areas. The official reference is `https://api.myfantasyleague.com/{season}/api_info?STATE=details`.
