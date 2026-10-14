using System.Diagnostics;
using System.Text.Json.Nodes;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflExportClient(IHttpClientFactory clients, ILogger<MflExportClient> logger) : IMflExportClient
{
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(1.1);
    private readonly SemaphoreSlim _requestStartGate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public async Task<MflResult<string>> ExportAsync(LeagueContext league, string requestType, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
    {
        var operationStarted = Stopwatch.GetTimestamp();
        var schedulingWait = TimeSpan.Zero;
        var networkTime = TimeSpan.Zero;
        DateTimeOffset? firstRequestStartedAt = null;
        var query = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>())
        {
            ["TYPE"] = requestType,
            ["L"] = league.LeagueId,
            ["JSON"] = "1"
        };
        // Injuries is a public, season-wide export. The league host rejects this
        // request when it carries L, even though other exports need league context.
        var isPublicSeasonExport = requestType is "injuries" or "nflByeWeeks";
        if (isPublicSeasonExport) query.Remove("L");
        var requestUri = requestType == "mfl_status"
            ? new Uri($"https://api.myfantasyleague.com/fflnetdynamic{league.Season.Year}/mfl_status.json")
            : FormHelpers.WithQuery(isPublicSeasonExport
                ? new Uri($"https://api.myfantasyleague.com/{league.Season.Year}/export")
                : league.ExportUri, query);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            schedulingWait += await ReserveRequestStartAsync(cancellationToken).ConfigureAwait(false);
            firstRequestStartedAt ??= DateTimeOffset.UtcNow;
            var networkStarted = Stopwatch.GetTimestamp();
            try
            {
                using var response = await clients.CreateClient("mfl").SendAsync(request, cancellationToken).ConfigureAwait(false);
                networkTime += Stopwatch.GetElapsedTime(networkStarted);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var playerCount = CountPlayerRecords(body);
                logger.LogInformation("MFL API response {RequestType}: started {RequestStartedAt}, HTTP {StatusCode}, {ResponseBytes} bytes, {PlayerCount} player records, elapsed {ElapsedMs} ms, network {NetworkMs} ms, scheduling wait {SchedulingWaitMs} ms, shape {Shape}, correlation {CorrelationId}",
                    requestType, firstRequestStartedAt, (int)response.StatusCode, System.Text.Encoding.UTF8.GetByteCount(body), playerCount,
                    ElapsedMilliseconds(operationStarted), Milliseconds(networkTime), Milliseconds(schedulingWait), ResponseShape(body), correlationId);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    logger.LogWarning("MFL API throttled {RequestType}; retry {RetryNumber}, correlation {CorrelationId}",
                        requestType, attempt + 1, correlationId);
                    await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("MFL API request {RequestType} failed with HTTP {StatusCode} after {ElapsedMs} ms, correlation {CorrelationId}",
                        requestType, (int)response.StatusCode, ElapsedMilliseconds(operationStarted), correlationId);
                    return MflResult<string>.Failure($"MFL returned HTTP {(int)response.StatusCode}.", correlationId, status: response.StatusCode);
                }
                var parsed = MflHttpResult.ParseMutation(body);
                if (!parsed.Success)
                {
                    logger.LogWarning("MFL API request {RequestType} was rejected with code {ErrorCode} after {ElapsedMs} ms, correlation {CorrelationId}",
                        requestType, parsed.Code ?? "UNKNOWN", ElapsedMilliseconds(operationStarted), correlationId);
                    return MflResult<string>.Failure(parsed.Message ?? "MFL rejected the read.", correlationId, parsed.Code, response.StatusCode);
                }
                if (playerCount < 0)
                    logger.LogWarning("MFL API response {RequestType} was not valid JSON, correlation {CorrelationId}", requestType, correlationId);
                return MflResult<string>.Success(body, correlationId, response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                networkTime += Stopwatch.GetElapsedTime(networkStarted);
                if (attempt == 2)
                {
                    logger.LogWarning("MFL export {Type} failed after {ElapsedMs} ms for correlation {CorrelationId}: {ExceptionType}",
                        requestType, ElapsedMilliseconds(operationStarted), correlationId, ex.GetType().Name);
                    return MflResult<string>.Failure("MFL could not be reached.", correlationId);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
                request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
            }
        }
        return MflResult<string>.Failure("MFL could not be reached.", correlationId);
    }

    private async Task<TimeSpan> ReserveRequestStartAsync(CancellationToken cancellationToken)
    {
        var waitStarted = Stopwatch.GetTimestamp();
        await _requestStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = _nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;
            return Stopwatch.GetElapsedTime(waitStarted);
        }
        finally { _requestStartGate.Release(); }
    }

    private static long ElapsedMilliseconds(long started) => Milliseconds(Stopwatch.GetElapsedTime(started));
    private static long Milliseconds(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date && date > DateTimeOffset.UtcNow) return date - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(3 * (1 << attempt));
    }

    private static int CountPlayerRecords(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            return JsonTraversal.ObjectsNamed(root, "player").Count() +
                JsonTraversal.ObjectsNamed(root, "playerRosterStatus")
                    .Concat(JsonTraversal.ObjectsNamed(root, "playerStatus"))
                    .Count(x => x.Any(pair => pair.Value is JsonValue &&
                        (pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("player_id", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("player", StringComparison.OrdinalIgnoreCase))));
        }
        catch (System.Text.Json.JsonException) { return -1; }
    }

    internal static string ResponseShape(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return DescribeShape(node, 3);
        }
        catch (System.Text.Json.JsonException) { return "non-json"; }
    }

    private static string DescribeShape(JsonNode? node, int depth) => node switch
    {
        JsonObject obj when depth > 0 => "object(" + string.Join(',', obj.Take(8)
            .Select(x => $"{x.Key}:{DescribeShape(x.Value, depth - 1)}")) + ")",
        JsonObject => "object",
        JsonArray array => $"array({array.Count})[" + string.Join(',',
            array.OfType<JsonObject>().FirstOrDefault()?.Select(x => x.Key).Take(8) ?? []) + "]",
        _ => "scalar"
    };
}

