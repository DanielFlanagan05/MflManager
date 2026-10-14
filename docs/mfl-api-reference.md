# MFL API implementation reference

Verified against the official 2026 `api_info` and `api_info?STATE=details` pages on 2026-09-23.

## Transport

- Login: HTTPS `POST https://api.myfantasyleague.com/{season}/login` with form fields `USERNAME`, `PASSWORD`, `XML=1`.
- Reads: `GET https://{league-host}/{season}/export?TYPE={type}&L={league}&JSON=1`.
- Mutations: HTTPS POST to `https://{league-host}/{season}/import` with `TYPE`, `L`, endpoint fields, and `JSON=1`.
- Live draft: HTTPS POST to `https://{league-host}/{season}/live_draft`.
- Authentication: `MFL_USER_ID` cookie. API keys are read-only.
- MFL can encode zero, one, or many elements differently. Parsers must tolerate an object or array.
- A successful status code is not sufficient for a mutation; parse JSON/XML error/status content.

## Core reads used by refresh

The Transactions tab displays completed `FREE_AGENT`, `WAIVER`, and `BBID_WAIVER` player moves from `transactions`, newest first. MFL timestamps are Unix seconds. Free-agent/waiver payloads encode `added-player-ids|dropped-player-ids`; blind-bid payloads encode `added-player-ids|bid-amount|dropped-player-ids`, with comma-separated player ids. Team and player names come from the cached league metadata and player catalog. Missing bids remain unavailable rather than becoming zero; pending request types are excluded.

The official API also provides `export TYPE=messageBoard` (league `L`, optional `COUNT`, default ten threads), `export TYPE=messageBoardThread` (`L` and required `THREAD`), and `import TYPE=messageBoard` for new threads or replies. These require league-owner access. Endpoint availability was checked against the official 2026 details page on 2026-10-08; the Messages UI remains unchanged.

`pendingWaivers` returns the current franchise's unprocessed claims, including comma-separated `player_bid_drop` values in `blindBidWaiverRequest.addsDrops`. It does not expose other owners' bids through the current-franchise request. Commissioners can explicitly request a franchise with `FRANCHISE_ID`; the UI does not enumerate other franchises' private bids. Verified against the official reference on 2026-09-16.

Refresh restores the selected league's SQLite snapshot first, then prioritizes the season's static `mfl_status.json`, player catalog when missing and free agents. The authenticated `calendar` export supplies waiver timing when it exposes an unambiguous next occurrence; season-long recurring window bounds are shown as unavailable rather than mislabeled as the next processing date. Fantasy Footballers positional ranking extraction runs independently in parallel. Roster, standings and pending claims are persisted and published incrementally. Tab navigation does not refresh the available-player/ranking feeds. Dated CSVs and validated metadata are stored in the local app cache; each position is scraped at most once per local calendar date, with stale last-good rankings preserved on failures. There is no overall external ranking.

The Waivers tab reads MFL's `playerScores` with `W=YTD`, `STATUS=freeagent`, and `RULES=1` for league-scored season totals; it does not need an external score provider or API key. The top bar's FAAB balance comes from the selected franchise's `bbidAvailableBalance` in `league`, fetched on startup refresh and again when the Waivers tab opens. New and edited blind bids reject missing balance and amounts above the current balance, and both the UI and coordinator enforce whole-dollar input and fresh balance checks.

Independent refresh exports are asynchronous. The MFL transport spaces request starts by 1.1 seconds to avoid bursts, but it does not hold the rate-limit gate while waiting for a response, so slow in-flight reads may overlap. The player catalog (when required), free agents, league settings, and the remaining section reads are queued in priority order. Weekly lineup resolution remains dependent on the refreshed week and roster, but reuses the full refresh's `weeklyResults` body instead of issuing another request when that response contains the selected franchise's starters. The resulting per-player scores are cached in the league snapshot so opening My Team does not repeat the same current-week read. Each response log records its start time, total elapsed time, network time, and scheduling wait; refresh completion logs the five slowest section tasks without recording response bodies.

The default weekly lineup uses `mfl_status.weeks.CurrentWeek` while that week is still in progress (`CurrentWeek > CompletedWeek`), then advances to `LineupWeek`. This distinction matters during live games because MFL may advertise the next editable `LineupWeek` before the current scoring week has completed.

My Team weekly reads always specify `W` and validate the response's explicit `weeklyResults.week` against the selected week. A missing or mismatched week, malformed response or failed read triggers one additional read after a short backoff; if it still cannot be validated, the lineup service throws `WeeklyPointsValidationException`. The UI shows the error and unavailable points. Full refresh validates its reused weekly body through the same service, persists other refreshed sections and preserves last-good score provenance before propagating that exception. Scores have a separate `WeeklyPlayerPointsWeek` in the snapshot and are displayed only for that exact selection; legacy snapshots lacking provenance are reloaded. Changing the dropdown clears old score rows immediately and invalidates outstanding loads for the previous selection.

If season status is unavailable, fresh league `currentWeek` is the next source. If neither API supplies a week, the current season falls back to an Eastern-time calendar: week one starts the Thursday after Labor Day and advances once each subsequent Tuesday at midnight, bounded by league start/end weeks. Cached league `currentWeek` and external ranking weeks do not suppress this fallback. Historical seasons retain their own available week context rather than following today's calendar.

