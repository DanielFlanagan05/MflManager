using System.Collections.Concurrent;
using System.Diagnostics;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflRefreshService(
    IMflExportClient exports,
    IMflDraftClient drafts,
    ILeagueSnapshotStore snapshots,
    ILeagueState state,
    ILogger<MflRefreshService> logger,
    IPlayerRankingClient playerRankings,
    IMflLineupQueryService lineups) : IMflRefreshService
{
    private static readonly (SnapshotSection Section, string Type)[] Requests =
    [
        (SnapshotSection.League, "league"), (SnapshotSection.Rosters, "rosters"), (SnapshotSection.Standings, "leagueStandings"),
        (SnapshotSection.Matchups, "weeklyResults"), (SnapshotSection.FreeAgents, "freeAgents"),
        (SnapshotSection.Adp, "adp"),
        (SnapshotSection.Injuries, "injuries"),
        (SnapshotSection.NflByeWeeks, "nflByeWeeks"),
        (SnapshotSection.Transactions, "transactions"), (SnapshotSection.Waivers, "pendingWaivers"),
        (SnapshotSection.Trades, "pendingTrades"), (SnapshotSection.DraftQueue, "myDraftList"),
        (SnapshotSection.Calendar, "calendar")
    ];

    public event EventHandler<LeagueSnapshot>? SnapshotUpdated;
    private readonly object _refreshLock = new();
    private Task<RefreshReport>? _inFlight;
    private LeagueContext? _refreshLeague;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public Task<RefreshReport> RefreshAllAsync(LeagueContext league, RefreshReason reason, CancellationToken cancellationToken)
    {
        lock (_refreshLock)
        {
            if (reason != RefreshReason.Mutation && _refreshLeague == league && _inFlight is { IsCompleted: false }) return _inFlight;
            _refreshLeague = league;
            return _inFlight = RunRefreshAsync(league, reason, cancellationToken);
        }
    }

    public async Task<bool> RefreshPlayerStatusesAsync(LeagueContext league, CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = state.CurrentSnapshot;
            if (snapshot?.Context != league) return false;
            var result = await exports.ExportAsync(league, "injuries", null, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string>? statuses = null;
            if (result.IsSuccess)
            {
                try { statuses = MflJsonParser.ParseInjuries(result.Value!); }
                catch (System.Text.Json.JsonException)
                { logger.LogWarning("MFL injuries response could not be parsed"); }
            }
            if (statuses is not null)
                logger.LogInformation("MFL player statuses refreshed: {DesignationCount} designations", statuses.Count);
            var now = DateTimeOffset.UtcNow;
            var sections = snapshot.Sections.ToDictionary(x => x.Key, x => x.Value);
            sections[SnapshotSection.Injuries] = statuses is null
                ? new SectionState(true, snapshot.Sections.GetValueOrDefault(SnapshotSection.Injuries)?.UpdatedAt,
                    "Player status could not be refreshed from MFL.")
                : new SectionState(false, now);
            snapshot = statuses is null
                ? snapshot with { Sections = sections }
                : snapshot with
                {
                    CapturedAt = now,
                    Sections = sections,
                    Players = snapshot.Players.Select(x => x with { Injury = statuses.GetValueOrDefault(x.Id) }).ToArray(),
                    Roster = snapshot.Roster.Select(x => x with { Player = x.Player with { Injury = statuses.GetValueOrDefault(x.Player.Id) } }).ToArray(),
                    FreeAgents = snapshot.FreeAgents.Select(x => x with { Injury = statuses.GetValueOrDefault(x.Id) }).ToArray()
                };
            await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            state.UpdateSnapshot(snapshot);
            SnapshotUpdated?.Invoke(this, snapshot);
            return statuses is not null;
        }
        finally { _executionGate.Release(); }
    }

    private async Task<RefreshReport> RunRefreshAsync(LeagueContext league, RefreshReason reason, CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshCoreAsync(league, reason, cancellationToken).ConfigureAwait(false); }
        finally { _executionGate.Release(); }
    }

    private async Task<RefreshReport> RefreshCoreAsync(LeagueContext league, RefreshReason reason, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var lastGood = await snapshots.GetAsync(league, cancellationToken).ConfigureAwait(false);
        var partial = (lastGood ?? new LeagueSnapshot { Context = league, CapturedAt = started }) with
            { AvailablePlayersReady = false, WeeklyRankingsReady = false, AvailablePlayersError = null };
        state.UpdateSnapshot(partial);
        var bodies = new ConcurrentDictionary<SnapshotSection, string>();
        var results = new ConcurrentBag<RefreshSectionResult>();
        using var gate = new SemaphoreSlim(6);
        DraftState? refreshedDraft = null;
        using var publishGate = new SemaphoreSlim(1, 1);

        async Task PublishSectionAsync(SnapshotSection section, string body)
        {
            await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var map = partial.Players.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                partial = section switch
                {
                    SnapshotSection.League => partial with { Settings = MflJsonParser.ParseSettings(body), Franchises = MflJsonParser.ParseFranchises(body), FaabBalance = MflJsonParser.ParseFaabBalance(body, league.FranchiseId) },
                    SnapshotSection.Players => partial with { Players = MflJsonParser.ParsePlayers(body) },
                    SnapshotSection.FreeAgents => partial with { FreeAgents = MflJsonParser.ParseFreeAgents(body, map) },
                    SnapshotSection.Rosters => partial with { Roster = MflJsonParser.ParseRoster(body, map, league.FranchiseId) },
                    SnapshotSection.Standings => partial with { Standings = MflJsonParser.ParseStandings(body) },
                    SnapshotSection.Waivers => partial with { Waivers = MflJsonParser.ParseWaivers(body) },
                    SnapshotSection.Calendar => partial with { NextWaiverProcessingAt = MflJsonParser.ParseNextWaiverProcessingAt(body, DateTimeOffset.UtcNow) },
                    _ => partial
                };
                var states = partial.Sections.ToDictionary(x => x.Key, x => x.Value);
                states[section] = new SectionState(false, DateTimeOffset.UtcNow);
                partial = partial with { CapturedAt = DateTimeOffset.UtcNow, Sections = states };
                if (section == SnapshotSection.Waivers)
                    logger.LogInformation("Pending waivers parsed {ClaimCount} active claims for the selected franchise", partial.Waivers.Count);
                await snapshots.SaveAsync(partial, cancellationToken).ConfigureAwait(false);
                state.UpdateSnapshot(partial);
                SnapshotUpdated?.Invoke(this, partial);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            { logger.LogWarning("Incremental refresh parser failed for {Section}: {ExceptionType}", section, ex.GetType().Name); }
            finally { publishGate.Release(); }
        }

        async Task FetchPriorityAsync(SnapshotSection section, string type)
        {
            var sectionStarted = DateTimeOffset.UtcNow;
            var response = await exports.ExportAsync(league, type, null, cancellationToken).ConfigureAwait(false);
            results.Add(new(section, response.IsSuccess, response.UserMessage, DateTimeOffset.UtcNow - sectionStarted));
            if (response.IsSuccess)
            {
                bodies[section] = response.Value!;
                await PublishSectionAsync(section, response.Value!).ConfigureAwait(false);
            }
        }

        // Ranking retrieval starts independently; cached roster/standings are already published.
        var rankingStarted = Stopwatch.GetTimestamp();
        var rankingTask = playerRankings.GetRankingsAsync(league.Season, cancellationToken);
        var statusStarted = Stopwatch.GetTimestamp();
        var status = await exports.ExportAsync(league, "mfl_status", null, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Refresh request MflStatus completed in {DurationMs} ms with success {IsSuccess}",
            Milliseconds(Stopwatch.GetElapsedTime(statusStarted)), status.IsSuccess);
        var weekStatus = new MflWeekStatus(0, 0, 0, 0, 0);
        if (status.IsSuccess)
        {
            try { weekStatus = MflJsonParser.ParseMflWeekStatus(status.Value!); }
            catch (System.Text.Json.JsonException) { }
        }
        var rankingWeek = weekStatus.UpcomingWeek > 0 ? weekStatus.UpcomingWeek : weekStatus.LineupWeek;
        partial = partial with { RankingWeek = rankingWeek, LineupWeek = weekStatus.ActiveLineupWeek };
        logger.LogInformation("MFL week status: lineup {LineupWeek}, current {CurrentWeek}, completed {CompletedWeek}, active lineup {ActiveLineupWeek}",
            weekStatus.LineupWeek, weekStatus.CurrentWeek, weekStatus.CompletedWeek, weekStatus.ActiveLineupWeek);
        var priorityWork = new List<Task>();
        priorityWork.Add(FetchPriorityAsync(SnapshotSection.Players, "players"));
        priorityWork.Add(FetchPriorityAsync(SnapshotSection.FreeAgents, "freeAgents"));
        priorityWork.Add(FetchPriorityAsync(SnapshotSection.League, "league"));

        if (weekStatus.ActiveLineupWeek <= 0) await Task.WhenAll(priorityWork).ConfigureAwait(false);
        var weekSnapshot = partial with
        {
            Settings = !bodies.ContainsKey(SnapshotSection.League) && partial.Settings is { } cachedSettings
                ? cachedSettings with { CurrentWeek = 0 }
                : partial.Settings
        };
        var requestedLineupWeek = LineupRules.ActiveWeek(weekSnapshot);
        // When status succeeded this was already set before concurrent section publication.
        if (weekStatus.ActiveLineupWeek <= 0) partial = partial with { LineupWeek = requestedLineupWeek };

        var work = Requests.Where(x => x.Section is not SnapshotSection.FreeAgents and not SnapshotSection.League).Select(async request =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sectionStarted = Stopwatch.GetTimestamp();
            try
            {
                var parameters = request.Section == SnapshotSection.Matchups
                    ? new Dictionary<string, string> { ["W"] = requestedLineupWeek.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : null;
                var result = await exports.ExportAsync(league, request.Type, parameters, cancellationToken).ConfigureAwait(false);
                var valid = result.IsSuccess;
                if (valid && request.Section == SnapshotSection.Injuries)
                {
                    try { _ = MflJsonParser.ParseInjuries(result.Value!); }
                    catch (System.Text.Json.JsonException)
                    {
                        valid = false;
                        logger.LogWarning("Refresh section Injuries returned an unrecognized response");
                    }
                }
                if (valid && request.Section == SnapshotSection.NflByeWeeks)
                {
                    try { _ = MflJsonParser.ParseNflByeWeeks(result.Value!, league.Season.Year); }
                    catch (System.Text.Json.JsonException) { valid = false; }
                }
                if (valid)
                {
                    bodies[request.Section] = result.Value!;
                    if (request.Section is SnapshotSection.Rosters or SnapshotSection.Standings or SnapshotSection.Waivers)
                        await PublishSectionAsync(request.Section, result.Value!).ConfigureAwait(false);
                }
                else if (!result.IsSuccess) logger.LogWarning("Refresh section {Section} failed with code {ErrorCode}, correlation {CorrelationId}",
                    request.Section, result.MflErrorCode ?? "HTTP_OR_NETWORK", result.CorrelationId);
                results.Add(new(request.Section, valid, valid ? null : result.UserMessage ?? "MFL returned an unrecognized response.", Stopwatch.GetElapsedTime(sectionStarted)));
            }
            finally { gate.Release(); }
        }).ToList();

        work.Add(FetchDraftAsync());
        await Task.WhenAll(priorityWork).ConfigureAwait(false);
        var availableReady = bodies.ContainsKey(SnapshotSection.FreeAgents) && partial.Players.Count > 0;
        IReadOnlyList<PlayerRanking> rankings = [];
        var rankingsReady = false;
        var rankingsStale = false;
        string? availableError = null;
        async Task PublishRankingsAsync()
        {
            RankingLoadResult rankingResult;
            try { rankingResult = await rankingTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { rankingResult = new(lastGood?.RankingSource == "Fantasy Footballers" ? lastGood.Rankings : [], true, "Player rankings could not be refreshed; last valid data was preserved."); }
            await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                rankings = rankingResult.Rankings;
                rankingsReady = rankings.Count > 0;
                rankingsStale = rankingResult.IsStale;
                availableError = !availableReady ? "MFL available players could not be refreshed." : rankingResult.Message;
                partial = partial with { Rankings = rankings, RankingSource = "Fantasy Footballers", RankingsStale = rankingsStale,
                    RankingWeek = rankingWeek, AvailablePlayersReady = availableReady, WeeklyRankingsReady = rankingsReady, AvailablePlayersError = availableError };
                var identities = partial.Players.Select(x => WaiverPlayerSelector.Identity(x.Name, x.NflTeam, x.Position)).ToHashSet();
                var unmatched = rankings.Where(x => !identities.Contains(WaiverPlayerSelector.Identity(x.PlayerName, x.Team, x.Position))).ToArray();
                logger.LogInformation("External rankings matched {MatchedCount} source rows to the MFL catalog; {UnmatchedCount} source rows unmatched", rankings.Count - unmatched.Length, unmatched.Length);
                logger.LogInformation("External rankings refresh completed in {DurationMs} ms with {RankingCount} rows; stale {IsStale}",
                    Milliseconds(Stopwatch.GetElapsedTime(rankingStarted)), rankings.Count, rankingsStale);
                await snapshots.SaveAsync(partial, cancellationToken).ConfigureAwait(false);
                state.UpdateSnapshot(partial);
                SnapshotUpdated?.Invoke(this, partial);
            }
            finally { publishGate.Release(); }
        }
        var rankingPublication = PublishRankingsAsync();

        async Task FetchDraftAsync()
        {
            var sectionStarted = Stopwatch.GetTimestamp();
            try
            {
                var result = await drafts.GetDraftStateAsync(league, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    bodies[SnapshotSection.Draft] = "ok";
                    refreshedDraft = result.Value;
                }
                else logger.LogWarning("Refresh section Draft failed with code {ErrorCode}, correlation {CorrelationId}",
                    result.MflErrorCode ?? "HTTP_OR_NETWORK", result.CorrelationId);
                results.Add(new(SnapshotSection.Draft, result.IsSuccess, result.UserMessage, Stopwatch.GetElapsedTime(sectionStarted)));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                logger.LogWarning("Refresh section Draft parser failed with {ExceptionType}", ex.GetType().Name);
                results.Add(new(SnapshotSection.Draft, false, "MFL returned an unrecognized draft response.", Stopwatch.GetElapsedTime(sectionStarted)));
            }
        }

        await Task.WhenAll(work.Append(rankingPublication)).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var players = ParseOr(bodies, SnapshotSection.Players, MflJsonParser.ParsePlayers, lastGood?.Players ?? []);
        if (bodies.TryGetValue(SnapshotSection.Adp, out var adpJson))
        {
            var adp = ParseOr(bodies, SnapshotSection.Adp, MflJsonParser.ParseAdp,
                new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
            players = players.Select(player => adp.TryGetValue(player.Id, out var value)
                ? player with { AverageDraftPosition = value }
                : player).ToArray();
        }
        var playerMap = players.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var injuries = ParseOr(bodies, SnapshotSection.Injuries, MflJsonParser.ParseInjuries,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (bodies.ContainsKey(SnapshotSection.Injuries))
        {
            logger.LogInformation("MFL full refresh parsed {DesignationCount} player status designations", injuries.Count);
            var freshCatalog = bodies.ContainsKey(SnapshotSection.Players);
            players = players.Select(player => player with
            {
                Injury = injuries.TryGetValue(player.Id, out var injury) ? injury : freshCatalog ? player.Injury : null
            }).ToArray();
            playerMap = players.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        }
        var settings = ParseOr(bodies, SnapshotSection.League, MflJsonParser.ParseSettings, lastGood?.Settings);
        var roster = ParseOr(bodies, SnapshotSection.Rosters,
            json => MflJsonParser.ParseRoster(json, playerMap, league.FranchiseId), lastGood?.Roster ?? []);
        roster = roster.Select(x => playerMap.TryGetValue(x.Player.Id, out var currentPlayer)
            ? x with { Player = currentPlayer } : x).ToArray();
        var freeAgents = ParseOr(bodies, SnapshotSection.FreeAgents,
            json => MflJsonParser.ParseFreeAgents(json, playerMap), lastGood?.FreeAgents ?? []);
        var freeAgentCopies = lastGood?.FreeAgentCopies ?? new Dictionary<string, int>();
        if (settings is { RostersPerPlayer: > 0 } &&
            settings.PlayerLimitUnit.Equals("LEAGUE", StringComparison.OrdinalIgnoreCase) &&
            bodies.TryGetValue(SnapshotSection.FreeAgents, out var freeAgentsJson) &&
            bodies.TryGetValue(SnapshotSection.Rosters, out var rostersJson))
        {
            try { freeAgentCopies = MflJsonParser.ParseFreeAgentCopies(freeAgentsJson, rostersJson, settings.RostersPerPlayer); }
            catch (System.Text.Json.JsonException)
            { logger.LogWarning("Free-agent copy availability could not be parsed; preserving last-good values"); }
        }
        logger.LogInformation("Refresh parsed player counts: players {PlayerCount}, roster {RosterCount}, free agents {FreeAgentCount}",
            players.Count, roster.Count, freeAgents.Count);
        logger.LogInformation("Free-agent copy availability: league limit {LeagueCopyLimit}, {CountedPlayerCount} available players counted, {MultipleCopyPlayerCount} with multiple available copies, maximum {MaximumCopies} available copies",
            settings?.RostersPerPlayer ?? 0, freeAgentCopies.Count, freeAgentCopies.Values.Count(count => count > 1),
            freeAgentCopies.Values.DefaultIfEmpty(0).Max());
        logger.LogInformation("Refresh parsed lineup requirements: {StarterCount} starters, {PositionRuleCount} position rules",
            settings?.StarterCount ?? 0, settings?.LineupRules.Count ?? 0);
        if (bodies.ContainsKey(SnapshotSection.Rosters) && roster.Count == 0)
            logger.LogWarning("MFL returned a roster response, but zero players matched the selected franchise");
        var matchups = ParseOr(bodies, SnapshotSection.Matchups, MflJsonParser.ParseMatchups, lastGood?.Matchups ?? []);
        var lineup = lastGood?.Lineup ?? [];
        var weeklyPlayerPoints = lastGood?.WeeklyPlayerPoints ?? new Dictionary<string, decimal>();
        var weeklyPlayerPointsWeek = lastGood?.WeeklyPlayerPointsWeek ?? 0;
        var lineupStarted = Stopwatch.GetTimestamp();
        var lineupWeek = requestedLineupWeek;
        WeeklyLineup? refreshedLineup = null;
        string? lineupError = null;
        WeeklyPointsValidationException? pointsFailure = null;
        try
        {
            bodies.TryGetValue(SnapshotSection.Matchups, out var weeklyResultsBody);
            var lineupResult = await lineups.GetAsync(league, lineupWeek,
                roster.Where(x => x.Status == RosterStatus.Active).Select(x => x.Player.Id).ToArray(),
                cancellationToken, weeklyResultsBody).ConfigureAwait(false);
            refreshedLineup = lineupResult.IsSuccess ? lineupResult.Value : null;
            lineupError = lineupResult.UserMessage;
            if (refreshedLineup is not null && refreshedLineup.Week != lineupWeek)
                throw new WeeklyPointsValidationException(lineupWeek);
        }
        catch (WeeklyPointsValidationException ex)
        {
            refreshedLineup = null;
            pointsFailure = ex;
            lineupError = ex.Message;
        }
        if (refreshedLineup is not null)
        {
            var rosterById = roster.ToDictionary(x => x.Player.Id, StringComparer.OrdinalIgnoreCase);
            lineup = refreshedLineup.StarterPlayerIds.Select(id => new LineupSlot(
                rosterById.GetValueOrDefault(id)?.Player.Position ?? string.Empty, id, true)).ToArray();
            weeklyPlayerPoints = refreshedLineup.PlayerPoints;
            weeklyPlayerPointsWeek = refreshedLineup.Week;
        }
        results.Add(new(SnapshotSection.Lineup, refreshedLineup is not null, lineupError, Stopwatch.GetElapsedTime(lineupStarted)));
        DraftState? draft = refreshedDraft ?? lastGood?.Draft;
        var resultMap = results.GroupBy(x => x.Section).ToDictionary(x => x.Key, x => x.Last());
        var sections = Enum.GetValues<SnapshotSection>().ToDictionary(section => section, section =>
        {
            if (resultMap.TryGetValue(section, out var sectionResult)) return new SectionState(!sectionResult.IsSuccess, sectionResult.IsSuccess ? now : lastGood?.Sections.GetValueOrDefault(section)?.UpdatedAt, sectionResult.Error);
            return lastGood?.Sections.GetValueOrDefault(section) ?? new SectionState(true, null, "Not applicable or not refreshed.");
        });
        var draftQueue = ParseOr(bodies, SnapshotSection.DraftQueue, MflJsonParser.ParseDraftQueue,
            draft?.Queue ?? lastGood?.Draft?.Queue ?? []);
        if (draft is not null) draft = draft with { Queue = draftQueue };
        var snapshot = new LeagueSnapshot
        {
            Context = league,
            RankingWeek = rankingWeek,
            LineupWeek = lineupWeek,
            AvailablePlayersReady = availableReady,
            WeeklyRankingsReady = rankingsReady,
            RankingsStale = rankingsStale,
            RankingSource = "Fantasy Footballers",
            AvailablePlayersError = availableError,
            CapturedAt = now,
            Settings = settings,
            Franchises = ParseOr(bodies, SnapshotSection.League, MflJsonParser.ParseFranchises, lastGood?.Franchises ?? []),
            FaabBalance = ParseOr(bodies, SnapshotSection.League, json => MflJsonParser.ParseFaabBalance(json, league.FranchiseId), lastGood?.FaabBalance),
            Roster = roster,
            NflByeWeeks = ParseOr(bodies, SnapshotSection.NflByeWeeks,
                json => MflJsonParser.ParseNflByeWeeks(json, league.Season.Year), lastGood?.NflByeWeeks ?? new Dictionary<string, int>()),
            WeeklyPlayerPoints = weeklyPlayerPoints,
            WeeklyPlayerPointsWeek = weeklyPlayerPointsWeek,
            Standings = ParseOr(bodies, SnapshotSection.Standings, MflJsonParser.ParseStandings, lastGood?.Standings ?? []),
            Players = players,
            FreeAgents = freeAgents,
            FreeAgentCopies = freeAgentCopies,
            Rankings = rankings,
            Transactions = ParseOr(bodies, SnapshotSection.Transactions, MflJsonParser.ParseTransactions, lastGood?.Transactions ?? []),
            Lineup = lineup,
            Matchups = matchups,
            Waivers = ParseOr(bodies, SnapshotSection.Waivers, MflJsonParser.ParseWaivers, lastGood?.Waivers ?? []),
            NextWaiverProcessingAt = bodies.TryGetValue(SnapshotSection.Calendar, out _)
                ? ParseOr(bodies, SnapshotSection.Calendar, json => MflJsonParser.ParseNextWaiverProcessingAt(json, now), lastGood?.NextWaiverProcessingAt)
                : lastGood?.NextWaiverProcessingAt,
            Trades = ParseOr(bodies, SnapshotSection.Trades, MflJsonParser.ParseTrades, lastGood?.Trades ?? []),
            Draft = draft,
            Sections = sections
        };
        await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        state.UpdateSnapshot(snapshot);
        logger.LogInformation("Refresh published snapshot: roster {RosterCount}, players {PlayerCount}, free agents {FreeAgentCount}, standings {StandingsCount}, matchups {MatchupCount}",
            snapshot.Roster.Count, snapshot.Players.Count, snapshot.FreeAgents.Count, snapshot.Standings.Count, snapshot.Matchups.Count);
        var completed = DateTimeOffset.UtcNow;
        var slowest = string.Join(", ", results.OrderByDescending(x => x.Duration).Take(5)
            .Select(x => $"{x.Section} {Milliseconds(x.Duration)} ms"));
        logger.LogInformation("Refresh completed in {DurationMs} ms; slowest section tasks (including scheduling): {SlowestSections}",
            Milliseconds(completed - started), slowest);
        SnapshotUpdated?.Invoke(this, snapshot);
        if (pointsFailure is not null) throw pointsFailure;
        return new RefreshReport(league, reason, started, completed, results.OrderBy(x => x.Section).ToArray(), snapshot);
    }

    private T ParseOr<T>(ConcurrentDictionary<SnapshotSection, string> bodies, SnapshotSection section, Func<string, T> parser, T fallback)
    {
        if (!bodies.TryGetValue(section, out var body)) return fallback;
        try { return parser(body); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("Refresh parser for section {Section} failed with {ExceptionType}; preserving last-good data",
                section, ex.GetType().Name);
            return fallback;
        }
    }

    private static long Milliseconds(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);
}