public sealed class MflImportClient(IHttpClientFactory clients, ILogger<MflImportClient> logger) : IMflImportClient
{
    public async Task<MflResult<string>> ImportAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (league.Season.IsReadOnly) return MflResult<string>.Failure("Historical seasons are read-only.", Guid.NewGuid().ToString("N"), "READ_ONLY_SEASON");
        var form = new Dictionary<string, string>(parameters) { ["L"] = league.LeagueId, ["JSON"] = "1" };
        var request = new HttpRequestMessage(HttpMethod.Post, league.ImportUri) { Content = new FormUrlEncodedContent(form) };
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        try
        {
            using var response = await clients.CreateClient("mfl").SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = MflHttpResult.ParseMutation(body);
            if (!response.IsSuccessStatusCode || !parsed.Success)
                return MflResult<string>.Failure(parsed.Message ?? $"MFL returned HTTP {(int)response.StatusCode}.", correlationId, parsed.Code, response.StatusCode);
            return MflResult<string>.Success(body, correlationId, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("MFL mutation failed for correlation {CorrelationId}: {ExceptionType}", correlationId, ex.GetType().Name);
            return MflResult<string>.Failure("The mutation outcome is unknown because MFL could not be reached. It was not retried.", correlationId, "UNKNOWN_OUTCOME");
        }
    }
}

public sealed class MflLeagueDiscoveryClient(IHttpClientFactory clients, ILogger<MflLeagueDiscoveryClient> logger) : IMflLeagueDiscoveryClient
{
    public MflLeagueDiscoveryClient(IHttpClientFactory clients)
        : this(clients, Microsoft.Extensions.Logging.Abstractions.NullLogger<MflLeagueDiscoveryClient>.Instance) { }

