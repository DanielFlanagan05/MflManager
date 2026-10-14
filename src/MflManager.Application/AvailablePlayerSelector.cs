using MflManager.Domain;

namespace MflManager.Application;

/// <summary>
/// Applies the client-side safeguards used when presenting MFL's league-specific
/// free-agent feed. The feed itself is the authority for whether a player can be
/// acquired; these checks prevent stale/duplicated roster records from leaking
/// into the Players page.
/// </summary>
public static class AvailablePlayerSelector
{
    public static IReadOnlyList<Player> Select(
        LeagueSnapshot snapshot,
        IReadOnlySet<string> eligiblePositions,
        IReadOnlySet<string> pickupEligiblePlayerIds)
    {
        var rosterIds = snapshot.Roster.Select(x => x.Player.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rosterIdentities = snapshot.Roster.Select(x => Identity(x.Player))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return snapshot.FreeAgents
            .Where(player => !string.IsNullOrWhiteSpace(player.Id))
            .Where(player => !rosterIds.Contains(player.Id))
            .Where(player => !rosterIdentities.Contains(Identity(player)))
            .Where(player => eligiblePositions.Contains(player.Position))
            .Where(player => pickupEligiblePlayerIds.Contains(player.Id))
            .DistinctBy(player => player.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string DisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Player name unavailable";

        var trimmed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var separator = trimmed.IndexOf(',');
        if (separator <= 0 || separator == trimmed.Length - 1) return trimmed;

        var familyName = trimmed[..separator].Trim();
        var givenName = trimmed[(separator + 1)..].Trim().Trim(',').Trim();
        return string.IsNullOrWhiteSpace(givenName) ? familyName : $"{givenName} {familyName}";
    }

    private static string Identity(Player player) =>
        $"{Normalize(DisplayName(player.Name))}|{player.Position.Trim().ToUpperInvariant()}|{player.NflTeam.Trim().ToUpperInvariant()}";

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
