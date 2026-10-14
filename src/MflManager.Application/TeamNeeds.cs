using MflManager.Domain;

namespace MflManager.Application;

public sealed record WeekRosterPlayer(Player Player, RosterStatus Status, decimal? Salary, int? ContractYear,
    int Week, string AvailabilityStatus, int? ByeWeek, bool IsBye, bool IsUnavailable, bool AvailabilityRisk);

public sealed record TeamNeedSlot(string Slot, IReadOnlyList<string> EligiblePositions, string? PlayerId,
    string AvailabilityStatus, bool? NeedsReplacement, IReadOnlyList<string> OwnedAlternativePlayerIds);

public sealed record TeamNeedsReport(int Week, int LineupWeek, bool LineupKnown, bool RosterStale,
    bool StatusStale, bool ByeWeeksStale, DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, int> NflByeWeeks, IReadOnlyList<WeekRosterPlayer> Roster,
    IReadOnlyList<TeamNeedSlot> Slots);

public static class TeamNeeds
{
    public static IReadOnlyList<WeekRosterPlayer> RosterForWeek(LeagueSnapshot snapshot, int week) =>
        snapshot.Roster.Select(x =>
        {
            var code = PlayerAvailability.Code(x.Player);
            var byeWeek = snapshot.NflByeWeeks.GetValueOrDefault(x.Player.NflTeam.Trim().ToUpperInvariant());
            var isBye = week > 0 && byeWeek == week;
            var unavailable = x.Status != RosterStatus.Active || isBye || code is
                "IR" or "IR-R" or "PUP" or "NFI" or "O" or "SUS" or "IA" or "RET";
            return new WeekRosterPlayer(x.Player, x.Status, x.Salary, x.ContractYear, week,
                PlayerAvailability.Code(x.Player, isBye), byeWeek > 0 ? byeWeek : null, isBye,
                unavailable, !unavailable && code.Length > 0);
        }).ToArray();

    public static TeamNeedsReport Build(LeagueSnapshot snapshot, int week)
    {
        if (week < (snapshot.Settings?.StartWeek ?? 1) || week > (snapshot.Settings?.EndWeek ?? 18))
            throw new ArgumentOutOfRangeException(nameof(week), "Week is outside the selected league's season.");
        var roster = RosterForWeek(snapshot, week);
        var lineupKnown = snapshot.LineupWeek == week && !Stale(snapshot, SnapshotSection.Lineup) &&
            LineupRules.BuildSlots(snapshot.Settings).Count > 0;
        var starters = lineupKnown ? snapshot.Lineup.Where(x => x.IsStarter && x.PlayerId is not null)
            .Select(x => x.PlayerId!).ToArray() : [];
        var byId = roster.ToDictionary(x => x.Player.Id, StringComparer.OrdinalIgnoreCase);
        var selected = starters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = LineupRules.AssignStarters(snapshot.Settings, snapshot.Roster, starters).Select(x =>
        {
            var player = x.Player is null ? null : byId.GetValueOrDefault(x.Player.Player.Id);
            var alternatives = roster.Where(candidate => !candidate.IsUnavailable && !selected.Contains(candidate.Player.Id) &&
                x.Definition.EligiblePositions.Contains(LineupRules.NormalizePosition(candidate.Player.Position)))
                .Select(candidate => candidate.Player.Id).ToArray();
            return new TeamNeedSlot(x.Definition.Label, x.Definition.EligiblePositions.ToArray(), player?.Player.Id,
                player?.AvailabilityStatus ?? string.Empty, lineupKnown ? player is null || player.IsUnavailable : null, alternatives);
        }).ToArray();
        return new(week, snapshot.LineupWeek, lineupKnown, Stale(snapshot, SnapshotSection.Rosters),
            Stale(snapshot, SnapshotSection.Injuries), Stale(snapshot, SnapshotSection.NflByeWeeks),
            snapshot.CapturedAt, snapshot.NflByeWeeks, roster, slots);
    }

    private static bool Stale(LeagueSnapshot snapshot, SnapshotSection section) =>
        snapshot.Sections.GetValueOrDefault(section)?.IsStale != false;
}
