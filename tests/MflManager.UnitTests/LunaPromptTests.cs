using MflManager.Terminal;

namespace MflManager.UnitTests;

public class LunaPromptTests
{
    [Fact]
    public void PickupAdvicePrioritizesPlayableWeeklySlotsAndPreservesAdviceOnlyBoundary()
    {
        Assert.Contains("standing weekly goal is to fill every required starting slot", LunaPrompt.Instructions);
        Assert.Contains("mfl_get_team_needs", LunaPrompt.Instructions);
        Assert.Contains("First consider eligible owned bench replacements", LunaPrompt.Instructions);
        Assert.Contains("without\ncounting one player in multiple slots", LunaPrompt.Instructions.Replace("\r\n", "\n"));
        Assert.Contains("NFL bye-week map for the requested week", LunaPrompt.Instructions);
        Assert.Contains("unknown, do not report its slots as definitively empty", LunaPrompt.Instructions);
        Assert.Contains("Never execute a mutation unless explicitly requested", LunaPrompt.Instructions);
        var context = LunaPrompt.Context(DateTimeOffset.UtcNow, "123", 2026, 5, false);
        Assert.Contains("Weekly lineup priority", context);
        Assert.Contains("Advice alone never authorizes", context);
    }
    [Fact]
    public void TuesdayAdvancesNflWeekAndFreshnessCutoff()
    {
        var context = LunaPrompt.Context(new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero), "123", 2026, 3, false);
        Assert.Contains("current NFL week 4", context);
        Assert.Contains("completed week 3", context);
        Assert.Contains("cutoff 2026-09-29", context);
        Assert.Contains("league active week: 3", context);
        Assert.Contains("DISABLED", context);
    }

    [Fact]
    public void ContextUsesEasternDateAndDoesNotHardcodeSeason()
    {
        var context = LunaPrompt.Context(new DateTimeOffset(2027, 9, 28, 2, 0, 0, TimeSpan.Zero), null, null, null, true);
        Assert.Contains("2027-09-27 (Monday)", context);
        Assert.Contains("current NFL week 3", context);
        Assert.Contains("ask the user to select a league", context);
    }

    [Fact]
    public void OffseasonDoesNotPretendItIsWeekEighteen()
    {
        Assert.Contains("outside the regular season", LunaPrompt.Context(new DateTimeOffset(2027, 4, 1, 13, 0, 0, TimeSpan.Zero), null, null, null, false));
        Assert.Contains("preview first", LunaPrompt.Instructions);
        Assert.Contains("under 150 words", LunaPrompt.Instructions);
    }

    [Fact]
    public void DropAdviceRequiresVerifiedInjuryAndRestOfSeasonValue()
    {
        var instructions = LunaPrompt.Instructions;
        var context = LunaPrompt.Context(new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero), "123", 2026, 4, false);
        Assert.Contains("MFL roster's Injury field", instructions);
        Assert.Contains("absence from it is NOT proof", instructions);
        Assert.Contains("A temporarily injured high-value player is generally a hold", instructions);
        Assert.Contains("never treat a weekly", instructions);
        Assert.Contains("never invent injury status", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://www.nfl.com/injuries/", instructions);
        Assert.Contains("https://www.footballguys.com/rankings/duration/restofseason", instructions);
        Assert.Contains("2026-10-01", context);
        Assert.Contains("current NFL week 4", context);
        Assert.Contains("Drop advice guardrail", context);
    }
}
