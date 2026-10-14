using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflLineupQueryService(IMflExportClient exports, ILogger<MflLineupQueryService> logger) : IMflLineupQueryService
{
    public async Task<MflResult<WeeklyLineup>> GetAsync(LeagueContext league, int week,
        IReadOnlyList<string> rosterPlayerIds, CancellationToken cancellationToken, string? weeklyResultsBody = null)
    {
        if (week <= 0) throw new ArgumentOutOfRangeException(nameof(week));
        if (rosterPlayerIds.Count == 0)
            return MflResult<WeeklyLineup>.Failure("MFL did not return any players for this team's roster.", Guid.NewGuid().ToString("N"), "EMPTY_ROSTER");

        WeeklyLineup? completeLineup = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = attempt == 0 ? weeklyResultsBody : null;
            if (body is null)
            {
                var weeklyResult = await exports.ExportAsync(league, "weeklyResults", new Dictionary<string, string>
                {
                    ["W"] = week.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["F"] = league.FranchiseId ?? string.Empty
                }, cancellationToken).ConfigureAwait(false);
                body = weeklyResult.IsSuccess ? weeklyResult.Value : null;
            }
            try
            {
                var parsed = body is null ? null : MflJsonParser.ParseWeeklyResultsLineup(body, week, league.FranchiseId);
                if (parsed?.Week == week)
                {
                    completeLineup = parsed;
                    var scoreDiagnostics = MflJsonParser.InspectWeeklyPlayerScores(body!, league.FranchiseId);
                    logger.LogInformation("MFL starting lineup parsed {StarterCount} starters and {PlayerScoreCount} player scores for week {Week} from weekly results",
                        completeLineup.StarterPlayerIds.Count, completeLineup.PlayerPoints.Count, week);
                    logger.LogInformation("WeeklyResults score diagnostics: {PlayerRecordCount} selected-team player records, {NumericScoreCount} numeric scores, {NonZeroScoreCount} nonzero scores, {TotalPoints} total points; player fields {PlayerFields}",
                        scoreDiagnostics.PlayerRecords, scoreDiagnostics.NumericScores, scoreDiagnostics.NonZeroScores,
                        scoreDiagnostics.TotalPoints, scoreDiagnostics.PlayerFields);
                    break;
                }
                logger.LogWarning("MFL weekly results failed week validation: requested {Week}, returned {ReturnedWeek}, attempt {Attempt}",
                    week, parsed?.Week ?? 0, attempt + 1);
            }
            catch (System.Text.Json.JsonException)
            {
                logger.LogWarning("MFL weekly results had an unrecognized shape for week {Week}, attempt {Attempt}", week, attempt + 1);
            }
            if (attempt == 0) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        if (completeLineup is null) throw new WeeklyPointsValidationException(week);
        // During the live week weeklyResults can contain only id/status, with
        // scores published separately in liveScoring. DETAILS includes the bench.
        if (completeLineup.PlayerPoints.Count == 0)
        {
            var live = await GetLiveScoresAsync(league, week, cancellationToken).ConfigureAwait(false);
            completeLineup = completeLineup with { PlayerPoints = live.PlayerPoints };
        }
        if (completeLineup.StarterPlayerIds.Count > 0)
            return MflResult<WeeklyLineup>.Success(completeLineup, Guid.NewGuid().ToString("N"));

        var result = await QueryRosterStatusAsync(league, week, rosterPlayerIds, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return MflResult<WeeklyLineup>.Failure(result.UserMessage ?? "The starting lineup could not be loaded from MFL.",
                result.CorrelationId, result.MflErrorCode, result.StatusCode);
        try
        {
            var lineup = MflJsonParser.ParsePlayerRosterStatus(result.Value!, week, league.FranchiseId)
                with { PlayerPoints = completeLineup.PlayerPoints };
            logger.LogInformation("MFL starting lineup parsed {StarterCount} starters for week {Week} from roster status fallback",
                lineup.StarterPlayerIds.Count, week);
            return MflResult<WeeklyLineup>.Success(lineup,
                result.CorrelationId, result.StatusCode ?? System.Net.HttpStatusCode.OK);
        }
        catch (System.Text.Json.JsonException)
        {
            return MflResult<WeeklyLineup>.Failure("MFL returned an unrecognized starting-lineup response.", result.CorrelationId,
                "INVALID_RESPONSE", result.StatusCode);
        }
    }

    private async Task<WeeklyLineup> GetLiveScoresAsync(LeagueContext league, int week, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await exports.ExportAsync(league, "liveScoring", new Dictionary<string, string>
            {
                ["W"] = week.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DETAILS"] = "1"
            }, cancellationToken).ConfigureAwait(false);
            try
            {
                var live = result.IsSuccess ? MflJsonParser.ParseLiveScoringLineup(result.Value!, league.FranchiseId) : null;
                if (live?.Week == week)
                {
                    logger.LogInformation("MFL live scoring parsed {PlayerScoreCount} player scores for week {Week}", live.PlayerPoints.Count, week);
                    return live;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                logger.LogWarning("MFL live scoring had an unrecognized shape for week {Week}, attempt {Attempt}", week, attempt + 1);
            }
            if (attempt == 0) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        throw new WeeklyPointsValidationException(week);
    }

    public async Task<MflResult<IReadOnlyList<PlayerRosterAvailability>>> GetAvailabilityAsync(
        LeagueContext league, int week, IReadOnlyList<string> playerIds, CancellationToken cancellationToken)
    {
        if (playerIds.Count == 0)
            return MflResult<IReadOnlyList<PlayerRosterAvailability>>.Success([], Guid.NewGuid().ToString("N"), System.Net.HttpStatusCode.OK);

        var statuses = new List<PlayerRosterAvailability>();
        foreach (var batch in playerIds.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            var result = await QueryRosterStatusAsync(league, week, batch, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return MflResult<IReadOnlyList<PlayerRosterAvailability>>.Failure(
                    result.UserMessage ?? "Player availability could not be verified with MFL.", result.CorrelationId,
                    result.MflErrorCode, result.StatusCode);
            try
            {
                statuses.AddRange(MflJsonParser.ParsePlayerAvailability(result.Value!, league.FranchiseId));
            }
            catch (System.Text.Json.JsonException)
            {
                return MflResult<IReadOnlyList<PlayerRosterAvailability>>.Failure(
                    "MFL returned an unrecognized player-availability response.", result.CorrelationId,
                    "INVALID_RESPONSE", result.StatusCode);
            }
        }

        return MflResult<IReadOnlyList<PlayerRosterAvailability>>.Success(
            statuses.DistinctBy(x => x.PlayerId, StringComparer.OrdinalIgnoreCase).ToArray(),
            Guid.NewGuid().ToString("N"), System.Net.HttpStatusCode.OK);
    }

    private Task<MflResult<string>> QueryRosterStatusAsync(LeagueContext league, int week, IReadOnlyList<string> playerIds,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["P"] = string.Join(',', playerIds),
            ["W"] = week.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(league.FranchiseId)) parameters["F"] = league.FranchiseId;
        return exports.ExportAsync(league, "playerRosterStatus", parameters, cancellationToken);
    }
}
