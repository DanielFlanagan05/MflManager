# MFL API implementation reference

Verified against the official 2026 `api_info` and `api_info?STATE=details` pages on 2026-09-01.

## Transport

- Login: HTTPS `POST https://api.myfantasyleague.com/{season}/login` with form fields `USERNAME`, `PASSWORD`, `XML=1`.
- Reads: `GET https://{league-host}/{season}/export?TYPE={type}&L={league}&JSON=1`.
- Mutations: HTTPS POST to `https://{league-host}/{season}/import` with `TYPE`, `L`, endpoint fields, and `JSON=1`.
- Live draft: HTTPS POST to `https://{league-host}/{season}/live_draft`.
- Authentication: `MFL_USER_ID` cookie. API keys are read-only.
- MFL can encode zero, one, or many elements differently. Parsers must tolerate an object or array.
- A successful status code is not sufficient for a mutation; parse JSON/XML error/status content.

## Core reads used by refresh

`league`, `rules`, `rosters`, `leagueStandings`, `weeklyResults`, `freeAgents`, `transactions`, `pendingWaivers`, `pendingTrades`, `draftResults`, `myDraftList`, `keepers`, `messageBoard`, `polls`, and `calendar`. Global `players`, injuries, rankings/projections, ADP/AAV, and NFL schedule are refreshed on a longer stale interval.

## Owner mutations

- `lineup`: weekly starters, tiebreakers/backups.
- `fcfsWaiver`: FCFS add/drop.
- `waiverRequest` and `blindBidWaiverRequest`: ordered/conditional requests and bids, append or replace.
- `ir` and `taxi_squad`: status movement.
- `keepers`, `myWatchList`, `myDraftList`, and `tradeBait`: list management.
- `tradeProposal` and `tradeResponse`: propose/accept/reject/revoke with players, picks, and BBID dollars where league rules permit.
- `messageBoard`, `pollVote`, `chat_save`, `poolPicks`, and `survivorPoolPick`: communication and pool participation.

## Live draft

Submit `L`, `CMD=DRAFT`, `PLAYER_PICK`, `ROUND`, `PICK`, and `JSON=1`; a commissioner also supplies `FRANCHISE_PICK`. The round, pick, and franchise must equal fresh on-the-clock data. Commissioner-only commands are `PAUSE`, `RESUME`, `SKIP`, and `UNDO`. A personal queue uses `import TYPE=myDraftList` with comma-separated `PLAYERS`; `draftResults` is read/offline-import semantics, not the live-pick endpoint.

## Host and season rules

`myleagues` is the source of league id, season, franchise, commissioner state, and league host. Never derive a host from another league. Historical seasons remain available for reads but the application rejects mutations before transport.
