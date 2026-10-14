using MflManager.Application;
using MflManager.Domain;

namespace MflManager.MflApi;

public sealed class MflWaiverDataService(IMflExportClient exports) : IMflWaiverDataService
{
    public async Task<WaiverSupplementalData> GetAsync(LeagueContext league, CancellationToken cancellationToken)
    {
        var balanceTask = exports.ExportAsync(league, "league", null, cancellationToken);
        var pointsTask = exports.ExportAsync(league, "playerScores",
            new Dictionary<string, string> { ["W"] = "YTD", ["STATUS"] = "freeagent", ["RULES"] = "1" }, cancellationToken);
        await Task.WhenAll(balanceTask, pointsTask).ConfigureAwait(false);
        var balance = balanceTask.Result;
        var points = pointsTask.Result;
        return new WaiverSupplementalData(
            balance.IsSuccess && balance.Value is not null ? MflJsonParser.ParseFaabBalance(balance.Value, league.FranchiseId) : null,
            balance.IsSuccess,
            points.IsSuccess && points.Value is not null ? MflJsonParser.ParseSeasonPlayerPoints(points.Value) : new Dictionary<string, decimal>(),
            points.IsSuccess);
    }
}
