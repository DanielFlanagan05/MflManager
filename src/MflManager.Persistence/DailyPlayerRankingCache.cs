using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using MflManager.Application;
using MflManager.Domain;

namespace MflManager.Persistence;

public sealed record RankingCacheMetadata(DateTimeOffset RetrievedAt, string RetrievalDate, string SourceUrl, string Position, int RowCount, string Sha256);

public sealed class DailyPlayerRankingCache(PlayerRankingOptions options, TimeProvider clock) : IPlayerRankingCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    public static readonly IReadOnlyDictionary<string, string> Slugs = new Dictionary<string, string>
    { ["QB"] = "quarterback", ["RB"] = "running-back", ["WR"] = "wide-receiver", ["TE"] = "tight-end", ["DEF"] = "defense", ["K"] = "kicker" };

    public async Task<RankingLoadResult> GetAsync(string position, Uri source, Func<string, CancellationToken, Task> extract, CancellationToken cancellationToken)
    {
        position = WaiverPlayerSelector.NormalizePosition(position);
        var slug = Slugs[position];
        // Partition by season as source URLs are season-specific.
        var season = Regex.Match(source.AbsolutePath, @"/([0-9]{4})-").Groups[1].Value;
        if (season.Length != 4) throw new ArgumentException("Invalid ranking source season.", nameof(source));
        var directory = Path.Combine(options.CacheDirectory, season);
        var date = clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filename = Path.Combine(directory, $"ffb-{slug}-rankings-{date}.csv");
        var gate = _gates.GetOrAdd(filename, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            if (await ReadValidAsync(filename, position, source, cancellationToken).ConfigureAwait(false) is { } current)
                return new(current, false);
            if (!File.Exists(filename + ".request"))
            {
                // Atomically claim this day before extraction. A failed attempt must not re-scrape on Refresh.
                try
                {
                    await using var claim = new FileStream(filename + ".request", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await claim.WriteAsync(Encoding.UTF8.GetBytes(clock.GetUtcNow().ToString("O")), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { return await LastGoodAsync(directory, slug, position, source, cancellationToken).ConfigureAwait(false); }
                try
                {
                    await extract(filename, cancellationToken).ConfigureAwait(false);
                    var csv = await File.ReadAllTextAsync(filename, cancellationToken).ConfigureAwait(false);
                    var rows = ParseCsv(csv, position);
                    var metadata = new RankingCacheMetadata(clock.GetUtcNow(), date, source.AbsoluteUri, position, rows.Count, Hash(csv));
                    await AtomicWriteAsync(filename + ".json", JsonSerializer.Serialize(metadata), cancellationToken).ConfigureAwait(false);
                    return new(rows, false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missing executable made no source request. Allow a corrected runtime to start.
                    if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) File.Delete(filename + ".request");
                    // Never expose process output, HTML, credentials or player payloads.
                }
            }
            return await LastGoodAsync(directory, slug, position, source, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static async Task<RankingLoadResult> LastGoodAsync(string directory, string slug, string position, Uri source, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory, $"ffb-{slug}-rankings-*.csv").OrderDescending())
            if (await ReadValidAsync(file, position, source, cancellationToken).ConfigureAwait(false) is { } rows)
                return new(rows, true, "Player rankings are stale; the last valid daily cache is being used.");
        return new([], true, "Player rankings are unavailable; today's retrieval failed. The next automatic source attempt is tomorrow.");
    }

    private static async Task<IReadOnlyList<PlayerRanking>?> ReadValidAsync(string filename, string position, Uri source, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<RankingCacheMetadata>(await File.ReadAllTextAsync(filename + ".json", cancellationToken).ConfigureAwait(false));
            var csv = await File.ReadAllTextAsync(filename, cancellationToken).ConfigureAwait(false);
            var rows = ParseCsv(csv, position);
            var expectedName = $"ffb-{Slugs[position]}-rankings-{metadata?.RetrievalDate}.csv";
            return metadata is not null && Path.GetFileName(filename) == expectedName && metadata.Position == position &&
                metadata.SourceUrl == source.AbsoluteUri && metadata.RowCount == rows.Count && metadata.Sha256 == Hash(csv) ? rows : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or MalformedLineException) { return null; }
    }

    public static IReadOnlyList<PlayerRanking> ParseCsv(string csv, string position)
    {
        position = WaiverPlayerSelector.NormalizePosition(position);
        if (!Slugs.ContainsKey(position)) throw new FormatException("Unsupported ranking position.");
        using var parser = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields()?.Select(x => x.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray() ?? [];
        var required = new[] { "player name", "player rank", "team code", "opposing team code", "home or away" };
        if (required.Any(x => headers.Count(h => h == x) != 1)) throw new FormatException("Missing or duplicate required ranking CSV columns.");
        var rows = new List<PlayerRanking>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (fields.Length != headers.Length) throw new FormatException("Incomplete ranking CSV row.");
            string Value(string key) => fields[Array.IndexOf(headers, key)].Trim();
            if (required.Any(x => Value(x).Length == 0) || !int.TryParse(Value("player rank"), out var rank) || rank != rows.Count + 1 ||
                !Regex.IsMatch(Value("team code"), "^[A-Z]{2,3}$") || !Regex.IsMatch(Value("opposing team code"), "^[A-Z]{2,3}$") ||
                Value("home or away") is not ("home" or "away")) throw new FormatException("Invalid required ranking CSV values.");
            rows.Add(new(Value("player name"), WaiverPlayerSelector.NormalizeTeam(Value("team code")), position, rank,
                WaiverPlayerSelector.NormalizeTeam(Value("opposing team code")), Value("home or away")));
        }
        if (rows.Count == 0 || rows.Select(x => WaiverPlayerSelector.Identity(x.PlayerName, x.Team, x.Position)).Distinct().Count() != rows.Count)
            throw new FormatException("Empty or ambiguous ranking CSV.");
        return rows;
    }

    private static string Hash(string csv) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
