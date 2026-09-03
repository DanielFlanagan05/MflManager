using MflManager.MflApi;

namespace MflManager.ContractTests;

public sealed class ResponseContractTests
{
    [Theory]
    [InlineData("Fixtures/players-empty.json", 0)]
    [InlineData("Fixtures/players-singleton.json", 1)]
    [InlineData("Fixtures/players-array.json", 2)]
    public void PlayerFixtures_ParseAllCollectionShapes(string path, int expected)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(expected, MflJsonParser.ParsePlayers(json).Count);
    }

    [Theory]
    [InlineData("{\"error\":\"Login required\"}", false)]
    [InlineData("{\"status\":\"OK\"}", true)]
    [InlineData("<status><error code=\"401\">Expired cookie</error></status>", false)]
    public void MutationBodies_DriveSuccess(string body, bool expected) => Assert.Equal(expected, MflHttpResult.ParseMutation(body).Success);

    [Fact]
    public void LeagueHostComesFromDiscoveryPayload()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "myleagues-host-change.json"));
        Assert.Contains("www73.myfantasyleague.com", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("www42", fixture, StringComparison.Ordinal);
    }
}
