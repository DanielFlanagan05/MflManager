using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MflManager.Persistence;

public sealed record PersistenceOptions(string DatabasePath);

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMflPersistence(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton(new PersistenceOptions(databasePath));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ILeagueSnapshotStore, SqliteSnapshotStore>();
        services.AddSingleton<IOperationJournal, SqliteOperationJournal>();
        return services;
    }
}

public sealed class SqliteDatabase(PersistenceOptions options)
{
    private int _initialized;
    public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath) ?? AppContext.BaseDirectory);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS snapshots (
              season INTEGER NOT NULL,
              league_id TEXT NOT NULL,
              captured_at TEXT NOT NULL,
              json TEXT NOT NULL,
              PRIMARY KEY (season, league_id)
            );
            CREATE TABLE IF NOT EXISTS operations (
              operation_id TEXT PRIMARY KEY,
              timestamp TEXT NOT NULL,
              league_id TEXT NOT NULL,
              franchise_id TEXT NULL,
              operation TEXT NOT NULL,
              request_json TEXT NOT NULL,
              accepted INTEGER NOT NULL,
              result TEXT NOT NULL,
              verified INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_operations_league_timestamp ON operations(league_id, timestamp DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteSnapshotStore(SqliteDatabase database) : ILeagueSnapshotStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LeagueSnapshot?> GetAsync(LeagueContext league, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM snapshots WHERE season=$season AND league_id=$league";
        command.Parameters.AddWithValue("$season", league.Season.Year);
        command.Parameters.AddWithValue("$league", league.LeagueId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<LeagueSnapshot>(value, Json);
    }

    public async Task SaveAsync(LeagueSnapshot snapshot, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots(season,league_id,captured_at,json) VALUES($season,$league,$captured,$json)
            ON CONFLICT(season,league_id) DO UPDATE SET captured_at=excluded.captured_at,json=excluded.json
            """;
        command.Parameters.AddWithValue("$season", snapshot.Context.Season.Year);
        command.Parameters.AddWithValue("$league", snapshot.Context.LeagueId);
        command.Parameters.AddWithValue("$captured", snapshot.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteOperationJournal(SqliteDatabase database) : IOperationJournal
{
    public async Task AppendAsync(OperationJournalEntry entry, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(operation_id,timestamp,league_id,franchise_id,operation,request_json,accepted,result,verified)
            VALUES($id,$timestamp,$league,$franchise,$operation,$request,$accepted,$result,$verified)
            """;
        command.Parameters.AddWithValue("$id", entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$league", entry.LeagueId);
        command.Parameters.AddWithValue("$franchise", (object?)entry.FranchiseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", entry.Operation.ToString());
        command.Parameters.AddWithValue("$request", entry.SanitizedRequestJson);
        command.Parameters.AddWithValue("$accepted", entry.Accepted ? 1 : 0);
        command.Parameters.AddWithValue("$result", entry.Result);
        command.Parameters.AddWithValue("$verified", entry.Verified ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperationJournalEntry>> GetRecentAsync(string leagueId, int count, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id,timestamp,league_id,franchise_id,operation,request_json,accepted,result,verified
            FROM operations WHERE league_id=$league ORDER BY timestamp DESC LIMIT $count
            """;
        command.Parameters.AddWithValue("$league", leagueId);
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 500));
        var entries = new List<OperationJournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new(
                Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), Enum.Parse<MutationKind>(reader.GetString(4)),
                reader.GetString(5), reader.GetInt32(6) == 1, reader.GetString(7), reader.GetInt32(8) == 1));
        }
        return entries;
    }
}

public sealed class InMemorySecureSessionStore : ISecureSessionStore
{
    private (string Username, string UserId)? _value;
    public Task SaveAsync(string username, string userId, CancellationToken cancellationToken) { _value = (username, userId); return Task.CompletedTask; }
    public Task<(string Username, string UserId)?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_value);
    public Task ForgetAsync(CancellationToken cancellationToken) { _value = null; return Task.CompletedTask; }
}

public sealed class InMemoryMflCookieStore : IMflCookieStore
{
    private string? _cookie;
    public Task SaveAsync(string cookieValue, CancellationToken cancellationToken) { _cookie = cookieValue; return Task.CompletedTask; }
    public Task<string?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_cookie);
    public Task ForgetAsync(CancellationToken cancellationToken) { _cookie = null; return Task.CompletedTask; }
}
