# Luna recommendation plan

Research and repository review: 2026-10-01. This is an implementation plan, not a change to recommendation behavior.

## Goal

Help a manager maximize the chance of winning under the selected league's rules and time horizon. A recommendation should compare the proposed action with holding the current roster and the best available alternatives. Weekly positional rank remains one input, not the decision rule. Show the reason, the main uncertainty, and the evidence date for every material claim.

## Current baseline

`LunaPrompt` already asks for injury verification and Footballguys rest-of-season (ROS) comparison before add/drop advice. The app's structured player feed, however, is Fantasy Footballers weekly **position** ranks, plus MFL roster/league state and ADP. `mfl_get_rankings` exposes source, week, readiness, and staleness. The agent can search the web, but there is no normalized multiweek opportunity, future value, or FAAB market context. `mfl_get_dashboard` does not expose the stored FAAB balance or transaction history; the typed tool registry has no read tool for either. See `LunaPrompt.cs`, `MflToolRegistry.cs`, `MflRefreshService.cs`, and `LeagueModels.cs`.

The existing Footballguys check is a useful second opinion, but browser availability and reuse rights need review before it becomes a background ingested feed. Missing or stale ROS evidence should lower confidence, not turn a weekly rank into a long-term valuation.

## Decision contract

1. **Recognize league format.** Parse scoring, starters/flex, bench/IR/taxi, waiver and FAAB rules, playoffs, keeper/dynasty rules, contracts and salary where applicable. Ask for missing strategic settings only when they change the recommendation. Let the user set risk preference and whether the team is contending, balanced, or rebuilding; suggest a stance from standings and roster but let the user override it. Historical seasons remain read-only.
2. **Compare complete alternatives.** For a start/sit, compare eligible lineup configurations. For a waiver, compare add/drop pairs, IR or taxi use, and holding. For a trade, compare both rosters after the trade, lost/received picks or bid dollars, and replacement value. For a draft, compare players across positions using league-scored projected value over replacement, rather than comparing positional rank numbers.
3. **Separate horizons.** Report this week, ROS, and future seasons only where league rules make them relevant. In redraft, “long term” means ROS and playoffs; in keeper/dynasty, include future seasons and pick value. Never let dynasty market value silently override this season's win probability for a contender.
4. **Represent upside and downside.** Model at least a median/base case, a plausible ceiling, and a downside case. Inputs include playing time and opportunity trend (snap share, routes, targets, carries, goal-line/red-zone work, air yards), role changes, historical consistency, depth-chart competition, injury/return uncertainty, and remaining schedule. Treat small samples and coach reports as uncertain. Do not equate low recent touchdowns with a guaranteed rebound.
5. **Use utility, not an opaque universal score.** Estimate incremental starter points or wins over the relevant weeks, adjusted for playoff timing, roster depth, bye coverage, opportunity cost, and user risk preference. A strong favorite may prefer a steadier starter; an underdog or rebuilding team may prefer an upside stash. Show the factors rather than claiming a precise win probability until the model is calibrated.
6. **Price bids in the league's economy.** For FAAB, use current available balance, initial/remaining budget, minimum and increment, waiver deadline, weeks left, positional need, alternatives, and the league's *completed* winning claims if those amounts are visible. Produce a bid range and a recommended amount, with a reason and a maximum walk-away price. Express it as dollars and percent of remaining budget. Never interpret preseason auction AAV as a FAAB bid. MFL `pendingWaivers` does not reveal other owners' sealed bids; do not pretend to know the winning amount in advance.
7. **Expose evidence and uncertainty.** Every numeric input carries source, season/week, retrieval time, scoring format, identity match confidence, and stale/available state. A recommendation names its key assumptions and what would change it. If player identity, current injury designation, league scoring, or bid balance is unresolved, narrow the claim or abstain.

## Data sources and priority

| Priority | Source | Use | Integration decision |
| --- | --- | --- | --- |
| 1 | Existing typed MFL reads: league/rules, rosters, free agents, standings, matchups, draft state, ADP, transactions, FAAB balance | Authoritative league context, ownership, eligibility, budget and team need | Extend sanitized read-only tools. Expose transaction amounts only if returned and permitted; never infer hidden bids. Check the current MFL `api_info` contract before adding `projectedScores`, `playerRanks`, `injuries`, `aav`, `topAdds` or `topDrops`. |
| 1 | Existing weekly Fantasy Footballers ranks | Current-week expert consensus within position | Keep as one signal. Preserve source/week/stale metadata. Do not infer overall rank or projection points. |
| 1 | Official NFL injury report, plus dated NFL/team reports | Practice/game designation and return information | Verify close to the decision deadline. The weekly injury report is not a complete list of IR players. Use MFL roster status and dated team reports for longer absences. |
| 2 | nflverse player stats, snap counts, schedules, rosters and injury data | Multiweek workload, consistency, usage trends and team context | Pilot a read-only cache with source timestamps and player-ID mapping. The nflreadr player-stats dictionary includes target share and air-yards measures; its release schedule documents update cadence. Validate coverage and dataset-specific terms before distributing. |
| 2 | ffopportunity expected fantasy points | Opportunity-based baseline that reduces reliance on recent TDs | Pilot against actual next-week/ROS outcomes; treat model version and data freshness as first-class fields. Check that weekly current-season data is available before relying on it. |
| 2 | Footballguys ROS board | External ROS projection/rank cross-check | Keep live, cited, scoring-matched use where accessible. Check licensing before caching or republishing. Never assume a page without an API is a stable machine feed. |
| 3 | MFL ADP/AAV and completed league transactions | Market context for drafts, auctions and FAAB | ADP/AAV are preseason market references, not player utility or waiver prices. League winning bids are more relevant to FAAB, if exposed. |
| 3, only for keeper/dynasty | DynastyProcess player/pick values | External market benchmark for future value | Optional and clearly labeled as market sentiment. Review GPL and underlying source rights before bundling/redistributing; omit for redraft. |

