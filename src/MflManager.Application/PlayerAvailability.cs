using MflManager.Domain;

namespace MflManager.Application;

public enum PlayerStatusTone { Neutral, Questionable, Doubtful, Unavailable }

public static class PlayerAvailability
{
    public static PlayerStatusTone Tone(string? status)
    {
        var codes = Normalize(status).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (codes.Any(x => x is "BYE" or "IR" or "IR-R" or "IR-PUP" or "IR-NFI" or "PUP" or "NFI" or "O" or "SUS" or "IA" or "RET" or "COVID"))
            return PlayerStatusTone.Unavailable;
        if (codes.Contains("D")) return PlayerStatusTone.Doubtful;
        return codes.Contains("Q") ? PlayerStatusTone.Questionable : PlayerStatusTone.Neutral;
    }

    public static string Code(Player player, int week, IReadOnlyDictionary<string, int> byeWeeks) =>
        Code(player, week > 0 && byeWeeks.TryGetValue(player.NflTeam.Trim().ToUpperInvariant(), out var byeWeek) && byeWeek == week);

    public static string Code(Player player, bool isBye = false)
    {
        var injury = Normalize(player.Injury);
        var status = injury.Length > 0 ? Map(injury, true) : Map(Normalize(player.Status), false);
        return !isBye ? status : status.Length == 0 ? "BYE" : $"{status} BYE";
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string Map(string value, bool fromInjury)
    {
        if (!fromInjury && value.Length == 1) return "";
        return value switch
        {
            "" or "ACTIVE" or "HEALTHY" or "AVAILABLE" or "NONE" or "N/A" or "-" => "",
            "Q" or "QUESTIONABLE" => "Q",
            "D" or "DOUBTFUL" => "D",
            "O" or "OUT" => "O",
            "P" or "PROBABLE" => "P",
            "IR" or "INJURED RESERVE" or "RESERVE/INJURED" => "IR",
            "IR-R" => "IR-R",
            "IR-PUP" => "PUP",
            "IR-NFI" => "NFI",
            "PUP" or "PHYSICALLY UNABLE TO PERFORM" => "PUP",
            "NFI" or "NON-FOOTBALL INJURY" or "NON-FOOTBALL ILLNESS" => "NFI",
            "S" or "SUS" or "SUSPENDED" or "SUSPENSION" => "SUS",
            "IA" or "INACTIVE" or "EXEMPT" or "COMMISSIONER EXEMPT" or "COMMISSIONER'S EXEMPT LIST" => "IA",
            "COVID" or "COVID-19" => "COVID",
            "RETIRED" => "RET",
            "HOLDOUT" => "IA",
            _ when fromInjury => value.Length <= 6 ? value : "OTHER",
            _ => ""
        };
    }
}
