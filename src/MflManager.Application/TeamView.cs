using System.Globalization;
using MflManager.Domain;

namespace MflManager.Application;

public sealed record WeeklyTeamResult(
    int Week,
    IReadOnlyList<string> StarterPlayerIds,
    IReadOnlyDictionary<string, decimal> PlayerPoints);

public sealed record TeamViewData(
    int Week,
    IReadOnlyList<RosterPlayer> Roster,
    IReadOnlyList<string> StarterPlayerIds,
    IReadOnlyDictionary<string, decimal> PlayerPoints);

public sealed record TeamPlayerRow(
    string Slot,
    string PlayerId,
    string DisplayName,
    string Position,
    string NflTeam,
    string RosterStatus,
    decimal Points)
{
    public string PointsText => Points.ToString("0.##", CultureInfo.CurrentCulture);
}

public sealed record TeamViewRows(
    IReadOnlyList<TeamPlayerRow> Starters,
    IReadOnlyList<TeamPlayerRow> Bench);

public static class TeamViewTable
{
    public static TeamViewRows Build(LeagueSettings? settings, TeamViewData data)
    {
        var assignments = LineupRules.AssignStarters(settings, data.Roster, data.StarterPlayerIds);
        var starters = assignments.Select(assignment =>
        {
            var player = assignment.Player?.Player;
            return new TeamPlayerRow(
                assignment.Definition.Label,
                player?.Id ?? string.Empty,
                player is null ? "Not selected" : AvailablePlayerSelector.DisplayName(player.Name),
                player?.Position ?? string.Empty,
                player?.NflTeam ?? string.Empty,
                "Starter",
                player is null ? 0m : data.PlayerPoints.GetValueOrDefault(player.Id));
        }).ToArray();

        var starterIds = assignments.Select(x => x.Player?.Player.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bench = LineupRules.OrderRoster(data.Roster.Where(x => !starterIds.Contains(x.Player.Id)))
            .Select(rosterPlayer => new TeamPlayerRow(
                string.Empty,
                rosterPlayer.Player.Id,
                AvailablePlayerSelector.DisplayName(rosterPlayer.Player.Name),
                rosterPlayer.Player.Position,
                rosterPlayer.Player.NflTeam,
                rosterPlayer.Status == RosterStatus.Active ? "Bench" : rosterPlayer.Status.ToString(),
                data.PlayerPoints.GetValueOrDefault(rosterPlayer.Player.Id)))
            .ToArray();

        return new TeamViewRows(starters, bench);
    }
}