Research references: [MFL official API details](https://api.myfantasyleague.com/2026/api_info?STATE=details) (the app's existing reference was verified 2026-09-23; the live page was inaccessible to the research browser), [NFL injury report](https://www.nfl.com/injuries/), [nflverse data and release cadence](https://github.com/nflverse/nflverse-data), [nflreadr player-stat fields](https://nflreadr.nflverse.com/articles/dictionary_player_stats.html), [nflreadr injury fields](https://nflreadr.nflverse.com/articles/dictionary_injuries.html), [ffopportunity feed](https://nflreadr.nflverse.com/reference/load_ff_opportunity.html), [Footballguys ROS board](https://www.footballguys.com/rankings/duration/restofseason), [DynastyProcess data](https://github.com/DynastyProcess/data). The nflverse package notes that NFL data may be governed by the original owners' terms; review each dataset before shipping it.

## Architecture

1. Add source-specific read clients in `MflApi` and cached normalized evidence in `Persistence`. Keep league/scoring context and immutable recommendation facts in `Domain`/`Application`. The UI does not fetch MFL directly.
2. Introduce a `RecommendationContext` assembled in `Application`: league rules; team objective; candidate and replacement players; weekly/ROS/future evidence; FAAB economics; timestamps and source references. Join external player IDs to MFL IDs conservatively using a reviewed crosswalk; ambiguous names are excluded, not guessed.
3. Expose narrow read-only named-pipe tools such as `mfl_get_player_outlook`, `mfl_get_waiver_market`, and `mfl_compare_options`, each requiring `league_id`. Return sanitized derived facts and bounded candidate sets, not entire raw feeds or protected owner details. Add the read tools to Luna's approved tool list. Existing mutation tools and `IMflMutationCoordinator` stay the only execution path.
4. Compute comparable numeric features and scenario estimates in `Application`; ask Luna to explain and challenge the result. Keep source data, formulas, and assumptions inspectable. Start with transparent rules and calibrated ranges before considering a trained model. Prompt wording alone cannot solve the missing-data problem.
5. In the UI, show a compact recommendation card: action, alternatives, this-week/ROS/future impact as applicable, upside/downside, bid range if applicable, confidence, timestamp and expandable evidence. Keep the normal chat answer concise, with links to the detailed card.

## Rollout

| Phase | Deliverable | Exit check |
| --- | --- | --- |
| 0. Audit | Capture 20-30 representative real questions without secrets; categorize redraft/keeper/dynasty, current data gaps, and bad rank-led answers. Verify league settings and MFL response fields in a test league. | Written fixtures and baseline answer-quality rubric. |
| 1. League-aware MVP | Add typed FAAB/budget, transaction, scoring/roster-rule and section-freshness reads; add user objective/risk settings. Build add/drop and waiver comparison packets from existing MFL state plus weekly ranks and cited ROS/injury checks. | No recommendation assumes cross-position rank equivalence, hidden bids, or an unknown scoring rule. |
| 2. Opportunity and risk | Cache nflverse workload trends and optionally ffopportunity; add identity mapping, ceiling/downside scenarios and start/hold/add/drop comparisons. | Historical backtest beats a weekly-rank-only baseline on decision quality and calibrated uncertainty. |
| 3. Market and roster strategy | Add bid ranges from league-completed claims, budget pacing and alternatives; evaluate lineup depth, bye/playoff weeks, trades, draft picks and conditional dynasty values. | Suggested bids obey balance/increment and beat a fixed-percent baseline in replay simulations; recommendations account for full-roster effects. |
| 4. Product feedback | Record user thumbs-up/down and whether advice was followed without storing credentials or private raw responses; review misses and recalibrate by position, league type and week. | Monitor stale-source rate, identity-match failures, recommendation acceptance, realized points/wins and bid efficiency. |

## Validation and safeguards

- Backtest with time travel: each historical decision sees only evidence published by that date. Compare against weekly-rank, ROS-rank and simple fixed-FAAB baselines. Measure regret versus available alternatives, projected/actual points, roster value and bid efficiency; avoid claiming causal win improvements from observational replay.
- Use synthetic fixtures for injury/IR, ambiguous names, bye weeks, keeper rules, zero balance, bid increments, missing budget, stale feeds, late lineup locks and multi-copy MFL leagues. Add a small manually reviewed recommendation set for tradeoffs and explanation quality.
- Refresh league data and injury/availability close to an actionable decision. Retain last-good cached evidence with visible stale flags; do not silently substitute it for current evidence. Never auto-submit or auto-retry mutations. Continue the existing preview, confirmation, single submission, refresh and verification workflow.
- Keep external content as data, not instructions. Never send credentials, cookies, owner email, private raw responses or logs to Luna or external feeds. Review terms, attribution and rate limits before integrating an outside source.
