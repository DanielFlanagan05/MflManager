using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;
using MflManager.MflApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace MflManager.UnitTests;

public sealed class WeeklyPointsTests
{
    private static readonly LeagueContext League = new("123", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");

    private static string Scores(int week, string starters = "p1") => JsonSerializer.Serialize(new
    {
        weeklyResults = new { week, franchise = new { id = "0001", starters, player = new[] { new { id = "p1", score = week * 10 } } } }
    });

    private const string UnscoredWeek = """{"weeklyResults":{"week":"3","franchise":{"id":"0001","starters":"p1","player":[{"id":"p1","status":"starter"},{"id":"bench","status":"nonstarter"}]}}}""";
    private const string LiveScores = """{"liveScoring":{"week":"3","franchise":[{"id":"0002","players":{"player":{"id":"p1","score":"999"}}},{"id":"0001","players":{"player":[{"id":"p1","status":"starter","score":"22.45","gameSecondsRemaining":"0"},{"id":"bench","status":"nonstarter","score":"0"},{"id":"negative","status":"nonstarter","score":"-2.5"},{"id":"missing","status":"nonstarter"},{"id":"invalid","score":"N/A"}]}}]}}""";

    [Fact]
    public void LiveScoreParser_ReadsNestedStarterAndBenchScoresForSelectedTeam()
    {
        var lineup = MflJsonParser.ParseLiveScoringLineup(LiveScores, "0001");
        Assert.Equal(3, lineup.Week);
        Assert.Equal(["p1"], lineup.StarterPlayerIds);
        Assert.Equal(22.45m, lineup.PlayerPoints["p1"]);
        Assert.Equal(0m, lineup.PlayerPoints["bench"]);
        Assert.Equal(-2.5m, lineup.PlayerPoints["negative"]);
        Assert.Equal(3, lineup.PlayerPoints.Count);
        var singleton = MflJsonParser.ParseLiveScoringLineup("""{"liveScoring":{"week":3,"franchise":{"id":"0001","players":{"player":{"id":"p1","score":12.5}}}}}""", "0001");
        Assert.Equal(12.5m, singleton.PlayerPoints["p1"]);
    }

    [Fact]
    public async Task WeeklyResultsWithoutScores_LoadsLiveScoresIncludingBenchAndRetainsCompleteStarters()
    {
        var exports = new Exports(UnscoredWeek);
        var result = await Query(exports).GetAsync(League, 3, ["p1", "bench"], CancellationToken.None, UnscoredWeek);
        Assert.Equal(["p1"], result.Value!.StarterPlayerIds);
        Assert.Equal(22.45m, result.Value.PlayerPoints["p1"]);
        Assert.Equal(0m, result.Value.PlayerPoints["bench"]);
        Assert.Empty(exports.WeeklyRequests);
        var request = Assert.Single(exports.LiveRequests);
        Assert.Equal("3", request!["W"]);
        Assert.Equal("1", request["DETAILS"]);
        Assert.Equal(0, exports.RosterStatusRequests);
    }

    [Theory]
    [InlineData("{\"liveScoring\":{\"week\":\"2\",\"franchise\":{\"id\":\"0001\"}}}")]
    [InlineData("{\"liveScoring\":{\"franchise\":{\"id\":\"0001\"}}}")]
    [InlineData("{\"liveScoring\":{\"week\":\"3\",\"franchise\":{\"id\":\"0002\"}}}")]
    [InlineData("invalid json")]
    [InlineData(null)]
    public async Task InvalidLiveScores_RetryOnceAndNeverRelabelOrUseAnotherTeam(string? body)
    {
        var exports = new Exports(UnscoredWeek) { LiveBody = body };
        await Assert.ThrowsAsync<WeeklyPointsValidationException>(() => Query(exports).GetAsync(League, 3, ["p1"], CancellationToken.None));
        Assert.Equal(2, exports.LiveRequests.Count);
        Assert.All(exports.LiveRequests, p => { Assert.Equal("3", p!["W"]); Assert.Equal("1", p["DETAILS"]); });
    }

    [Fact]
    public async Task FullRefresh_PersistsLiveScoreFallbackWithCorrectWeekAndInjuryCodes()
    {
        var exports = new Exports(UnscoredWeek);
        var store = new Store(new LeagueSnapshot { Context = League, CapturedAt = DateTimeOffset.UtcNow });
        var state = new LeagueState();
        state.SelectLeague(League);
        var refresh = new MflRefreshService(exports, new Drafts(), store, state,
            NullLogger<MflRefreshService>.Instance, new Rankings(), Query(exports));
        await refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None);
        Assert.True(store.Snapshot.HasPointsForWeek(3));
        Assert.Equal(22.45m, store.Snapshot.WeeklyPlayerPoints["p1"]);
        Assert.Equal("Q", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player));
        Assert.Single(exports.LiveRequests);
        Assert.Equal(3, store.Snapshot.NflByeWeeks["BUF"]);
        Assert.Equal("Q BYE", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player, 3, store.Snapshot.NflByeWeeks));
        exports.ByeBody = "{}";
        await refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None);
        Assert.Equal(3, store.Snapshot.NflByeWeeks["BUF"]);
        Assert.True(store.Snapshot.Sections[SnapshotSection.NflByeWeeks].IsStale);
    }

    [Fact]
    public async Task FailedLiveFallback_PreservesLastGoodScoreProvenanceAndMarksLineupStale()
    {
        var exports = new Exports(UnscoredWeek) { LiveBody = null };
        var store = new Store(new LeagueSnapshot
        {
            Context = League, CapturedAt = DateTimeOffset.UtcNow,
            WeeklyPlayerPointsWeek = 2, WeeklyPlayerPoints = new Dictionary<string, decimal> { ["p1"] = 17m }
        });
        var state = new LeagueState();
        state.SelectLeague(League);
        var refresh = new MflRefreshService(exports, new Drafts(), store, state,
            NullLogger<MflRefreshService>.Instance, new Rankings(), Query(exports));
        await Assert.ThrowsAsync<WeeklyPointsValidationException>(() => refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None));
        Assert.Equal(2, store.Snapshot.WeeklyPlayerPointsWeek);
        Assert.Equal(17m, store.Snapshot.WeeklyPlayerPoints["p1"]);
        Assert.False(store.Snapshot.HasPointsForWeek(3));
        Assert.True(store.Snapshot.Sections[SnapshotSection.Lineup].IsStale);
    }

    [Fact]
    public async Task WrongWeek_RetriesRequestedWeekAndReturnsOnlyValidatedScores()
    {
        var exports = new Exports(Scores(2), Scores(3));
        var result = await Query(exports).GetAsync(League, 3, ["p1"], CancellationToken.None);
        Assert.Equal(3, result.Value!.Week);
        Assert.Equal(30m, result.Value.PlayerPoints["p1"]);
        Assert.Equal(2, exports.WeeklyRequests.Count);
        Assert.All(exports.WeeklyRequests, p => { Assert.Equal("3", p!["W"]); Assert.Equal("0001", p["F"]); });
    }

    [Theory]
    [InlineData("{\"weeklyResults\":{\"week\":\"2\"}}")]
    [InlineData("{\"weeklyResults\":{\"franchise\":{\"id\":\"0001\",\"player\":{\"id\":\"p1\",\"score\":\"20\"}}}}")]
    [InlineData("invalid json")]
    [InlineData(null)]
    public async Task UnverifiableWeek_ThrowsAfterOneRetryAndNeverUsesRosterFallback(string? body)
    {
        var exports = new Exports(body, body);
        var error = await Assert.ThrowsAsync<WeeklyPointsValidationException>(() =>
            Query(exports).GetAsync(League, 3, ["p1"], CancellationToken.None));
        Assert.Equal(3, error.Week);
        Assert.Equal(2, exports.WeeklyRequests.Count);
        Assert.Equal(0, exports.RosterStatusRequests);
    }

    [Fact]
    public async Task ReusedResponse_IsValidatedAndCountsAsInitialAttempt()
    {
        var exports = new Exports(Scores(3));
        var result = await Query(exports).GetAsync(League, 3, ["p1"], CancellationToken.None, Scores(2));
        Assert.Equal(30m, result.Value!.PlayerPoints["p1"]);
        Assert.Single(exports.WeeklyRequests);
    }

    [Fact]
    public async Task ValidWeekWithoutStarters_PreservesVerifiedPointsThroughRosterFallback()
    {
        var exports = new Exports(Scores(3, ""));
        var result = await Query(exports).GetAsync(League, 3, ["p1"], CancellationToken.None);
        Assert.Equal("p1", Assert.Single(result.Value!.StarterPlayerIds));
        Assert.Equal(30m, result.Value.PlayerPoints["p1"]);
        Assert.Single(exports.WeeklyRequests);
        Assert.Equal(1, exports.RosterStatusRequests);
        Assert.Empty(exports.LiveRequests);
    }

    [Fact]
    public void LegacyAndDifferentWeekCaches_CannotSupplySelectedWeekPoints()
    {
        var legacy = JsonSerializer.Deserialize<LeagueSnapshot>(JsonSerializer.Serialize(new
        {
            Context = League, CapturedAt = DateTimeOffset.UtcNow, LineupWeek = 3,
            WeeklyPlayerPoints = new Dictionary<string, decimal> { ["p1"] = 20m }
        }))!;
        Assert.False(legacy.HasPointsForWeek(3));
        Assert.False((legacy with { WeeklyPlayerPointsWeek = 2 }).HasPointsForWeek(3));
        Assert.True((legacy with { WeeklyPlayerPointsWeek = 2 }).HasPointsForWeek(2));
    }

    [Theory]
    [InlineData("2026-09-15T03:59:59Z", 1)]
    [InlineData("2026-09-15T04:00:00Z", 2)]
    [InlineData("2026-09-22T03:59:59Z", 2)]
    [InlineData("2026-09-22T04:00:00Z", 3)]
    [InlineData("2026-09-28T16:00:00Z", 3)]
    [InlineData("2026-09-29T04:00:00Z", 4)]
    [InlineData("2027-02-01T12:00:00Z", 18)]
    public void WeekFallback_AdvancesEveryTuesdayInEasternTime(string timestamp, int expected)
    {
        var snapshot = new LeagueSnapshot { Context = League, CapturedAt = DateTimeOffset.UtcNow, RankingWeek = 9 };
        var now = DateTimeOffset.Parse(timestamp);
        Assert.Equal(expected, LineupRules.ActiveWeek(snapshot, now));
        Assert.Equal(7, LineupRules.ActiveWeek(snapshot with { LineupWeek = 7 }, now));
        Assert.Equal(6, LineupRules.ActiveWeek(snapshot with { Settings = new("League", 1, 18, 20, 0, 0, new Dictionary<string, string>(), CurrentWeek: 6) }, now));
        Assert.Equal(1, LineupRules.ActiveWeek(snapshot with { Context = League with { Season = SeasonContext.Historical(2026) } }, now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_RequestsActiveWeekAndNeverRelabelsOldCachedPoints(bool retryFails)
    {
        var exports = new Exports(Scores(2), Scores(retryFails ? 2 : 3));
        var store = new Store(new LeagueSnapshot
        {
            Context = League, CapturedAt = DateTimeOffset.UtcNow, LineupWeek = 2,
            WeeklyPlayerPointsWeek = 2, WeeklyPlayerPoints = new Dictionary<string, decimal> { ["p1"] = 20m }
        });
        var state = new LeagueState();
        state.SelectLeague(League);
        var refresh = new MflRefreshService(exports, new Drafts(), store, state,
            NullLogger<MflRefreshService>.Instance, new Rankings(), Query(exports));
        if (retryFails)
        {
            await Assert.ThrowsAsync<WeeklyPointsValidationException>(() => refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None));
            Assert.Equal(2, store.Snapshot.WeeklyPlayerPointsWeek);
            Assert.False(store.Snapshot.HasPointsForWeek(3));
            Assert.True(store.Snapshot.Sections[SnapshotSection.Lineup].IsStale);
        }
        else
        {
            var report = await refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None);
            Assert.True(report.Snapshot.HasPointsForWeek(3));
            Assert.Equal(30m, report.Snapshot.WeeklyPlayerPoints["p1"]);
        }
        Assert.Equal(3, store.Snapshot.LineupWeek);
        Assert.Equal("Q", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player));
        Assert.Equal(2, exports.WeeklyRequests.Count);
        Assert.All(exports.WeeklyRequests, p => Assert.Equal("3", p!["W"]));
    }

    [Fact]
    public async Task ScopedStatusRefresh_ClearsExpiredDesignationAndMarksMalformedResponseStale()
    {
        var player = new Player("p1", "Player", "QB", "BUF", Injury: "Out");
        var snapshot = new LeagueSnapshot
        {
            Context = League, CapturedAt = DateTimeOffset.UtcNow,
            Players = [player], Roster = [new RosterPlayer(player, RosterStatus.Active)]
        };
        var exports = new Exports(Scores(3)) { InjuryBody = """{"injuries":{}}""" };
        var store = new Store(snapshot);
        var state = new LeagueState();
        state.SelectLeague(League);
        state.UpdateSnapshot(snapshot);
        var refresh = new MflRefreshService(exports, new Drafts(), store, state,
            NullLogger<MflRefreshService>.Instance, new Rankings(), Query(exports));

        Assert.True(await refresh.RefreshPlayerStatusesAsync(League, CancellationToken.None));
        Assert.Equal("", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player));
        Assert.False(store.Snapshot.Sections[SnapshotSection.Injuries].IsStale);

        exports.InjuryBody = "{}";
        Assert.False(await refresh.RefreshPlayerStatusesAsync(League, CancellationToken.None));
        Assert.True(store.Snapshot.Sections[SnapshotSection.Injuries].IsStale);
        Assert.Equal("", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player));
    }

    [Fact]
    public async Task FullRefresh_UpdatesStatusOnLastGoodRosterWhenRosterReadFails()
    {
        var player = new Player("p1", "Player", "QB", "BUF", Injury: "Out");
        var snapshot = new LeagueSnapshot
        {
            Context = League, CapturedAt = DateTimeOffset.UtcNow,
            Players = [player], Roster = [new RosterPlayer(player, RosterStatus.Active)]
        };
        var exports = new Exports(Scores(3)) { FailRoster = true };
        var store = new Store(snapshot);
        var state = new LeagueState();
        state.SelectLeague(League);
        var refresh = new MflRefreshService(exports, new Drafts(), store, state,
            NullLogger<MflRefreshService>.Instance, new Rankings(), Query(exports));

        await refresh.RefreshAllAsync(League, RefreshReason.Manual, CancellationToken.None);

        Assert.True(store.Snapshot.Sections[SnapshotSection.Rosters].IsStale);
        Assert.Equal("Q", PlayerAvailability.Code(Assert.Single(store.Snapshot.Roster).Player));
    }

    private static MflLineupQueryService Query(Exports exports) => new(exports, NullLogger<MflLineupQueryService>.Instance);

    private sealed class Exports(params string?[] responses) : IMflExportClient
    {
        public string InjuryBody { get; set; } = """{"injuries":{"injury":{"id":"p1","status":"Questionable"}}}""";
        public bool FailRoster { get; set; }
        public string ByeBody { get; set; } = """{"nflByeWeeks":{"year":"2026","team":{"id":"BUF","bye_week":"3"}}}""";
        public string? LiveBody { get; set; } = LiveScores;
        public List<IReadOnlyDictionary<string, string>?> LiveRequests { get; } = [];
        private int _weeklyCalls;
        public List<IReadOnlyDictionary<string, string>?> WeeklyRequests { get; } = [];
        public int RosterStatusRequests { get; private set; }
        public Task<MflResult<string>> ExportAsync(LeagueContext league, string requestType,
            IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            if (requestType == "rosters" && FailRoster)
                return Task.FromResult(MflResult<string>.Failure("Unavailable", "test"));
            string? body;
            if (requestType == "weeklyResults")
            {
                WeeklyRequests.Add(parameters);
                body = responses[Math.Min(_weeklyCalls++, responses.Length - 1)];
            }
            else if (requestType == "liveScoring")
            {
                LiveRequests.Add(parameters);
                body = LiveBody;
            }
            else if (requestType == "playerRosterStatus")
            {
                RosterStatusRequests++;
                body = """{"playerRosterStatus":{"player":{"id":"p1","roster_franchise":{"id":"0001","status":"S"}}}}""";
            }
            else body = requestType switch
            {
                "mfl_status" => """{"mfl_status":{"weeks":{"CurrentWeek":"3","LineupWeek":"4","CompletedWeek":"2"}}}""",
                "players" => """{"players":{"player":{"id":"p1","name":"Player","position":"QB","team":"BUF"}}}""",
                "injuries" => InjuryBody,
                "nflByeWeeks" => ByeBody,
                "rosters" => """{"rosters":{"franchise":{"id":"0001","player":{"id":"p1","status":"ROSTER"}}}}""",
                "league" => """{"league":{"startWeek":"1","endWeek":"18"}}""",
                _ => "{}"
            };
            return Task.FromResult(body is null ? MflResult<string>.Failure("Unavailable", "test") : MflResult<string>.Success(body, "test"));
        }
    }

    private sealed class Store(LeagueSnapshot snapshot) : ILeagueSnapshotStore
    {
        public LeagueSnapshot Snapshot { get; private set; } = snapshot;
        public Task<LeagueSnapshot?> GetAsync(LeagueContext league, CancellationToken cancellationToken) => Task.FromResult<LeagueSnapshot?>(Snapshot);
        public Task SaveAsync(LeagueSnapshot value, CancellationToken cancellationToken) { Snapshot = value; return Task.CompletedTask; }
    }
    private sealed class Rankings : IPlayerRankingClient
    {
        public Task<RankingLoadResult> GetRankingsAsync(SeasonContext season, CancellationToken cancellationToken) => Task.FromResult(new RankingLoadResult([], false));
    }
    private sealed class Drafts : IMflDraftClient
    {
        public Task<MflResult<DraftState>> GetDraftStateAsync(LeagueContext league, CancellationToken cancellationToken) => Task.FromResult(MflResult<DraftState>.Failure("Unavailable", "test"));
        public Task<MflResult<string>> SubmitAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
