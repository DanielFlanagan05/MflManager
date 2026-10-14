using MflManager.Domain;
using MflManager.Persistence;
using Microsoft.Data.Sqlite;

namespace MflManager.IntegrationTests;

public sealed class SqlitePersistenceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "mfl-manager-tests", Guid.NewGuid().ToString("N") + ".db");
    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SnapshotAndJournal_RoundTripWithoutSecrets()
    {
        var database = new SqliteDatabase(new(_path));
        var snapshots = new SqliteSnapshotStore(database);
        var journal = new SqliteOperationJournal(database);
        var context = new LeagueContext("12345", SeasonContext.Current(2026), new Uri("https://www73.myfantasyleague.com"), "0001");
        var snapshot = new LeagueSnapshot
        {
            Context = context,
            CapturedAt = DateTimeOffset.UtcNow,
            Players = [new("1", "Test Player", "RB", "DET")],
            WeeklyPlayerPoints = new Dictionary<string, decimal> { ["1"] = 12.75m },
            WeeklyPlayerPointsWeek = 2,
            NflByeWeeks = new Dictionary<string, int> { ["DET"] = 5 },
            FreeAgentCopies = new Dictionary<string, int> { ["1"] = 2 }
        };
        await snapshots.SaveAsync(snapshot, CancellationToken.None);
        var loaded = await snapshots.GetAsync(context, CancellationToken.None);
        Assert.Equal("Test Player", Assert.Single(loaded!.Players).Name);
        Assert.Equal(12.75m, loaded.WeeklyPlayerPoints["1"]);
        Assert.True(loaded.HasPointsForWeek(2));
        Assert.False(loaded.HasPointsForWeek(3));
        Assert.Equal(2, loaded.FreeAgentCopies["1"]);
        Assert.Equal(5, loaded.NflByeWeeks["DET"]);

        var entry = new OperationJournalEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "12345", "0001", MutationKind.AddDrop, "{\"ADD\":\"1\"}", true, "Accepted", true);
        await journal.AppendAsync(entry, CancellationToken.None);
        var recent = await journal.GetRecentAsync("12345", 10, CancellationToken.None);
        Assert.Equal(entry.OperationId, Assert.Single(recent).OperationId);
        Assert.DoesNotContain("PASSWORD", recent[0].SanitizedRequestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MFL_USER_ID", recent[0].SanitizedRequestJson, StringComparison.OrdinalIgnoreCase);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" }) { var file = _path + suffix; if (File.Exists(file)) File.Delete(file); }
        return Task.CompletedTask;
    }
}
