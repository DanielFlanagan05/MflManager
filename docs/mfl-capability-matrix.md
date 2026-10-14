# MFL capability matrix

Status reflects this repository on 2026-09-01. “Typed” means the shared client/orchestrator supports the request and the UI and/or named-pipe tool has a typed route. Feature availability is still gated by league configuration and account permissions.

| Category | Request | Kind | Access | Required/important parameters | Format | UI | Agent tool | Status | Contract |
|---|---|---|---|---|---|---|---|---|---|
| Discovery | `myleagues` | read | user | season cookie | JSON/XML | league picker | `mfl_list_leagues`, `mfl_select_league` | typed | yes |
| League | `league` | read | owner | L | JSON/XML | Dashboard, League | `mfl_get_league_settings` | typed | yes |
| League | `rules`, `allRules` | read | owner/public | L for rules | JSON/XML | League | `mfl_get_league_settings` | typed | fixture |
| League | `rosters` | read | owner | L, optional W/FRANCHISE | JSON/XML | My Team | `mfl_get_roster` | typed | yes |
| Team advice | cached roster, lineup rules, injuries and `nflByeWeeks` | read | owner/public | explicit league_id, optional week | JSON | Luna | `mfl_get_team_needs`; week-aware `mfl_get_roster` | typed, slots/statuses/bench alternatives/freshness | unit/tool contract |
| League | `playerRosterStatus` | read | owner | L, P, optional W/F | JSON/XML | My Team | `mfl_get_lineup` | typed | yes |
| League | `freeAgents` | read | owner | L, optional POSITION | JSON/XML | Players | `mfl_get_free_agents` | typed | yes |
| League | `schedule`, `weeklyResults` | read | owner | L, optional W/F | JSON/XML | Matchups | `mfl_get_matchups` | typed | fixture |
| League | `leagueStandings` | read | owner | L | JSON/XML | Standings | `mfl_get_standings` | typed | yes |
| League | `calendar`, `playoffBrackets`, `playoffBracket` | read | owner | L | JSON/XML | League | — | generic typed read | fixture |
| Transactions | `transactions` | read | owner | L, W/TRANS_TYPE/FRANCHISE/DAYS/COUNT | JSON/XML | League | dashboard | typed | yes |
| Transactions | `pendingWaivers` | read | owner | L, optional FRANCHISE_ID | JSON/XML | Waivers | dashboard | typed | fixture |
| Transactions | `pendingTrades` | read | owner | L, optional FRANCHISE_ID | JSON/XML | Trades | `mfl_get_pending_trades` | typed | fixture |
| Transactions | `tradeBait` | read | owner | L | JSON/XML | Trades | — | typed capability | fixture |
| Players | `players` | read | public | optional PLAYERS/SINCE/DETAILS | JSON/XML | Players | `mfl_search_players` | typed/cache | yes |
| Players | `injuries`, `playerRanks`, `projectedScores` | read | public/owner | week/position variants | JSON/XML | My Team status; Players | search result enrichment | typed capability | fixture |
| Players | `adp`, `aav`, `topAdds`, `topDrops`, `topOwns`, `topStarters` | read | public | period/scoring/team count | JSON/XML | Players | search result enrichment | typed capability | fixture |
| NFL | `nflSchedule`, `mfl_status`, `nfl_sched` | read | public | season/week | JSON/XML/static | Matchups | dashboard | typed capability | fixture |
| Draft | `draftResults`, `auctionResults` | read | owner | L | JSON/XML | Draft | `mfl_get_draft_state` | typed | yes |
| Draft | `myDraftList` | read | owner | L | JSON/XML | Draft | `mfl_get_draft_state` | typed | fixture |
| Draft | `live_draft` DRAFT | mutation | owner | L, PLAYER_PICK, ROUND, PICK | JSON/XML | Draft | `mfl_submit_draft_pick` | typed + fresh validation | rejected-200 fixture |
| Draft | `myDraftList` | mutation | owner | L, PLAYERS | JSON/XML | Draft | `mfl_set_draft_queue` | typed | serialization |
| Draft | `live_draft` PAUSE/RESUME/SKIP/UNDO | mutation | commissioner | L, CMD | JSON/XML | Draft (permission gated) | capability only | typed generic | authorization fixture |
| Team | `lineup` | mutation | owner | L, W, STARTERS; optional tiebreakers | JSON/XML | My Team | `mfl_set_lineup` | typed | validator |
| Team | `fcfsWaiver` | mutation | owner | L, ADD and/or DROP | JSON/XML | Players | `mfl_add_drop` | typed | rejected-200 fixture |
| Team | `waiverRequest` | mutation | owner | L, ROUND, PICKS, optional REPLACE | JSON/XML | Waivers | `mfl_submit_waiver_claims` | typed | serializer |
| Team | `blindBidWaiverRequest` | mutation | owner | L, PICKS, optional ROUND/REPLACE | JSON/XML | Waivers | `mfl_submit_blind_bids` | typed | serializer |
| Team | `ir` | mutation | owner | L, ACTIVATE/DEACTIVATE/DROP | JSON/XML | My Team | `mfl_move_to_ir`, `mfl_move_from_ir` | typed | capability |
| Team | `taxi_squad` | mutation | owner | L, PROMOTE/DEMOTE/DROP | JSON/XML | My Team | `mfl_promote_taxi_player`, `mfl_demote_taxi_player` | typed | capability |
| Team | `keepers` | read/mutation | owner | L, KEEP | JSON/XML | My Team | capability | typed | fixture |
| Team | `myWatchList` | read/mutation | owner | L, ADD/REMOVE | JSON/XML | Players | capability | typed | fixture |
| Trades | `tradeProposal` | mutation | owner | offered-to and asset fields | JSON/XML | Trades | `mfl_propose_trade` | typed generic assets | preview test |
| Trades | `tradeResponse` | mutation | owner | TRADE_ID, RESPONSE | JSON/XML | Trades | `mfl_respond_to_trade` | typed | enum test |
| Trades | `tradeBait` | mutation | owner | list/comment fields | JSON/XML | Trades | capability | typed generic | fixture |
| Communications | `messageBoard`, `polls` | read | owner | L | JSON/XML | Messages | dashboard | typed refresh | empty/singleton fixture |
| Communications | `messageBoard`, `pollVote`, `emailMessage`, `chat_save` | mutation | owner | endpoint fields | JSON/XML | Messages | capability | generic typed/permission gated | fixture |
| Pools | `poolPicks`, `survivorPoolPick` | mutation | owner | picks/week; commissioner FRANCHISE_ID | JSON/XML | League | capability | typed generic | fixture |
| Accounting | `accounting` | read/mutation | commissioner | L, franchise/amount/description | JSON/XML | League | capability | permission gated | authorization fixture |
| Commissioner | `salaries`, `salaryAdj`, `playerScoreAdjustment`, `franchiseScoreAdjustment` | mutation | commissioner | documented data/ids/points | JSON/XML | League | capability | typed generic, hidden otherwise | authorization fixture |

Unsupported league features are represented by capability absence or a stale/not-applicable section and must be disabled with an explanation. The `tools/refresh-api-reference.ps1` script extracts the current official request names so drift can be reviewed deliberately.
