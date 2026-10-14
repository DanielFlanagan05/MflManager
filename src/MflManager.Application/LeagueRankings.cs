using MflManager.Domain;

namespace MflManager.Application;

public sealed record LeagueRankingData(
    int Week,
    IReadOnlyList<Standing> Standings,
    IReadOnlyDictionary<string, decimal> CurrentWeekPoints);

public sealed record LeagueRankingRow(
    int Rank,
    string FranchiseId,
    string TeamName,
    decimal CurrentWeekPoints,
    decimal OverallPoints,
    bool IsMyTeam)
{
    public string RankText => Rank.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string TeamDisplay => IsMyTeam ? $"{TeamName} (My Team)" : TeamName;
    public string CurrentWeekPointsText => CurrentWeekPoints.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    public string OverallPointsText => OverallPoints.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
}

public static class LeagueRankingTable
{
    public static IReadOnlyList<LeagueRankingRow> Build(
        LeagueRankingData data, IReadOnlyList<Franchise> franchises, string? currentFranchiseId)
    {
        var standingsById = data.Standings
            .GroupBy(x => x.FranchiseId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var knownIds = franchises.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var teams = franchises.Select(franchise =>
            {
                var standing = standingsById.GetValueOrDefault(franchise.Id);
                return (Id: franchise.Id, Name: franchise.Name, OverallPoints: standing?.PointsFor ?? 0m);
            })
            .Concat(data.Standings.Where(x => !knownIds.Contains(x.FranchiseId))
                .Select(x => (Id: x.FranchiseId, Name: x.FranchiseName, OverallPoints: x.PointsFor)))
            .OrderByDescending(x => x.OverallPoints)
            .ToArray();

        return teams.Select((team, index) => new LeagueRankingRow(
            index + 1,
            team.Id,
            team.Name,
            data.CurrentWeekPoints.GetValueOrDefault(team.Id),
            team.OverallPoints,
            string.Equals(team.Id, currentFranchiseId, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
