using System.Diagnostics;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class RenderedPlayerRankingClient(IPlayerRankingCache cache, PlayerRankingOptions options, ILogger<RenderedPlayerRankingClient> logger) : IPlayerRankingClient
{
    private readonly SemaphoreSlim _browserGate = new(2, 2);
    private static readonly (string Position, string Slug)[] Sources = [("QB", "quarterback"), ("RB", "running-back"), ("WR", "wide-receiver"), ("TE", "tight-end"), ("DEF", "defense"), ("K", "kicker")];
    public async Task<RankingLoadResult> GetRankingsAsync(SeasonContext season, CancellationToken cancellationToken)
    {
        var tasks = Sources.Select(async source =>
        {
            var url = new Uri($"https://www.thefantasyfootballers.com/{season.Year}-{source.Slug}-rankings/");
            var result = await cache.GetAsync(source.Position, url, async (output, token) =>
            {
                await _browserGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                var start = new ProcessStartInfo(options.NodeExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Path.Combine(options.ScriptsDirectory, "run-ffb-rankings.mjs"));
                start.ArgumentList.Add(source.Position);
                start.ArgumentList.Add(season.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(output);
                using var process = Process.Start(start) ?? throw new IOException("Ranking extractor could not start.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(100));
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new IOException("Ranking extraction failed.");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (!process.HasExited) process.Kill(true); throw new IOException("Ranking extraction timed out."); }
                finally { if (!process.HasExited) process.Kill(true); }
                }
                finally { _browserGate.Release(); }
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("External rankings {Position}: {RowCount} rows, stale {IsStale}", source.Position, result.Rankings.Count, result.IsStale);
            return result;
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new(results.SelectMany(x => x.Rankings).ToArray(), results.Any(x => x.IsStale),
            results.Any(x => x.IsStale) ? "Some player rankings are stale or unavailable; last valid daily rankings were preserved." : null);
    }
}
