using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MflManager.Domain;
using MflManager.Application;
using MflManager.MflApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MflManager.ContractTests;

public sealed class ApiResponseAuditTests
{
    [Fact]
    public void EveryParsedResponseIsAuditedAgainstRawFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "api-sample-responses.json");
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var players = MflJsonParser.ParsePlayers(root.GetProperty("players").GetRawText());
        var freeAgents = MflJsonParser.ParsePlayers(root.GetProperty("freeAgents").GetRawText());
        var playersById = players.Concat(freeAgents).ToDictionary(x => x.Id);
        var parsed = new Dictionary<string, object>
        {
            ["settings"] = MflJsonParser.ParseSettings(root.GetProperty("league").GetRawText()),
            ["franchises"] = MflJsonParser.ParseFranchises(root.GetProperty("franchises").GetRawText()),
            ["roster"] = MflJsonParser.ParseRoster(root.GetProperty("rosters").GetRawText(), playersById),
            ["matchups"] = MflJsonParser.ParseMatchups(root.GetProperty("weeklyResults").GetRawText()),
            ["standings"] = MflJsonParser.ParseStandings(root.GetProperty("leagueStandings").GetRawText()),
            ["players"] = players,
            ["freeAgents"] = freeAgents,
            ["adp"] = MflJsonParser.ParseAdp(root.GetProperty("adp").GetRawText()),
            ["transactions"] = MflJsonParser.ParseTransactions(root.GetProperty("transactions").GetRawText()),
            ["waivers"] = MflJsonParser.ParseWaivers(root.GetProperty("pendingWaivers").GetRawText()),
            ["trades"] = MflJsonParser.ParseTrades(root.GetProperty("pendingTrades").GetRawText()),
            ["draft"] = MflJsonParser.ParseDraft(root.GetProperty("draftResults").GetRawText()),
            ["draftQueue"] = MflJsonParser.ParseDraftQueue(root.GetProperty("myDraftList").GetRawText()),
            ["lineup"] = MflJsonParser.ParseLineup(root.GetProperty("lineup").GetRawText())
        };
        Assert.Equal("Doe, Jane", players.Single().Name);
        Assert.Equal("WR", players.Single().Position);
        Assert.Empty(MflJsonParser.ParsePlayers("{\"players\":{\"player\":{\"id\":\"x\",\"name\":\"bad\",\"position\":\"C\"}}}"));
        Assert.Equal("0001", parsed["draft"] is DraftState d ? d.OnTheClockFranchiseId : null);
        Assert.Equal(["p1", "p3"], (IReadOnlyList<string>)parsed["draftQueue"]);
        Assert.Single((IReadOnlyList<LineupSlot>)parsed["lineup"]);
        var log = Path.Combine(AppContext.BaseDirectory, "api-response-audit.log");
        File.WriteAllText(log, JsonSerializer.Serialize(new { raw = root, parsed }, new JsonSerializerOptions { WriteIndented = true }));
        var written = File.ReadAllText(log);
        Assert.Contains("Doe, Jane", written, StringComparison.Ordinal);
        Assert.Contains("settings", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryExportAndDraftResponsesReturnHttp200()
    {
        var handler = new StaticHandler("{\"status\":\"OK\"}");
        var services = new ServiceCollection().AddLogging().AddSingleton<IHttpClientFactory>(new StubFactory(handler)).BuildServiceProvider();
        var exports = new MflExportClient(services.GetRequiredService<IHttpClientFactory>(), NullLogger<MflExportClient>.Instance);
        var league = new LeagueContext("123", SeasonContext.Current(2026), new Uri("https://example.test"));
        var export = await exports.ExportAsync(league, "players", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var discovery = new MflLeagueDiscoveryClient(services.GetRequiredService<IHttpClientFactory>());
        var leagues = await discovery.GetMyLeaguesAsync(SeasonContext.Current(2026), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, leagues.StatusCode);
        var draft = new MflDraftClient(exports, services.GetRequiredService<IHttpClientFactory>());
        var draftResult = await draft.GetDraftStateAsync(league, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, draftResult.StatusCode);
    }

    [Theory]
    [InlineData("injuries")]
    [InlineData("nflByeWeeks")]
    public async Task PublicSeasonExportUsesPublicEndpointWithoutLeagueId(string requestType)
    {
        var handler = new RecordingHandler("""{"injuries":{}}""");
        var exports = new MflExportClient(new StubFactory(handler), NullLogger<MflExportClient>.Instance);
        var league = new LeagueContext("123", SeasonContext.Current(2026), new Uri("https://league.example.test"));

        var result = await exports.ExportAsync(league, requestType, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("api.myfantasyleague.com", handler.RequestUri!.Host);
        Assert.Equal("/2026/export", handler.RequestUri.AbsolutePath);
        Assert.Contains($"TYPE={requestType}", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("L=", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyTeam_RetrievesAndParsesLivePointsWhenWeeklyResultsHaveOnlyStatuses()
    {
        var handler = new MyTeamHandler();
        var exports = new MflExportClient(new StubFactory(handler), NullLogger<MflExportClient>.Instance);
        var league = new LeagueContext("123", SeasonContext.Current(2026), new Uri("https://league.example.test"), "0001");
        var service = new MflLineupQueryService(exports, NullLogger<MflLineupQueryService>.Instance);
        var lineup = await service.GetAsync(league, 4, ["starter", "bench"], CancellationToken.None);
        Assert.Equal(4, lineup.Value!.Week);
        Assert.Equal(["starter"], lineup.Value.StarterPlayerIds);
        Assert.Equal(24.75m, lineup.Value.PlayerPoints["starter"]);
        Assert.Equal(0m, lineup.Value.PlayerPoints["bench"]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("league.example.test", uri.Host);
            Assert.Contains("L=123", uri.Query);
            Assert.Contains("W=4", uri.Query);
            Assert.Contains("JSON=1", uri.Query);
        });
        Assert.Contains("DETAILS=1", handler.Requests[1].Query);

        var injuries = await exports.ExportAsync(league, "injuries", null, CancellationToken.None);
        var statuses = MflJsonParser.ParseInjuries(injuries.Value!);
        Assert.Equal("O", PlayerAvailability.Code(new Player("starter", "Player", "RB", "BUF", Injury: statuses["starter"])));
        Assert.Equal("IR", PlayerAvailability.Code(new Player("bench", "Player", "RB", "BUF", Injury: statuses["bench"])));
        Assert.Equal("api.myfantasyleague.com", handler.Requests[2].Host);
        Assert.DoesNotContain("L=", handler.Requests[2].Query);
    }

    private sealed class MyTeamHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var body = request.RequestUri!.Query.Contains("TYPE=weeklyResults", StringComparison.Ordinal)
                ? """{"weeklyResults":{"week":"4","franchise":{"id":"0001","starters":"starter","player":[{"id":"starter","status":"starter"},{"id":"bench","status":"nonstarter"}]}}}"""
                : request.RequestUri.Query.Contains("TYPE=liveScoring", StringComparison.Ordinal)
                    ? """{"liveScoring":{"week":"4","franchise":{"id":"0001","players":{"player":[{"id":"starter","score":"24.75","status":"starter"},{"id":"bench","score":"0","status":"nonstarter"}]}}}}"""
                    : """{"injuries":{"week":"4","injury":[{"id":"starter","status":"Out"},{"id":"bench","status":"IR"}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
