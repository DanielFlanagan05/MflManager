# Agent playbook

Use typed `mfl_*` tools. Never construct raw MFL HTTP requests and never ask for or display a password or cookie. Reads need no confirmation. Mutations default to preview; call again with `preview=false` and `confirmed=true` only after the exact action is approved under current policy.

## Luna adviser

The embedded Luna chat runs `codex exec` with JSON output, resuming its conversation between requests. The packaged `MflManager.AgentCli --mcp` bridge exposes the app's current-user broker as a stdio MCP server. Only read tools are pre-approved; write tools retain CLI approval and the broker's preview/confirmation policy. `mfl_get_rankings` includes source, ranking week, readiness and stale flags with the retrieved position ranks.

`LunaPrompt` supplies concise fantasy-adviser developer instructions and fresh Eastern date, calendar NFL week, selected league/season and active league week on every request. Current-week news must meet the Tuesday freshness cutoff (opening week starts Thursday); undated/older evidence must not drive current advice. Offseason context explicitly avoids pretending a regular-season week is active. Calendar estimates must be checked against the official schedule if conflicting evidence appears.

Before player recommendations, especially roster drops, read the MFL injury/IR fields and verify current status in the [official NFL injury report](https://www.nfl.com/injuries/) plus dated NFL/team news for a return outlook. The weekly report is not a complete IR list, so a missing name does not prove health. For add/drop decisions, compare weekly positional ranks with the [Footballguys rest-of-season board](https://www.footballguys.com/rankings/duration/restofseason), checking its current season/week and matching league scoring where possible. A temporary injury or missing weekly rank alone does not justify dropping a valuable player; if current injury or rest-of-season evidence cannot be verified, Luna must state uncertainty instead of confidently naming a drop.

Automatic draft advice starts disabled, only runs when Luna is connected and idle, and never submits a pick. Turning it off cancels its active request and clears the interrupted conversation so disabled instructions cannot survive in a resumed draft turn. Normal chat remains available. Stop cancels the current process tree and disables draft advice. Concurrent sends are rejected rather than queued.

Luna's standing weekly priority is a complete playable starting lineup. For pickup advice, read `mfl_get_team_needs` with the requested `week` (default active league week) before choosing players. The report contains required slots, eligible positions, submitted starters, owned alternatives, all roster status fields, combined availability codes (including `BYE`), the NFL-team bye map, and freshness flags. `mfl_get_roster` also includes the requested week's availability status, bye week, unavailable flag and status risk, while preserving player/roster/salary/contract fields. Empty slots and unavailable starters take priority; questionable/doubtful players need backups. Use owned bench replacements first, then position-specific free-agent searches and weekly rankings. Do not double-count a FLEX player, recommend an unavailable or bye-week replacement, or drop a valuable injured hold solely to fix a temporary gap. A requested week's submitted lineup is explicitly unknown when only another week's lineup is cached. Refresh stale reads and disclose missing data. Filling slots is an advice objective; it does not authorize lineup submissions, pickups or drops.

## Start Gibbs over Montgomery this week

1. Call `mfl_list_leagues`; if needed call `mfl_select_league` with league id, season, host, and franchise.
2. Call `mfl_get_dashboard`, `mfl_get_roster`, and `mfl_get_lineup` with the explicit league id. Determine the current scoring week from refreshed league state.
3. Call `mfl_search_players` for Gibbs and Montgomery and resolve unambiguous MFL ids. Verify both are on the user's roster and that Gibbs is eligible for the vacated lineup slot.
4. Call `mfl_set_lineup` with the complete desired starters/tiebreakers and `preview=true`. Report the exact substitution and any warning.
5. Obtain confirmation if the user's lineup policy requires it. Call again with `preview=false` and the appropriate `confirmed` value.
6. Wait for the mutation receipt. It distinguishes MFL acceptance from refresh and verification.
7. Call `mfl_get_lineup` again and report the refreshed MFL state. If accepted but refresh failed, say exactly that and do not resubmit.

## Add/drop

Read roster and free agents, resolve ids, validate roster limits, then verify the proposed drop's injury status, expected absence, healthy role and current rest-of-season value against the add candidate. Consider IR eligibility and long-term hold value before naming a drop. Call `mfl_add_drop` in preview mode. The preview must name both players. After confirmation, execute once, wait for full refresh, then fetch roster and free agents to verify both sides.

## Waivers

Read pending waivers and free agents. Build the entire ordered claim set, including conditional alternatives and drop ids. For FAAB include each exact bid and whether the submission appends or replaces. Preview the complete set, confirm once, execute once, refresh, and read pending waivers.

## Live draft

### Queue assistant

When asked to fill or suggest a draft queue, read draft state, roster needs, available players, and cached Fantasy Footballers positional rankings first. PositionRank is comparable only within its own position; never manufacture a cross-position overall rank. Default to five players unless another count is requested. Honor position priorities and liked players; disliked players are hard exclusions. Remove drafted/unavailable players, preserve order, explain the choices briefly, and present a sanitized preview before calling `mfl_set_draft_queue`. Queue updates remain subject to confirmation. Never submit a live pick unless explicitly asked.

Call `mfl_get_draft_state` immediately before preview and again internally at execution. Confirm player name/team/position, round, pick, overall pick, and franchise. Use `mfl_submit_draft_pick`; never import `draftResults`. If the draft advanced, abandon the stale request and start again. A successful pick with failed verification must not be retried automatically.

## Trades

Read pending trades and relevant rosters/picks. Represent players, current/future picks, and BBID dollars explicitly. Preview the full give/receive sides and recipient. For response, identify trade id and exact Accept/Reject/Revoke action. Confirm, execute once, refresh, and re-read pending trades and rosters.
