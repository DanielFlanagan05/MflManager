using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflLeagueRankingsService(
    IMflExportClient exports,
    ILogger<MflLeagueRankingsService> logger) : ILeagueRankingsService
{
    public async Task<MflResult<LeagueRankingData>> GetAsync(
        LeagueContext league, int fallbackWeek, CancellationToken cancellationToken)
    {
        var statusResult = await exports.ExportAsync(league, "mfl_status", null, cancellationToken).ConfigureAwait(false);
        var week = fallbackWeek;
        if (statusResult.IsSuccess)
        {
            try
            {
                var status = MflJsonParser.ParseMflWeekStatus(statusResult.Value!);
                if (status.ActiveLineupWeek > 0) week = status.ActiveLineupWeek;
            }
            catch (System.Text.Json.JsonException)
            {
                logger.LogWarning("League rankings could not parse MFL week status; using fallback week {Week}", week);
            }
        }
        if (week <= 0)
            return MflResult<LeagueRankingData>.Failure(
                "MFL did not provide a current scoring week.", statusResult.CorrelationId, "MISSING_WEEK", statusResult.StatusCode);

        var standingsResult = await exports.ExportAsync(league, "leagueStandings", null, cancellationToken).ConfigureAwait(false);
        if (!standingsResult.IsSuccess)
            return MflResult<LeagueRankingData>.Failure(
                standingsResult.UserMessage ?? "League total points could not be refreshed.", standingsResult.CorrelationId,
                standingsResult.MflErrorCode, standingsResult.StatusCode);

        var weeklyResult = await exports.ExportAsync(league, "weeklyResults", new Dictionary<string, string>
        {
            ["W"] = week.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        if (!weeklyResult.IsSuccess)
            return MflResult<LeagueRankingData>.Failure(
                weeklyResult.UserMessage ?? "Current-week league points could not be refreshed.", weeklyResult.CorrelationId,
                weeklyResult.MflErrorCode, weeklyResult.StatusCode);

        try
        {
            var standings = MflJsonParser.ParseStandings(standingsResult.Value!);
            var currentPoints = MflJsonParser.ParseWeeklyScores(weeklyResult.Value!);
            logger.LogInformation("League rankings parsed {StandingCount} overall teams and {WeeklyCount} week {Week} scores",
                standings.Count, currentPoints.Count, week);
            return MflResult<LeagueRankingData>.Success(
                new LeagueRankingData(week, standings, currentPoints), weeklyResult.CorrelationId,
                weeklyResult.StatusCode ?? System.Net.HttpStatusCode.OK);
        }
        catch (System.Text.Json.JsonException)
        {
            return MflResult<LeagueRankingData>.Failure(
                "MFL returned an unrecognized league-rankings response.", weeklyResult.CorrelationId,
                "INVALID_RESPONSE", weeklyResult.StatusCode);
        }
    }
}
