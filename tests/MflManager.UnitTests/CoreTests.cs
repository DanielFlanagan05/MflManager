using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;
using MflManager.MflApi;

namespace MflManager.UnitTests;

public sealed class CoreTests
{
    [Fact]
    public void SingleOrArrayConverter_ReadsSingleObjectAndArray()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new SingleOrArrayJsonConverter<Item>());
        var single = JsonSerializer.Deserialize<IReadOnlyList<Item>>("{\"id\":\"1\"}", options);
        var array = JsonSerializer.Deserialize<IReadOnlyList<Item>>("[{\"id\":\"1\"},{\"id\":\"2\"}]", options);
        Assert.Equal("1", Assert.Single(single!).Id);
        Assert.Equal(2, array!.Count);
    }

    [Fact]
    public void MutationResult_DoesNotTrustHttp200WithErrorBody()
    {
        var result = MflHttpResult.ParseMutation("{\"error\":\"Player is not available\"}");
        Assert.False(result.Success);
        Assert.Equal("MFL_REJECTED", result.Code);
    }

    [Fact]
    public void LoginXml_StatusParserAcceptsSuccessfulBody()
    {
        var result = MflHttpResult.ParseMutation("<status>OK</status>");
        Assert.True(result.Success);
        Assert.Equal("OK", result.Message);
    }

    [Fact]
    public void WaiverSerialization_PreservesOrderBidAndAppend()
    {
        var mutation = new WaiverMutation([
            new(null, "101", "201", 2, 7.5m),
            new(null, "102", null, 1, 11m)
        ], true, true);
        var parameters = mutation.ToParameters();
        Assert.Equal("blindBidWaiverRequest", parameters["TYPE"]);
        Assert.Equal("102_11_0000,101_7.5_201", parameters["PICKS"]);
        Assert.Equal("0", parameters["REPLACE"]);
    }

    [Fact]
    public void DraftPickSerialization_UsesLiveDraftFields()
    {
        var parameters = new DraftPickMutation("500", 3, 7, "0004").ToParameters();
        Assert.Equal("DRAFT", parameters["CMD"]);
        Assert.Equal("500", parameters["PLAYER_PICK"]);
        Assert.Equal("3", parameters["ROUND"]);
        Assert.Equal("7", parameters["PICK"]);
        Assert.DoesNotContain("TYPE", parameters.Keys);
    }

    [Fact]
    public void CapabilityService_HidesCommissionerActionsFromOwner()
    {
        var service = new MflCapabilityService();
        Assert.DoesNotContain(service.GetCapabilities(null, false).Items, x => x.Access == CapabilityAccess.Commissioner);
        Assert.Contains(service.GetCapabilities(null, true).Items, x => x.RequestType == "live_draft:PAUSE");
    }

    [Fact]
    public void PlayerParser_ToleratesSingletonShape()
    {
        var players = MflJsonParser.ParsePlayers("{\"players\":{\"player\":{\"id\":\"42\",\"name\":\"Example, Player\",\"position\":\"RB\",\"team\":\"DET\"}}}");
        Assert.Equal("42", Assert.Single(players).Id);
    }

    [Fact]
    public void PlayerParser_NormalizesPrefixesAndFiltersUnsupportedPositions()
    {
        var players = MflJsonParser.ParsePlayers("{\"players\":{\"player\":[{\"id\":\"1\",\"name\":\"Receiver\",\"position\":\"TM WR DET\",\"team\":\"DET\"},{\"id\":\"2\",\"name\":\"Center\",\"position\":\"C\",\"team\":\"DET\"}]}}");
        var player = Assert.Single(players);
        Assert.Equal("WR", player.Position);
        Assert.Equal("Receiver", player.Name);
    }

    [Fact]
    public void PlayerParser_NormalizesCompactMflTeamCode()
    {
        var players = MflJsonParser.ParsePlayers("{\"players\":{\"player\":{\"id\":\"1\",\"name\":\"Bills, Buffalo\",\"position\":\"TMWR\",\"team\":\"BUF\"}}}");
        Assert.Equal("WR", Assert.Single(players).Position);
    }

    [Fact]
    public void DraftQueueParser_PreservesPlayerOrderAcrossMflShapes()
    {
        var queue = MflJsonParser.ParseDraftQueue("{\"myDraftList\":{\"player\":[{\"id\":\"p3\"},\"p1,p2\"]}}");
        Assert.Equal(["p3", "p1", "p2"], queue);
    }

    [Fact]
    public void DraftParser_ReadsEveryPickFromNestedDraftUnit()
    {
        var draft = MflJsonParser.ParseDraft("""
            {"draftResults":{"draftUnit":{"unit":"LEAGUE","draftPick":[
              {"round":"1","pick":"1","franchise":"0001","player":"p1"},
              {"round":"1","pick":"2","franchise":"0002","player":"p2"},
              {"round":"1","pick":"3","franchise":"0003","player":"p3"},
              {"round":"1","pick":"4","franchise":"0004","player":"p4"},
              {"round":"1","pick":"5","franchise":"0005","player":"p5"},
              {"round":"1","pick":"6","franchise":"0006","player":"p6"},
              {"round":"1","pick":"7","franchise":"0007","player":"p7"}
            ]}}}
            """);
        Assert.Equal(7, draft.Picks.Count);
        Assert.Equal("p7", draft.Picks[6].PlayerId);
    }

    [Fact]
    public void AdpParser_MapsPlayersToAveragePick()
    {
        var adp = MflJsonParser.ParseAdp("{\"adp\":{\"player\":[{\"id\":\"p1\",\"averagePick\":\"2.75\"},{\"id\":\"p2\",\"adp\":\"10\"}]}}");
        Assert.Equal(2.75m, adp["p1"]);
        Assert.Equal(10m, adp["p2"]);
    }

    public sealed record Item(string Id);
}
