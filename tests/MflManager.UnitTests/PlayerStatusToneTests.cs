using MflManager.Application;

namespace MflManager.UnitTests;

public sealed class PlayerStatusToneTests
{
    [Theory]
    [InlineData("Q", PlayerStatusTone.Questionable)]
    [InlineData("D", PlayerStatusTone.Doubtful)]
    [InlineData("IR", PlayerStatusTone.Unavailable)]
    [InlineData("BYE", PlayerStatusTone.Unavailable)]
    [InlineData("O", PlayerStatusTone.Unavailable)]
    [InlineData("SUS", PlayerStatusTone.Unavailable)]
    [InlineData("PUP", PlayerStatusTone.Unavailable)]
    [InlineData("NFI", PlayerStatusTone.Unavailable)]
    [InlineData("IA", PlayerStatusTone.Unavailable)]
    [InlineData("RET", PlayerStatusTone.Unavailable)]
    [InlineData("IR-R", PlayerStatusTone.Unavailable)]
    [InlineData("COVID", PlayerStatusTone.Unavailable)]
    [InlineData("Q BYE", PlayerStatusTone.Unavailable)]
    [InlineData("D BYE", PlayerStatusTone.Unavailable)]
    [InlineData("IR BYE", PlayerStatusTone.Unavailable)]
    [InlineData(" q ", PlayerStatusTone.Questionable)]
    [InlineData("", PlayerStatusTone.Neutral)]
    [InlineData(null, PlayerStatusTone.Neutral)]
    [InlineData("P", PlayerStatusTone.Neutral)]
    [InlineData("OTHER", PlayerStatusTone.Neutral)]
    public void StatusTonePrioritizesUnableToPlayOverQuestionableOrDoubtful(string? status, PlayerStatusTone expected) =>
        Assert.Equal(expected, PlayerAvailability.Tone(status));
}