Routine refresh requests only data currently consumed by the snapshot and UI: `league`, `rosters`, `leagueStandings`, `weeklyResults`, `freeAgents`, `adp`, `transactions`, `pendingWaivers`, `pendingTrades`, `calendar`, `draftResults`, `myDraftList`, `players`, and `injuries`. Supported but currently undisplayed `rules`, `keepers`, `messageBoard`, and `polls` reads are not placed in the routine queue. Complete starting lineups and each rostered player's current-week score are read from the same `weeklyResults` response for the selected week and franchise. `playerRosterStatus` remains a fallback and is also requested with player ids (`P`), week (`W`), and franchise (`F`) for player availability; its `S`/`R` values are not treated as a complete live-week lineup because carried-over starters can remain `R`. The public, season-wide `injuries` export is requested from `api.myfantasyleague.com` without `L`; MFL rejects this export when sent with league context on the league host. My Team reloads `injuries` on entry; a missing designation clears an old one, while a failed read preserves saved statuses and marks them stale. Rankings/projections and NFL schedule are refreshed on a longer stale interval.

For league-wide multi-copy player limits, `freeAgents` identifies players with at least one available copy but does not provide the quantity. When `league.playerLimitUnit` is `LEAGUE`, calculate the available quantity as `league.rostersPerPlayer` minus the number of that player's occurrences in the league-wide `rosters` response. The Waivers view caches this derived quantity with the snapshot and does not issue an additional request.

Player score parsing distinguishes a numeric zero from a missing `score`/`points` field. Missing values are omitted and displayed as unavailable rather than being synthesized as zero. One aggregate diagnostic line records selected-team player count, numeric-score count, nonzero count, total points, and available player field names without logging player identities or raw response data.

During an in-progress week, `weeklyResults` can return only player `id` and lineup `status`, without any scores. My Team then reads `liveScoring` with explicit `W` and `DETAILS=1` (which includes non-starters), selects the signed-in franchise, and parses its nested `players.player` scores. The complete starter list remains sourced from `weeklyResults`; live scoring can omit owned players. Live score provenance requires a matching explicit week and franchise, with one validation retry on failure. Injury availability remains sourced from `injuries`; live scoring's `starter`/`nonstarter` status is not an injury designation.

Full refresh reads the public season-wide `nflByeWeeks` export from `api.myfantasyleague.com` without `L`. Its `year` must match the selected season, and each `team.id` / `bye_week` pair is cached in the snapshot. Failed or malformed reads preserve the last-good map and mark the section stale. My Team compares that map against the selected lineup week and appends `BYE` after any existing status (for example `IR BYE`, `Q BYE`, or `SUS BYE`); a player with no other designation displays `BYE` alone. Changing the week recalculates the designation without another NFL schedule request.

The League Rankings page has an isolated refresh path. It requests only `mfl_status`, `leagueStandings`, and `weeklyResults` for the active scoring week. Cached league metadata supplies franchise names. Rows are sorted once by total points (`leagueStandings.pf`) in descending order; their displayed rank is assigned from that order, and the signed-in franchise is marked as My Team.

Selecting a League Rankings row opens the reusable read-only Team View. It requests `mfl_status`, `rosters`, and `weeklyResults` for the selected franchise and active scoring week. The weekly response supplies the submitted starter ids and each rostered player's current-week score; the roster response supplies active, injured-reserve, and taxi ownership. Team View uses the same slot-assignment and position ordering rules as My Team, but exposes no lineup mutation controls.

## Owner mutations

- `lineup`: weekly starters, tiebreakers/backups.
- `fcfsWaiver`: FCFS add/drop.
- `waiverRequest` and `blindBidWaiverRequest`: ordered/conditional requests and bids, append or replace.
- Canceling one pending waiver claim uses the documented round-replacement behavior: read pending claims fresh, remove the selected claim locally, then submit that round with `REPLACE=1` and all remaining `PICKS`. Removing the final claim sends an empty `PICKS` value. This is a mutation and is never retried automatically.
- Reordering pending claims replaces the entire affected round with `REPLACE=1` and `PICKS` in the dragged order. The coordinator compares fresh `pendingWaivers` with the displayed original order before submitting and verifies the refreshed order afterward. Dragging across waiver rounds is rejected.
- `ir` and `taxi_squad`: status movement.
- `keepers`, `myWatchList`, `myDraftList`, and `tradeBait`: list management.
- `tradeProposal` and `tradeResponse`: propose/accept/reject/revoke with players, picks, and BBID dollars where league rules permit.
- `messageBoard`, `pollVote`, `chat_save`, `poolPicks`, and `survivorPoolPick`: communication and pool participation.

## Live draft

Submit `L`, `CMD=DRAFT`, `PLAYER_PICK`, `ROUND`, `PICK`, and `JSON=1`; a commissioner also supplies `FRANCHISE_PICK`. The round, pick, and franchise must equal fresh on-the-clock data. Commissioner-only commands are `PAUSE`, `RESUME`, `SKIP`, and `UNDO`. A personal queue uses `import TYPE=myDraftList` with comma-separated `PLAYERS`; `draftResults` is read/offline-import semantics, not the live-pick endpoint.

## Host and season rules

`myleagues` is the source of league id, season, franchise, commissioner state, and league host. Never derive a host from another league. Historical seasons remain available for reads but the application rejects mutations before transport.
