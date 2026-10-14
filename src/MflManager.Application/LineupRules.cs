using MflManager.Domain;

namespace MflManager.Application;

public sealed record LineupSlotDefinition(string Label, IReadOnlySet<string> EligiblePositions);
public sealed record AssignedLineupSlot(LineupSlotDefinition Definition, RosterPlayer? Player);

public static class LineupRules
{
    private static readonly string[] PreferredOrder =
        ["QB", "RB", "WR", "FLEX", "TE", "K", "PK", "DEF", "DT", "DE", "LB", "CB", "S"];

    public static IReadOnlyList<LineupSlotDefinition> BuildSlots(LeagueSettings? settings)
    {
        if (settings is null) return [];
        var rules = settings.LineupRules
            .Where(x => x.Minimum > 0 || x.Maximum > 0)
            .Select(x => x with { Position = NormalizePosition(x.Position) })
            .Where(x => x.Position.Length > 0)
            .ToArray();
        if (rules.Length == 0) return [];

        var slots = new List<LineupSlotDefinition>();
        foreach (var rule in Order(rules))
            for (var index = 1; index <= rule.Minimum; index++)
                slots.Add(new(index == 1 && rule.Minimum == 1 ? rule.Position : $"{rule.Position}{index}",
                    new HashSet<string>([rule.Position], StringComparer.OrdinalIgnoreCase)));

        var flexibleCount = Math.Max(0, settings.StarterCount - slots.Count);
        var flexPositions = rules.Where(x => x.Maximum > x.Minimum).Select(x => x.Position)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index <= flexibleCount; index++)
            slots.Add(new($"FLEX{index}", flexPositions));

        return slots.OrderBy(x => SortOrder(x.Label)).ThenBy(x => NumericSuffix(x.Label)).ToArray();
    }

    public static IReadOnlySet<string> EligiblePlayerPositions(LeagueSettings? settings) =>
        BuildSlots(settings).SelectMany(x => x.EligiblePositions).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static int ActiveWeek(LeagueSnapshot snapshot, DateTimeOffset? now = null)
    {
        if (snapshot.LineupWeek > 0) return snapshot.LineupWeek;
        if (snapshot.Settings?.CurrentWeek > 0) return snapshot.Settings.CurrentWeek;
        if (snapshot.Context.Season.IsReadOnly)
            return snapshot.Matchups.Select(x => x.Week).Where(x => x > 0)
                .DefaultIfEmpty(snapshot.Settings?.StartWeek ?? 1).Max();

        var eastern = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var date = DateOnly.FromDateTime(eastern.DateTime);
        var september = new DateOnly(snapshot.Context.Season.Year, 9, 1);
        var laborDay = september.AddDays(((int)DayOfWeek.Monday - (int)september.DayOfWeek + 7) % 7);
        // NFL week one starts the Thursday after Labor Day; weeks advance the following Tuesday.
        var firstRollover = laborDay.AddDays(8);
        var week = date < firstRollover ? 1 : 2 + (date.DayNumber - firstRollover.DayNumber) / 7;
        return Math.Clamp(week, snapshot.Settings?.StartWeek ?? 1, snapshot.Settings?.EndWeek ?? 18);
    }

    public static string NormalizePosition(string value)
    {
        var normalized = new string(value.Trim().ToUpperInvariant().Where(char.IsLetter).ToArray());
        return normalized switch { "D" or "DST" => "DEF", "TMQB" => "QB", "TMRB" => "RB", "TMWR" => "WR", "TMTE" => "TE", _ => normalized };
    }

    public static int SortOrder(string position)
    {
        var basePosition = new string(position.TakeWhile(char.IsLetter).ToArray());
        var index = Array.FindIndex(PreferredOrder, x => x.Equals(basePosition, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? PreferredOrder.Length : index;
    }

    public static IReadOnlyList<AssignedLineupSlot> AssignStarters(
        LeagueSettings? settings, IReadOnlyList<RosterPlayer> roster, IReadOnlyList<string> starterIds)
    {
        var definitions = BuildSlots(settings);
        var activeRoster = roster.Where(x => x.Status == RosterStatus.Active).ToArray();
        var startersRemaining = new HashSet<string>(starterIds, StringComparer.OrdinalIgnoreCase);
        var assignments = definitions.Select(x => new AssignedLineupSlot(x, null)).ToArray();

        foreach (var index in Enumerable.Range(0, assignments.Length).OrderBy(x =>
                     assignments[x].Definition.Label.StartsWith("FLEX", StringComparison.OrdinalIgnoreCase)))
        {
            var assignment = assignments[index];
            var selected = activeRoster
                .Where(x => assignment.Definition.EligiblePositions.Contains(NormalizePosition(x.Player.Position)))
                .OrderBy(x => AvailablePlayerSelector.DisplayName(x.Player.Name), StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault(x => startersRemaining.Contains(x.Player.Id));
            if (selected is null) continue;
            assignments[index] = assignment with { Player = selected };
            startersRemaining.Remove(selected.Player.Id);
        }

        return assignments;
    }

    public static IReadOnlyList<RosterPlayer> OrderRoster(IEnumerable<RosterPlayer> roster) =>
        roster.OrderBy(x => SortOrder(x.Player.Position))
            .ThenBy(x => x.Player.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IEnumerable<LineupPositionRule> Order(IEnumerable<LineupPositionRule> rules) =>
        rules.OrderBy(x => SortOrder(x.Position)).ThenBy(x => x.Position, StringComparer.OrdinalIgnoreCase);

    private static int NumericSuffix(string label) =>
        int.TryParse(new string(label.SkipWhile(char.IsLetter).ToArray()), out var value) ? value : 0;
}
