# Agent playbook

Use typed `mfl_*` tools. Never construct raw MFL HTTP requests and never ask for or display a password or cookie. Reads need no confirmation. Mutations default to preview; call again with `preview=false` and `confirmed=true` only after the exact action is approved under current policy.

## Start Gibbs over Montgomery this week

1. Call `mfl_list_leagues`; if needed call `mfl_select_league` with league id, season, host, and franchise.
2. Call `mfl_get_dashboard`, `mfl_get_roster`, and `mfl_get_lineup` with the explicit league id. Determine the current scoring week from refreshed league state.
3. Call `mfl_search_players` for Gibbs and Montgomery and resolve unambiguous MFL ids. Verify both are on the user's roster and that Gibbs is eligible for the vacated lineup slot.
4. Call `mfl_set_lineup` with the complete desired starters/tiebreakers and `preview=true`. Report the exact substitution and any warning.
5. Obtain confirmation if the user's lineup policy requires it. Call again with `preview=false` and the appropriate `confirmed` value.
6. Wait for the mutation receipt. It distinguishes MFL acceptance from refresh and verification.
7. Call `mfl_get_lineup` again and report the refreshed MFL state. If accepted but refresh failed, say exactly that and do not resubmit.

## Add/drop

Read roster and free agents, resolve ids, validate roster limits, and call `mfl_add_drop` in preview mode. The preview must name both players. After confirmation, execute once, wait for full refresh, then fetch roster and free agents to verify both sides.

## Waivers

Read pending waivers and free agents. Build the entire ordered claim set, including conditional alternatives and drop ids. For FAAB include each exact bid and whether the submission appends or replaces. Preview the complete set, confirm once, execute once, refresh, and read pending waivers.

## Live draft

### Queue assistant

When asked to fill or suggest a draft queue, read draft state, roster needs, available players, and FantasyPros expert rankings first. Default to five players unless another count is requested. Honor position priorities and liked players; disliked players are hard exclusions. Remove drafted/unavailable players, preserve order, explain the choices briefly, and present a sanitized preview before calling `mfl_set_draft_queue`. Queue updates remain subject to confirmation. Never submit a live pick unless explicitly asked.

Call `mfl_get_draft_state` immediately before preview and again internally at execution. Confirm player name/team/position, round, pick, overall pick, and franchise. Use `mfl_submit_draft_pick`; never import `draftResults`. If the draft advanced, abandon the stale request and start again. A successful pick with failed verification must not be retried automatically.

## Trades

Read pending trades and relevant rosters/picks. Represent players, current/future picks, and BBID dollars explicitly. Preview the full give/receive sides and recipient. For response, identify trade id and exact Accept/Reject/Revoke action. Confirm, execute once, refresh, and re-read pending trades and rosters.
