using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflTeamViewService(
    IMflExportClient exports,
    ILogger<MflTeamViewService> logger) : ITeamViewService
{
    public async Task<MflResult<TeamViewData>> GetAsync(
        LeagueContext league,
        string franchiseId,
        int fallbackWeek,
        IReadOnlyDictionary<string, Player> players,
        CancellationToken cancellationToken)
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
                logger.LogWarning("Team view could not parse MFL week status; using fallback week {Week}", week);
            }
        }
        if (week <= 0)
            return MflResult<TeamViewData>.Failure(
                "MFL did not provide a current scoring week.", statusResult.CorrelationId,
                "MISSING_WEEK", statusResult.StatusCode);

        var rosterTask = exports.ExportAsync(league, "rosters", null, cancellationToken);
        var weeklyTask = exports.ExportAsync(league, "weeklyResults", new Dictionary<string, string>
        {
            ["W"] = week.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["F"] = franchiseId
        }, cancellationToken);
        await Task.WhenAll(rosterTask, weeklyTask).ConfigureAwait(false);
        var rosterResult = await rosterTask.ConfigureAwait(false);
        var weeklyResult = await weeklyTask.ConfigureAwait(false);

        if (!rosterResult.IsSuccess)
            return MflResult<TeamViewData>.Failure(
                rosterResult.UserMessage ?? "The selected team roster could not be loaded.", rosterResult.CorrelationId,
                rosterResult.MflErrorCode, rosterResult.StatusCode);
        if (!weeklyResult.IsSuccess)
            return MflResult<TeamViewData>.Failure(
                weeklyResult.UserMessage ?? "The selected team's current-week results could not be loaded.", weeklyResult.CorrelationId,
                weeklyResult.MflErrorCode, weeklyResult.StatusCode);

        try
        {
            var roster = MflJsonParser.ParseRoster(rosterResult.Value!, players, franchiseId);
            var weekly = MflJsonParser.ParseWeeklyTeamResult(weeklyResult.Value!, week, franchiseId);
            logger.LogInformation(
                "Team view parsed franchise {FranchiseId}: {RosterCount} roster players, {StarterCount} starters, {ScoreCount} week {Week} player scores",
                franchiseId, roster.Count, weekly.StarterPlayerIds.Count, weekly.PlayerPoints.Count, weekly.Week);
            return MflResult<TeamViewData>.Success(
                new TeamViewData(weekly.Week, roster, weekly.StarterPlayerIds, weekly.PlayerPoints),
                weeklyResult.CorrelationId,
                weeklyResult.StatusCode ?? System.Net.HttpStatusCode.OK);
        }
        catch (System.Text.Json.JsonException)
        {
            return MflResult<TeamViewData>.Failure(
                "MFL returned an unrecognized team-view response.", weeklyResult.CorrelationId,
                "INVALID_RESPONSE", weeklyResult.StatusCode);
        }
    }
}
