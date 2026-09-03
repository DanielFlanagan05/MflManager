using System.Collections.Concurrent;
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
    IFantasyProsRankingClient fantasyPros) : IMflRefreshService
{
    private static readonly (SnapshotSection Section, string Type)[] Requests =
    [
        (SnapshotSection.League, "league"), (SnapshotSection.Rules, "rules"),
        (SnapshotSection.Rosters, "rosters"), (SnapshotSection.Standings, "leagueStandings"),
        (SnapshotSection.Matchups, "weeklyResults"), (SnapshotSection.FreeAgents, "freeAgents"),
        (SnapshotSection.Adp, "adp"),
        (SnapshotSection.Transactions, "transactions"), (SnapshotSection.Waivers, "pendingWaivers"),
        (SnapshotSection.Trades, "pendingTrades"), (SnapshotSection.Keepers, "keepers"),
        (SnapshotSection.DraftQueue, "myDraftList"), (SnapshotSection.Messages, "messageBoard"), (SnapshotSection.Polls, "polls"),
        (SnapshotSection.Calendar, "calendar")
    ];

    public event EventHandler<LeagueSnapshot>? SnapshotUpdated;

    public async Task<RefreshReport> RefreshAllAsync(LeagueContext league, RefreshReason reason, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var lastGood = await snapshots.GetAsync(league, cancellationToken).ConfigureAwait(false);
        var bodies = new ConcurrentDictionary<SnapshotSection, string>();
        var results = new ConcurrentBag<RefreshSectionResult>();
        using var gate = new SemaphoreSlim(6);

        var work = Requests.Select(async request =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sectionStarted = DateTimeOffset.UtcNow;
            try
            {
                var result = await exports.ExportAsync(league, request.Type, null, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess) bodies[request.Section] = result.Value!;
                results.Add(new(request.Section, result.IsSuccess, result.UserMessage, DateTimeOffset.UtcNow - sectionStarted));
            }
            finally { gate.Release(); }
        }).ToList();

        work.Add(Task.Run(async () =>
        {
            var sectionStarted = DateTimeOffset.UtcNow;
            var result = await drafts.GetDraftStateAsync(league, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) bodies[SnapshotSection.Draft] = "ok";
            results.Add(new(SnapshotSection.Draft, result.IsSuccess, result.UserMessage, DateTimeOffset.UtcNow - sectionStarted));
        }, cancellationToken));

        if (lastGood?.Players.Count == 0 || lastGood is null || reason == RefreshReason.Force)
        {
            work.Add(Task.Run(async () =>
            {
                var sectionStarted = DateTimeOffset.UtcNow;
                var result = await exports.ExportAsync(league, "players", null, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess) bodies[SnapshotSection.Players] = result.Value!;
                results.Add(new(SnapshotSection.Players, result.IsSuccess, result.UserMessage, DateTimeOffset.UtcNow - sectionStarted));
            }, cancellationToken));
        }

        await Task.WhenAll(work).ConfigureAwait(false);
        var rankingResults = await Task.WhenAll(new[] { "QB", "RB", "WR", "TE", "K", "DEF" }.Select(p => fantasyPros.GetConsensusRankingsAsync(league.Season, p, cancellationToken))).ConfigureAwait(false);
        var rankings = rankingResults.Where(x => x.IsSuccess).SelectMany(x => x.Value ?? []).OrderBy(x => x.Rank).ToArray();
        var now = DateTimeOffset.UtcNow;
        var players = bodies.TryGetValue(SnapshotSection.Players, out var playerJson) ? MflJsonParser.ParsePlayers(playerJson) : lastGood?.Players ?? [];
        if (bodies.TryGetValue(SnapshotSection.Adp, out var adpJson))
        {
            var adp = MflJsonParser.ParseAdp(adpJson);
            players = players.Select(player => adp.TryGetValue(player.Id, out var value)
                ? player with { AverageDraftPosition = value }
                : player).ToArray();
        }
        var playerMap = players.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        DraftState? draft = lastGood?.Draft;
        try { var result = await drafts.GetDraftStateAsync(league, cancellationToken).ConfigureAwait(false); if (result.IsSuccess) draft = result.Value; } catch (Exception ex) { logger.LogDebug("Draft follow-up parse failed: {Type}", ex.GetType().Name); }
        var resultMap = results.GroupBy(x => x.Section).ToDictionary(x => x.Key, x => x.Last());
        var sections = Enum.GetValues<SnapshotSection>().ToDictionary(section => section, section =>
        {
            if (resultMap.TryGetValue(section, out var sectionResult)) return new SectionState(!sectionResult.IsSuccess, sectionResult.IsSuccess ? now : lastGood?.Sections.GetValueOrDefault(section)?.UpdatedAt, sectionResult.Error);
            return lastGood?.Sections.GetValueOrDefault(section) ?? new SectionState(true, null, "Not applicable or not refreshed.");
        });
        var draftQueue = bodies.TryGetValue(SnapshotSection.DraftQueue, out var draftQueueJson)
            ? MflJsonParser.ParseDraftQueue(draftQueueJson)
            : draft?.Queue ?? lastGood?.Draft?.Queue ?? [];
        if (draft is not null) draft = draft with { Queue = draftQueue };
        var snapshot = new LeagueSnapshot
        {
            Context = league,
            CapturedAt = now,
            Settings = ParseOr(bodies, SnapshotSection.League, MflJsonParser.ParseSettings, lastGood?.Settings),
            Franchises = ParseOr(bodies, SnapshotSection.League, MflJsonParser.ParseFranchises, lastGood?.Franchises ?? []),
            Roster = bodies.TryGetValue(SnapshotSection.Rosters, out var rosterJson) ? MflJsonParser.ParseRoster(rosterJson, playerMap) : lastGood?.Roster ?? [],
            Standings = ParseOr(bodies, SnapshotSection.Standings, MflJsonParser.ParseStandings, lastGood?.Standings ?? []),
            Players = SortByFantasyPros(players, rankings),
            FreeAgents = SortByFantasyPros(ParseOr(bodies, SnapshotSection.FreeAgents, MflJsonParser.ParsePlayers, lastGood?.FreeAgents ?? []), rankings),
            Rankings = rankings,
            Transactions = ParseOr(bodies, SnapshotSection.Transactions, MflJsonParser.ParseTransactions, lastGood?.Transactions ?? []),
            Lineup = ParseOr(bodies, SnapshotSection.Matchups, MflJsonParser.ParseLineup, lastGood?.Lineup ?? []),
            Matchups = ParseOr(bodies, SnapshotSection.Matchups, MflJsonParser.ParseMatchups, lastGood?.Matchups ?? []),
            Waivers = ParseOr(bodies, SnapshotSection.Waivers, MflJsonParser.ParseWaivers, lastGood?.Waivers ?? []),
            Trades = ParseOr(bodies, SnapshotSection.Trades, MflJsonParser.ParseTrades, lastGood?.Trades ?? []),
            Draft = draft,
            Sections = sections
        };
        await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        state.UpdateSnapshot(snapshot);
        SnapshotUpdated?.Invoke(this, snapshot);
        return new RefreshReport(league, reason, started, DateTimeOffset.UtcNow, results.OrderBy(x => x.Section).ToArray(), snapshot);
    }

    private static IReadOnlyList<Player> SortByFantasyPros(IReadOnlyList<Player> players, IReadOnlyList<FantasyProsRanking> rankings)
    {
        var rank = rankings.GroupBy(x => (Name: Normalize(x.PlayerName), Team: x.Team.ToUpperInvariant())).ToDictionary(x => x.Key, x => x.Min(r => r.Rank));
        return players.OrderBy(x => rank.GetValueOrDefault((Normalize(x.Name), x.NflTeam.ToUpperInvariant()), int.MaxValue)).ThenBy(x => x.Name).ToArray();
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static T ParseOr<T>(ConcurrentDictionary<SnapshotSection, string> bodies, SnapshotSection section, Func<string, T> parser, T fallback)
    {
        if (!bodies.TryGetValue(section, out var body)) return fallback;
        try { return parser(body); } catch { return fallback; }
    }
}
