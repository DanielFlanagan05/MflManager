using MflManager.Application;
using MflManager.Domain;
using MflManager.MflApi;

namespace MflManager.UnitTests;

public sealed class ByeWeekTests
{
    [Theory]
    [InlineData(null, null, "BYE")]
    [InlineData("IR", null, "IR BYE")]
    [InlineData("Questionable", null, "Q BYE")]
    [InlineData("Out", null, "O BYE")]
    [InlineData("Doubtful", null, "D BYE")]
    [InlineData(null, "Suspended", "SUS BYE")]
    [InlineData(null, "Exempt", "IA BYE")]
    [InlineData(null, "RETIRED", "RET BYE")]
    [InlineData(null, "HOLDOUT", "IA BYE")]
    [InlineData("IR-PUP", null, "PUP BYE")]
    [InlineData("IR-NFI", null, "NFI BYE")]
    [InlineData("IR-R", null, "IR-R BYE")]
    [InlineData("COVID", null, "COVID BYE")]
    [InlineData("Probable", null, "P BYE")]
    [InlineData("Other", null, "OTHER BYE")]
    public void ByeFollowsEveryExistingStatus(string? injury, string? status, string expected)
    {
        var player = new Player("p1", "Player", "RB", " kcc ", status, injury);
        var byes = new Dictionary<string, int> { ["KCC"] = 5 };
        Assert.Equal(expected, PlayerAvailability.Code(player, 5, byes));
        Assert.Equal(PlayerAvailability.Code(player), PlayerAvailability.Code(player, 4, byes));
        Assert.Equal(PlayerAvailability.Code(player), PlayerAvailability.Code(player, 6, byes));
        Assert.Equal(PlayerAvailability.Code(player), PlayerAvailability.Code(player, 0, byes));
        Assert.Equal(PlayerAvailability.Code(player), PlayerAvailability.Code(player, 5, new Dictionary<string, int>()));
    }

    [Theory]
    [InlineData("{\"nflByeWeeks\":{\"year\":\"2026\",\"team\":{\"id\":\"KCC\",\"bye_week\":\"5\"}}}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2026,\"team\":[{\"id\":\"kcc\",\"bye_week\":5}]}}")]
    public void ParserAcceptsSingletonAndArrayTeams(string body)
    {
        var byes = MflJsonParser.ParseNflByeWeeks(body, 2026);
        Assert.Equal(5, Assert.Single(byes).Value);
        Assert.Equal("KCC", Assert.Single(byes).Key);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2025,\"team\":{\"id\":\"KCC\",\"bye_week\":5}}}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2026}}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2026,\"team\":{\"id\":\"KCC\",\"bye_week\":0}}}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2026,\"team\":{\"id\":\"KCC\",\"bye_week\":19}}}")]
    [InlineData("{\"nflByeWeeks\":{\"year\":2026,\"team\":{\"id\":\"KCC\",\"bye_week\":\"unknown\"}}}")]
    public void InvalidOrWrongSeasonByeDataIsRejected(string body) =>
        Assert.Throws<System.Text.Json.JsonException>(() => MflJsonParser.ParseNflByeWeeks(body, 2026));
}
