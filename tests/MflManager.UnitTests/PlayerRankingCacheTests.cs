using MflManager.Application;
using MflManager.Domain;
using MflManager.Persistence;

namespace MflManager.UnitTests;

public sealed class PlayerRankingCacheTests
{
    [Theory]
    [InlineData("Walker, Kenneth III", "Kenneth Walker", "RB", "RB")]
    [InlineData("Pineiro, Eddy", "Eddy Piñeiro", "PK", "K")]
    [InlineData("Seahawks, Seattle", "Seattle Seahawks", "Def", "D/ST")]
    public void NormalizedIdentity_HandlesSourceFormattingWithoutIgnoringTeamOrPosition(string mfl, string source, string mflPosition, string sourcePosition)
    {
        Assert.Equal(WaiverPlayerSelector.Identity(mfl, "SEA", mflPosition), WaiverPlayerSelector.Identity(source, "SEA", sourcePosition));
        Assert.NotEqual(WaiverPlayerSelector.Identity(mfl, "SEA", mflPosition), WaiverPlayerSelector.Identity(source, "NYJ", sourcePosition));
    }
    private const string Csv = "player name,player rank,team code,opposing team code,home or away\r\n\"Test, Player\",1,SEA,SF,home\r\n";

    [Theory]
    [InlineData("QB", "QB")]
    [InlineData("RB", "RB")]
    [InlineData("WR", "WR")]
    [InlineData("TE", "TE")]
    [InlineData("D/ST", "DEF")]
    [InlineData("DST", "DEF")]
    [InlineData("K", "K")]
    public void Csv_ParsesAllSixFormatsWithAuthoritativeSourcePosition(string source, string expected)
    {
        var row = Assert.Single(DailyPlayerRankingCache.ParseCsv(Csv, source));
        Assert.Equal(expected, row.Position);
        Assert.Equal("Test, Player", row.PlayerName);
        Assert.Equal(1, row.PositionRank);
        Assert.Equal("SF", row.Opponent);
    }

    [Theory]
    [InlineData("player name,player rank,team code,home or away\nPlayer,1,SEA,home\n")]
    [InlineData("player name,player rank,team code,opposing team code,home or away\nPlayer,1,,SF,home\n")]
    [InlineData("player name,player rank,team code,opposing team code,home or away\nPlayer,2,SEA,SF,home\n")]
    [InlineData("player name,player rank,team code,opposing team code,home or away\nPlayer,1,SEA,SF\n")]
    [InlineData("player name,player rank,team code,opposing team code,home or away\n")]
    public void Csv_RejectsMissingColumnsValuesAndPartialRows(string csv) => Assert.Throws<FormatException>(() => DailyPlayerRankingCache.ParseCsv(csv, "QB"));

    [Fact]
    public async Task Cache_HitsTodayAttemptsOnlyOnceAndKeepsLastGoodOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mfl-ranking-test-" + Guid.NewGuid().ToString("N"));
        var clock = new TestClock(new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var cache = new DailyPlayerRankingCache(new(directory, "unused"), clock);
        var url = new Uri("https://www.thefantasyfootballers.com/2026-quarterback-rankings/");
        var calls = 0;
        Task Extract(string file, CancellationToken ct) { calls++; return File.WriteAllTextAsync(file, Csv, ct); }
        Assert.False((await cache.GetAsync("QB", url, Extract, default)).IsStale);
        Assert.False((await cache.GetAsync("QB", url, Extract, default)).IsStale);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(directory, "2026", "ffb-quarterback-rankings-2026-09-16.csv.json")));
        clock.Now = clock.Now.AddDays(1);
        Task Fail(string file, CancellationToken ct) { calls++; throw new IOException("Synthetic source failure"); }
        var stale = await cache.GetAsync("QB", url, Fail, default);
        Assert.True(stale.IsStale);
        Assert.Single(stale.Rankings);
        Assert.Single((await cache.GetAsync("QB", url, Fail, default)).Rankings);
        Assert.Equal(2, calls);
        clock.Now = clock.Now.AddDays(1);
        Assert.False((await cache.GetAsync("QB", url, Extract, default)).IsStale);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Cache_RejectsTruncatedCsvWithoutRescrapingSameDay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mfl-ranking-test-" + Guid.NewGuid().ToString("N"));
        var clock = new TestClock(new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var cache = new DailyPlayerRankingCache(new(directory, "unused"), clock);
        var url = new Uri("https://www.thefantasyfootballers.com/2026-quarterback-rankings/");
        var calls = 0;
        Task Extract(string file, CancellationToken ct) { calls++; return File.WriteAllTextAsync(file, Csv, ct); }
        await cache.GetAsync("QB", url, Extract, default);
        await File.WriteAllTextAsync(Path.Combine(directory, "2026", "ffb-quarterback-rankings-2026-09-16.csv"), Csv.Replace("SEA", "XXX"));
        var invalid = await cache.GetAsync("QB", url, Extract, default);
        Assert.True(invalid.IsStale);
        Assert.Empty(invalid.Rankings);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Candidates_ConservativelyMatchAndSortOnlyWithinPosition()
    {
        var snapshot = new LeagueSnapshot
        {
            Context = new("test", SeasonContext.Current(2026), new Uri("https://www1.myfantasyleague.com"), "0001", false), CapturedAt = DateTimeOffset.UtcNow,
            AvailablePlayersReady = true, WeeklyRankingsReady = true,
            FreeAgents = [new("a", "Second, Player", "QB", "JAC"), new("b", "First, Player", "QB", "SEA"), new("c", "First, Player", "QB", "DET"), new("d", "Runner, Player", "RB", "SEA")],
            Rankings = [new("Player Second", "JAX", "QB", 20), new("Player First", "SEA", "QB", 2), new("Player Runner", "SEA", "RB", 1)]
        };
        Assert.Equal(new[] { "b", "a" }, WaiverPlayerSelector.Select(snapshot, "QB").Select(x => x.Player.Id));
        Assert.Equal("QB 20", WaiverPlayerSelector.Select(snapshot, "QB")[1].PositionRank);
        Assert.Equal("d", Assert.Single(WaiverPlayerSelector.Select(snapshot, "RB")).Player.Id);
        Assert.Empty(WaiverPlayerSelector.Select(snapshot with { WeeklyRankingsReady = false }, "QB"));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
}