    public async Task<MflResult<IReadOnlyList<LeagueSummary>>> GetMyLeaguesAsync(SeasonContext season, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.myfantasyleague.com/{season.Year}/export?TYPE=myleagues&JSON=1");
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient("mfl").SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("MFL league discovery request failed with {ExceptionType}, correlation {CorrelationId}",
                ex.GetType().Name, correlationId);
            return MflResult<IReadOnlyList<LeagueSummary>>.Failure("MFL league discovery could not be reached.", correlationId);
        }
        using (response)
        {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("MFL API response myleagues: HTTP {StatusCode}, {ResponseBytes} bytes, correlation {CorrelationId}",
            (int)response.StatusCode, body.Length, correlationId);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("MFL league discovery failed with HTTP {StatusCode}, correlation {CorrelationId}",
                (int)response.StatusCode, correlationId);
            return MflResult<IReadOnlyList<LeagueSummary>>.Failure("MFL league discovery failed.", correlationId, status: response.StatusCode);
        }
        try
        {
            var root = JsonNode.Parse(body);
            var nodes = JsonTraversal.ObjectsNamed(root, "league");
            var leagues = nodes.Select(x =>
            {
                var id = JsonTraversal.Value(x, "league_id", "id") ?? string.Empty;
                var year = int.TryParse(JsonTraversal.Value(x, "year"), out var parsedYear) ? parsedYear : season.Year;
                var url = JsonTraversal.Value(x, "url", "host") ?? "https://api.myfantasyleague.com";
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                var franchiseId = JsonTraversal.Value(x, "franchise_id", "franchiseId");
                var commissioner = franchiseId == "0000" || JsonTraversal.Value(x, "is_commissioner") == "1";
                return new LeagueSummary(id, year, JsonTraversal.Value(x, "name") ?? id, new Uri(url), franchiseId, commissioner);
            }).Where(x => !string.IsNullOrWhiteSpace(x.LeagueId)).ToArray();
            logger.LogInformation("MFL league discovery parsed {LeagueCount} leagues, correlation {CorrelationId}", leagues.Length, correlationId);
            return MflResult<IReadOnlyList<LeagueSummary>>.Success(leagues, correlationId, response.StatusCode);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or UriFormatException)
        {
            logger.LogWarning("MFL league discovery parsing failed with {ExceptionType}, correlation {CorrelationId}",
                ex.GetType().Name, correlationId);
            return MflResult<IReadOnlyList<LeagueSummary>>.Failure("MFL returned an unrecognized league list.", correlationId, "INVALID_RESPONSE", response.StatusCode);
        }
        }
    }
}

public sealed class MflDraftClient(IMflExportClient exports, IHttpClientFactory clients) : IMflDraftClient
{
    public async Task<MflResult<DraftState>> GetDraftStateAsync(LeagueContext league, CancellationToken cancellationToken)
    {
        var result = await exports.ExportAsync(league, "draftResults", null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) return MflResult<DraftState>.Failure(result.UserMessage ?? "Draft state unavailable.", result.CorrelationId, result.MflErrorCode, result.StatusCode);
        var state = MflJsonParser.ParseDraft(result.Value!);
        return MflResult<DraftState>.Success(state, result.CorrelationId, result.StatusCode ?? System.Net.HttpStatusCode.OK);
    }

    public async Task<MflResult<string>> SubmitAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (league.Season.IsReadOnly) return MflResult<string>.Failure("Historical seasons are read-only.", Guid.NewGuid().ToString("N"), "READ_ONLY_SEASON");
        var form = new Dictionary<string, string>(parameters) { ["L"] = league.LeagueId, ["JSON"] = "1" };
        var request = new HttpRequestMessage(HttpMethod.Post, league.LiveDraftUri) { Content = new FormUrlEncodedContent(form) };
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        using var response = await clients.CreateClient("mfl").SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = MflHttpResult.ParseMutation(body);
        return response.IsSuccessStatusCode && parsed.Success
            ? MflResult<string>.Success(body, correlationId, response.StatusCode)
            : MflResult<string>.Failure(parsed.Message ?? "MFL rejected the draft command.", correlationId, parsed.Code, response.StatusCode);
    }
}

internal static class JsonTraversal
{
    public static IEnumerable<JsonObject> ObjectsNamed(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value is JsonObject found) yield return found;
                    if (pair.Value is JsonArray array) foreach (var child in array.OfType<JsonObject>()) yield return child;
                }
                foreach (var child in ObjectsNamed(pair.Value, name)) yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) foreach (var found in ObjectsNamed(child, name)) yield return found;
        }
    }

    public static string? Value(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var pair = obj.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (pair.Key is not null && pair.Value is not null) return pair.Value.ToString();
        }
        return null;
    }
}
