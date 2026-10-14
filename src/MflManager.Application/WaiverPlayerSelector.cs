using MflManager.Domain;

namespace MflManager.Application;

public sealed record RankedWaiverPlayer(Player Player, PlayerRanking Ranking, int? AvailableCopies, decimal? SeasonPoints = null)
{
    public string DisplayName => $"{AvailablePlayerSelector.DisplayName(Player.Name)}, {Player.NflTeam}";
    public string PositionRank => $"{WaiverPlayerSelector.NormalizePosition(Player.Position)} {Ranking.PositionRank}";
    public string CopiesText => AvailableCopies?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string SeasonPointsText => SeasonPoints?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
}

public static class WaiverPlayerSelector
{
    public static IReadOnlyList<RankedWaiverPlayer> Select(LeagueSnapshot snapshot, string position = "QB", int limit = 50)
    {
        if (!snapshot.AvailablePlayersReady || !snapshot.WeeklyRankingsReady) return [];
        var rankings = snapshot.Rankings.Where(x => x.PositionRank > 0)
            .GroupBy(x => Identity(x.PlayerName, x.Team, x.Position))
            .Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());
        var owned = snapshot.Roster.Select(x => x.Player.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unambiguousPlayers = snapshot.FreeAgents.DistinctBy(x => x.Id)
            .GroupBy(x => Identity(x.Name, x.NflTeam, x.Position)).Where(x => x.Count() == 1).Select(x => x.Single());
        return unambiguousPlayers.Where(x => !owned.Contains(x.Id) && NormalizePosition(x.Position) == NormalizePosition(position))
            .Select(player => rankings.TryGetValue(Identity(player.Name, player.NflTeam, player.Position), out var rank)
                ? new RankedWaiverPlayer(player, rank, snapshot.FreeAgentCopies.TryGetValue(player.Id, out var copies) ? copies : null) : null)
            .OfType<RankedWaiverPlayer>().DistinctBy(x => x.Player.Id)
            .OrderBy(x => x.Ranking.PositionRank).ThenBy(x => x.DisplayName).Take(limit).ToArray();
    }

    public static string Identity(string name, string team, string position) =>
        $"{Normalize(AvailablePlayerSelector.DisplayName(name))}|{NormalizeTeam(team)}|{NormalizePosition(position)}";
    public static string NormalizePosition(string position) => position.Trim().ToUpperInvariant() switch
    { "D/ST" or "DST" or "DEF" => "DEF", "PK" => "K", var value => value };
    public static string NormalizeTeam(string team) => team.Trim().ToUpperInvariant() switch
    { "JAC" => "JAX", "LA" or "LAR" => "LAR", "WSH" or "WAS" => "WAS", "KCC" => "KC", "GBP" => "GB", "NEP" => "NE", "NOS" => "NO", "SFO" => "SF", "TBB" => "TB", "LVR" or "OAK" => "LV", "SD" => "LAC", var value => value };
    private static string Normalize(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\b(Jr\.?|Sr\.?|II|III|IV)(?=\s|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new(text.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }
}
