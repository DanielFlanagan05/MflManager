using System.Net.Http.Headers;
using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class FantasyProsRankingClient(IHttpClientFactory clients, ILogger<FantasyProsRankingClient> logger) : IFantasyProsRankingClient
{
    private static readonly string[] Positions = ["QB", "RB", "WR", "TE", "K", "DEF"];

    public async Task<MflResult<IReadOnlyList<FantasyProsRanking>>> GetConsensusRankingsAsync(SeasonContext season, string position, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("FANTASYPROS_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return MflResult<IReadOnlyList<FantasyProsRanking>>.Failure("FantasyPros API key is not configured.", Guid.NewGuid().ToString("N"), "MISSING_FANTASYPROS_KEY");
        var normalized = Positions.Contains(position, StringComparer.OrdinalIgnoreCase) ? position.ToUpperInvariant() : "RB";
        var uri = $"https://api.fantasypros.com/public/v2/json/nfl/{season.Year}/consensus-rankings?position={normalized}&scoring=PPR";
        var correlation = Guid.NewGuid().ToString("N");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation);
            using var response = await clients.CreateClient("fantasypros").SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return MflResult<IReadOnlyList<FantasyProsRanking>>.Failure($"FantasyPros returned HTTP {(int)response.StatusCode}.", correlation, status: response.StatusCode);
            using var doc = JsonDocument.Parse(body);
            var list = doc.RootElement.TryGetProperty("players", out var players) ? players.EnumerateArray().Select(x => new FantasyProsRanking(
                x.TryGetProperty("player_name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                x.TryGetProperty("player_team_id", out var team) ? team.GetString() ?? string.Empty : string.Empty,
                normalized,
                x.TryGetProperty("rank_ecr", out var rank) && rank.TryGetInt32(out var r) ? r : int.MaxValue,
                x.TryGetProperty("tier", out var tier) && tier.TryGetInt32(out var t) ? t : null)).Where(x => x.PlayerName.Length > 0).OrderBy(x => x.Rank).ToArray() : [];
            return MflResult<IReadOnlyList<FantasyProsRanking>>.Success(list, correlation, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("FantasyPros rankings failed for correlation {CorrelationId}: {ExceptionType}", correlation, ex.GetType().Name);
            return MflResult<IReadOnlyList<FantasyProsRanking>>.Failure("FantasyPros rankings could not be loaded.", correlation, "FANTASYPROS_UNAVAILABLE");
        }
    }
}
