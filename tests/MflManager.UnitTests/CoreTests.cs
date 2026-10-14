using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;
using MflManager.MflApi;

namespace MflManager.UnitTests;

public sealed class CoreTests
{
    [Fact]
    public void WaiverSupplementalParsers_ReadFaabAndYearToDatePoints()
    {
        var balance = MflJsonParser.ParseFaabBalance(
            """{"league":{"franchise":[{"id":"0001","bbidAvailableBalance":"79"},{"id":"0002","bbidAvailableBalance":"40"}]}}""", "0001");
        Assert.Equal(79m, balance);
        Assert.Null(MflJsonParser.ParseFaabBalance("{}", "0001"));
        var points = MflJsonParser.ParseSeasonPlayerPoints(
            """{"playerScores":{"playerScore":[{"id":"p1","score":"32.75"},{"id":"p2","score":"0"},{"id":"p3"}]}}""");
        Assert.Equal(32.75m, points["p1"]);
        Assert.Equal(0m, points["p2"]);
        Assert.False(points.ContainsKey("p3"));
    }

    [Fact]
    public void Calendar_ExpandsKnownWeeklyRecurrenceAndRejectsUnknownWindowEnd()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var date = MflJsonParser.ParseNextWaiverProcessingAt(
            """{"calendar":{"event":{"title":"Waiver requests","start_time":"1790000000","end_time":"1800000000","happens":"weekly"}}}""", now);
        Assert.True(date > now && date <= now.AddDays(7));
        Assert.Null(MflJsonParser.ParseNextWaiverProcessingAt(
            """{"calendar":{"event":{"title":"Waiver requests","start_time":"1790000000","end_time":"1800000000","happens":"custom"}}}""", now));
    }

    [Fact]
    public void Waivers_DecodeBlindBidAddsDropsSingletonAndArray()
    {
        var claims = MflJsonParser.ParseWaivers("""
            {"pendingWaivers":{"blindBidWaiverRequest":[
                {"round":"1","addsDrops":"p1_12.50_p2,p3_0_0000"},
                {"round":"2","addsDrops":"p4_3_p5"}
            ]}}
            """);
        Assert.Equal(3, claims.Count);
        Assert.Equal("p1", claims[0].AddPlayerId);
        Assert.Equal(12.50m, claims[0].BidAmount);
        Assert.Equal("p2", claims[0].DropPlayerId);
        Assert.Null(claims[1].DropPlayerId);
        Assert.Equal(2, claims[1].Priority);
        Assert.Single(MflJsonParser.ParseWaivers("""{"pendingWaivers":{"blindBidWaiverRequest":{"addsDrops":"p1_1_0000"}}}"""));
        Assert.Empty(MflJsonParser.ParseWaivers("""{"pendingWaivers":{}}"""));
    }

    [Fact]
    public void Waivers_PreserveRoundForCancellationReplacement()
    {
        var claims = MflJsonParser.ParseWaivers("""
            {"pendingWaivers":{"blindBidWaiverRequest":{"round":"3","addsDrops":"p1_5_p2,p3_2_0000"}}}
            """);

        Assert.All(claims, claim => Assert.Equal(3, claim.Round));
        Assert.Equal([1, 2], claims.Select(claim => claim.Priority));
    }

    [Fact]
    public void WaiverRankings_FilterPositionAndPreserveOriginalPositionRanks()
    {
        PlayerRanking[] rankings = [new("Sam Darnold", "SEA", "QB", 20), new("Other Player", "DET", "RB", 30)];
        var snapshot = new LeagueSnapshot
        {
            Context = new("l1", SeasonContext.Current(2026), new Uri("https://www1.myfantasyleague.com"), "0001", false),
            CapturedAt = DateTimeOffset.UtcNow, AvailablePlayersReady = true, WeeklyRankingsReady = true, RankingWeek = 2,
            Rankings = rankings, FreeAgents = [new("p1", "Darnold, Sam", "QB", "SEA"), new("p2", "Player, Other", "RB", "DET")],
            FreeAgentCopies = new Dictionary<string, int> { ["p1"] = 3, ["p2"] = 1 }
        };
        var players = WaiverPlayerSelector.Select(snapshot);
        Assert.Equal("p1", Assert.Single(players).Player.Id);
        Assert.Equal("QB 20", players[0].PositionRank);
        Assert.Equal("Sam Darnold, SEA", players[0].DisplayName);
        Assert.Equal(3, players[0].AvailableCopies);
        Assert.Equal("3", players[0].CopiesText);
        Assert.Equal("p2", Assert.Single(WaiverPlayerSelector.Select(snapshot, "RB")).Player.Id);
        Assert.Empty(WaiverPlayerSelector.Select(snapshot with { AvailablePlayersReady = false }));
        Assert.Empty(WaiverPlayerSelector.Select(snapshot with { Roster = [new(snapshot.FreeAgents[0], RosterStatus.Active)] }));
    }
    [Fact]
    public void FreeAgentParser_ResolvesIdOnlyFeedUsingPlayerCatalog()
    {
        var player = new Player("p1", "Known, Player", "RB", "DET");
        var parsed = MflJsonParser.ParseFreeAgents("""
            {"freeAgents":{"player":[{"id":"p1"},{"id":"unknown"}]}}
            """, new Dictionary<string, Player> { [player.Id] = player });
        Assert.Equal(player, Assert.Single(parsed));
    }

    [Fact]
    public void FreeAgentParser_CalculatesAvailableCopiesFromLeagueLimitAndRosters()
    {
        var copies = MflJsonParser.ParseFreeAgentCopies(
            """{"freeAgents":{"leagueUnit":{"unit":"LEAGUE","player":[{"id":"p1"},{"id":"p2"}]}}}""",
            """{"rosters":{"franchise":[{"id":"1","player":[{"id":"p1"},{"id":"p2"}]},{"id":"2","player":{"id":"p1"}}]}}""",
            3);

        Assert.Equal(1, copies["p1"]);
        Assert.Equal(2, copies["p2"]);
        Assert.Equal(2, copies.Count);
        Assert.Empty(MflJsonParser.ParseFreeAgentCopies("{}", "{}", 0));
    }

    [Fact]
    public void PlayerRosterStatusParser_ReadsMflPluralWrapperAndDoesNotStartNonStarters()
    {
        const string response = """
            {"playerRosterStatuses":{"playerRosterStatus":[
              {"id":"p1","is_fa":"0","roster_franchise":{"id":"0001","status":"S"}},
              {"id":"p2","is_fa":"1","roster_franchise":{"id":"0001","status":"NON_STARTER"}}
            ]}}
            """;
        Assert.Equal("p1", Assert.Single(MflJsonParser.ParsePlayerRosterStatus(response, 2, "0001").StarterPlayerIds));
        Assert.True(MflJsonParser.ParsePlayerAvailability(response, "0001").Single(x => x.PlayerId == "p2").CanAdd);
        var liveShape = response.Replace("\"playerRosterStatus\":", "\"playerStatus\":", StringComparison.Ordinal);
        Assert.Equal("p1", Assert.Single(MflJsonParser.ParsePlayerRosterStatus(liveShape, 2, "0001").StarterPlayerIds));
        Assert.Equal(2, MflJsonParser.ParsePlayerAvailability(liveShape, "0001").Count);
    }

    [Fact]
    public void MflWeekStatusParser_UsesDedicatedLineupWeek()
    {
        var status = MflJsonParser.ParseMflWeekStatus("""
            {"mfl_status":{"weeks":{"LineupWeek":"3","CurrentWeek":"2","UpcomingWeek":"3","CompletedWeek":"1","LiveScoringWeek":"2"}}}
            """);

        Assert.Equal(3, status.LineupWeek);
        Assert.Equal(2, status.CurrentWeek);
        Assert.Equal(3, status.UpcomingWeek);
        Assert.Equal(2, status.ActiveLineupWeek);
        Assert.Equal(3, (status with { CompletedWeek = 2 }).ActiveLineupWeek);
    }

    [Fact]
    public void WeeklyScoresParser_ReadsAllFranchiseScores()
    {
        var scores = MflJsonParser.ParseWeeklyScores("""
            {"weeklyResults":{"week":"2","franchise":[
              {"id":"0001","score":"112.35"},{"id":"0002","score":"98"}]}}
            """);

        Assert.Equal(112.35m, scores["0001"]);
        Assert.Equal(98m, scores["0002"]);
    }

    [Fact]
    public void WeeklyTeamResultParser_ReadsSelectedLineupAndPlayerPoints()
    {
        var result = MflJsonParser.ParseWeeklyTeamResult("""
            {"weeklyResults":{"week":"3","franchise":[
              {"id":"0001","starters":"other","player":{"id":"other","score":"1"}},
              {"id":"0002","starters":"qb1,rb1,","nonstarters":"wr1,",
               "player":[{"id":"qb1","score":"18.75"},{"id":"rb1","score":"9"},{"id":"wr1","score":"0"}]}]}}
            """, 2, "0002");

        Assert.Equal(3, result.Week);
        Assert.Equal(["qb1", "rb1"], result.StarterPlayerIds);
        Assert.Equal(18.75m, result.PlayerPoints["qb1"]);
        Assert.Equal(0m, result.PlayerPoints["wr1"]);
        Assert.DoesNotContain("other", result.PlayerPoints.Keys);
    }

    [Fact]
    public void TeamViewTable_UsesMyTeamSlotAndBenchOrdering()
    {
        var settings = new LeagueSettings("League", 1, 18, 6, 1, 0,
            new Dictionary<string, string>(), 3, 2,
            [new("QB", 1, 1), new("RB", 1, 2), new("WR", 0, 1)]);
        RosterPlayer[] roster =
        [
            new(new Player("wr1", "Zulu, Will", "WR", "SEA"), RosterStatus.Active),
            new(new Player("qb2", "Backup, Aaron", "QB", "DET"), RosterStatus.Active),
            new(new Player("rb1", "Runner, Rita", "RB", "GB"), RosterStatus.Active),
            new(new Player("qb1", "Starter, Quinn", "QB", "BUF"), RosterStatus.Active),
            new(new Player("rb2", "Bench, Bob", "RB", "DAL"), RosterStatus.Active),
            new(new Player("wr2", "Reserve, Riley", "WR", "MIA"), RosterStatus.InjuredReserve)
        ];
        var data = new TeamViewData(2, roster, ["qb1", "rb1", "wr1"],
            new Dictionary<string, decimal> { ["qb1"] = 20m, ["rb1"] = 12.5m, ["wr1"] = 8m, ["qb2"] = 3m });

        var rows = TeamViewTable.Build(settings, data);

        Assert.Equal(["QB", "RB", "FLEX1"], rows.Starters.Select(x => x.Slot));
        Assert.Equal(["qb1", "rb1", "wr1"], rows.Starters.Select(x => x.PlayerId));
        Assert.Equal(["qb2", "rb2", "wr2"], rows.Bench.Select(x => x.PlayerId));
        Assert.Equal("3", rows.Bench[0].PointsText);
        Assert.Equal("InjuredReserve", rows.Bench[2].RosterStatus);
    }

    [Fact]
    public void LeagueRankingTable_SortsOverallPointsOnceAndHighlightsCurrentTeam()
    {
        var data = new LeagueRankingData(2,
        [
            new Standing("0001", "Fallback One", 0, 0, 0, 100m, 0),
            new Standing("0002", "Fallback Two", 0, 0, 0, 140m, 0),
            new Standing("0003", "Fallback Three", 0, 0, 0, 120m, 0)
        ], new Dictionary<string, decimal> { ["0001"] = 10m, ["0002"] = 20m, ["0003"] = 30m });

        var rows = LeagueRankingTable.Build(data,
        [
            new Franchise("0001", "Team One"),
            new Franchise("0002", "Team Two"),
            new Franchise("0003", "Team Three")
        ], "0003");

        Assert.Equal(["0002", "0003", "0001"], rows.Select(x => x.FranchiseId));
        Assert.Equal([1, 2, 3], rows.Select(x => x.Rank));
        Assert.Equal(30m, rows[1].CurrentWeekPoints);
        Assert.True(rows[1].IsMyTeam);
        Assert.Equal("Team Three (My Team)", rows[1].TeamDisplay);
    }

    [Fact]
    public async Task LeagueRankingsService_RequestsOnlyRequiredEndpoints()
    {
        var exports = new RecordingExportClient();
        var service = new MflLeagueRankingsService(exports,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MflLeagueRankingsService>.Instance);
        var league = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");

        var result = await service.GetAsync(league, 1, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["mfl_status", "leagueStandings", "weeklyResults"], exports.Requests.Select(x => x.Type));
        Assert.Equal("2", exports.Requests[2].Parameters!["W"]);
    }

    [Fact]
    public async Task TeamViewService_RequestsCurrentWeekRosterAndSelectedTeamResults()
    {
        var exports = new RecordingExportClient();
        var service = new MflTeamViewService(exports,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MflTeamViewService>.Instance);
        var league = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");
        var players = new Dictionary<string, Player>
        {
            ["qb1"] = new("qb1", "Starter, Quinn", "QB", "BUF"),
            ["rb1"] = new("rb1", "Runner, Rita", "RB", "GB")
        };

        var result = await service.GetAsync(league, "0002", 1, players, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["mfl_status", "rosters", "weeklyResults"], exports.Requests.Select(x => x.Type));
        Assert.Equal("2", exports.Requests[2].Parameters!["W"]);
        Assert.Equal("0002", exports.Requests[2].Parameters!["F"]);
        Assert.Equal(2, result.Value!.Roster.Count);
        Assert.Equal("qb1", Assert.Single(result.Value.StarterPlayerIds));
        Assert.Equal(18.5m, result.Value.PlayerPoints["qb1"]);
    }

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
    public void MutationResult_SurfacesWaiverLockFromHtmlBody()
    {
        var result = MflHttpResult.ParseMutation("<html><div class=\"error\">Waiver requests are locked for this league.</div></html>");

        Assert.False(result.Success);
        Assert.Equal("MFL_REJECTED", result.Code);
        Assert.Contains("locked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalendarParser_ReturnsNextWaiverProcessingEvent()
    {
        var now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");
        var processing = MflJsonParser.ParseNextWaiverProcessingAt("""
            {"calendar":{"event":[
              {"name":"Lock Waivers","startTime":"1790168400"},
              {"name":"Process Blind Bid Waivers","startTime":"1790193600"},
              {"name":"Process Blind Bid Waivers","startTime":"1790798400"}]}}
            """, now);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790193600), processing);
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
    public void CancelWaiverSerialization_ReplacesRoundAndCanClearLastClaim()
    {
        var canceled = new WaiverClaim(null, "101", "201", 1, 7.5m, Round: 3);
        var remaining = new WaiverClaim(null, "102", null, 2, 4m, Round: 3);
        var parameters = new CancelWaiverClaimMutation(canceled, [remaining], true, 3).ToParameters();

        Assert.Equal("blindBidWaiverRequest", parameters["TYPE"]);
        Assert.Equal("3", parameters["ROUND"]);
        Assert.Equal("102_4_0000", parameters["PICKS"]);
        Assert.Equal("1", parameters["REPLACE"]);
        Assert.Equal(string.Empty, new CancelWaiverClaimMutation(canceled, [], true, 3).ToParameters()["PICKS"]);
        Assert.True(new ConfirmationPolicy().RequiresConfirmation(MutationKind.CancelWaiverClaim));
    }

    [Fact]
    public void ReorderWaiverSerialization_PreservesConditionalPriorityWithSharedDrop()
    {
        var original = new[]
        {
            new WaiverClaim(null, "p1", "d1", 1, 9m),
            new WaiverClaim(null, "p2", "d1", 2, 7m),
            new WaiverClaim(null, "p3", "d1", 3, 5m)
        };
        var parameters = new ReorderWaiverClaimsMutation(original, [original[2], original[0], original[1]], true, 1).ToParameters();

        Assert.Equal("blindBidWaiverRequest", parameters["TYPE"]);
        Assert.Equal("p3_5_d1,p1_9_d1,p2_7_d1", parameters["PICKS"]);
        Assert.Equal("1", parameters["REPLACE"]);
        Assert.True(new ConfirmationPolicy().RequiresConfirmation(MutationKind.ReorderWaiverClaims));
    }

    [Fact]
    public async Task ReorderWaiverCoordinator_UsesFreshRoundAndVerifiesPriority()
    {
        var league = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");
        var original = new[]
        {
            new WaiverClaim(null, "p1", "d1", 1, 8m),
            new WaiverClaim(null, "p2", null, 2, 3m),
            new WaiverClaim(null, "p3", null, 3, 1m)
        };
        var desired = new[] { original[2], original[0], original[1] };
        var refreshed = desired.Select((claim, index) => claim with { Priority = index + 1 }).ToArray();
        var state = new LeagueState();
        state.SelectLeague(league);
        state.UpdateSnapshot(new LeagueSnapshot { Context = league, CapturedAt = DateTimeOffset.UtcNow, Waivers = original });
        var imports = new RecordingImportClient();
        var coordinator = new MflMutationCoordinator(imports, new UnusedDraftClient(),
            new CancellationRefreshService(league, refreshed), new RecordingJournal(),
            new ConfirmationPolicy(), new CancellationExportClient(), state);

        var receipt = await coordinator.ExecuteAsync(league,
            new ReorderWaiverClaimsMutation(original, desired, true, 1), true, CancellationToken.None);

        Assert.True(receipt.Accepted);
        Assert.True(receipt.Verified);
        Assert.Equal("p3_1_0000,p1_8_d1,p2_3_0000", imports.Parameters!["PICKS"]);
        Assert.Equal("1", imports.Parameters["REPLACE"]);
    }

    [Fact]
    public async Task ReorderWaiverCoordinator_RejectsStaleClaimsBeforeImport()
    {
        var league = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");
        var original = new[]
        {
            new WaiverClaim(null, "p1", "d1", 1, 8m),
            new WaiverClaim(null, "p2", null, 2, 4m),
            new WaiverClaim(null, "p3", null, 3, 1m)
        };
        var state = new LeagueState();
        state.SelectLeague(league);
        var imports = new RecordingImportClient();
        var coordinator = new MflMutationCoordinator(imports, new UnusedDraftClient(),
            new CancellationRefreshService(league, []), new RecordingJournal(),
            new ConfirmationPolicy(), new CancellationExportClient(), state);

        var receipt = await coordinator.ExecuteAsync(league,
            new ReorderWaiverClaimsMutation(original, [original[1], original[0], original[2]], true, 1), true, CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.Null(imports.Parameters);
    }

    [Fact]
    public async Task CancelWaiverCoordinator_PreservesClaimsFromFreshPendingResponse()
    {
        var league = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001");
        var target = new WaiverClaim(null, "p1", "d1", 1, 8m, Round: 1);
        var originallyRemaining = new WaiverClaim(null, "p2", null, 2, 3m, Round: 1);
        var refreshedRemaining = new[]
        {
            originallyRemaining with { Priority = 1 },
            new WaiverClaim(null, "p3", null, 2, 1m, Round: 1)
        };
        var state = new LeagueState();
        state.SelectLeague(league);
        state.UpdateSnapshot(new LeagueSnapshot { Context = league, CapturedAt = DateTimeOffset.UtcNow, Waivers = [target, originallyRemaining] });
        var imports = new RecordingImportClient();
        var refresh = new CancellationRefreshService(league, refreshedRemaining);
        var coordinator = new MflMutationCoordinator(imports, new UnusedDraftClient(), refresh, new RecordingJournal(),
            new ConfirmationPolicy(), new CancellationExportClient(), state);

        var receipt = await coordinator.ExecuteAsync(league,
            new CancelWaiverClaimMutation(target, [originallyRemaining], true, 1), true, CancellationToken.None);

        Assert.True(receipt.Accepted);
        Assert.True(receipt.Verified);
        Assert.Equal("p2_3_0000,p3_1_0000", imports.Parameters!["PICKS"]);
        Assert.Equal("1", imports.Parameters["REPLACE"]);
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
    public void LineupSerialization_UsesWeekAndCompleteStarterList()
    {
        var parameters = new SetLineupMutation(7, ["qb1", "rb1", "rb2"], []).ToParameters();

        Assert.Equal("lineup", parameters["TYPE"]);
        Assert.Equal("7", parameters["W"]);
        Assert.Equal("qb1,rb1,rb2", parameters["STARTERS"]);
        Assert.Equal(string.Empty, parameters["TIEBREAKERS"]);
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

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("Questionable", null, "Q")]
    [InlineData("Doubtful", null, "D")]
    [InlineData("Out", null, "O")]
    [InlineData("Exempt", null, "IA")]
    [InlineData("Suspended", null, "SUS")]
    [InlineData("IR", null, "IR")]
    [InlineData("IR-R", null, "IR-R")]
    [InlineData("IR-PUP", null, "PUP")]
    [InlineData("IR-NFI", null, "NFI")]
    [InlineData("Holdout", null, "IA")]
    [InlineData("Retired", null, "RET")]
    [InlineData(null, "R", "")]
    [InlineData(null, "S", "")]
    public void PlayerAvailability_UsesInjuryAndRecognizedInactiveCodes(string? injury, string? status, string expected)
    {
        Assert.Equal(expected, PlayerAvailability.Code(new Player("p1", "Player", "RB", "GB", status, injury)));
    }

    [Fact]
    public void InjuryParser_ReadsCurrentDesignationsAndRejectsUnexpectedBodies()
    {
        var statuses = MflJsonParser.ParseInjuries("""{"injuries":{"injury":[{"id":"p1","status":"Questionable"},{"id":"p2","status":"Exempt"}]}}""");
        Assert.Equal("Questionable", statuses["p1"]);
        Assert.Equal("Exempt", statuses["p2"]);
        Assert.Throws<System.Text.Json.JsonException>(() => MflJsonParser.ParseInjuries("{}"));
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
    public void AvailablePlayerSelector_UsesFreeAgentFeedAndExcludesRosterDuplicates()
    {
        var rosterPlayer = new Player("roster-id", "Roster, Riley", "WR", "DET");
        var snapshot = new LeagueSnapshot
        {
            Context = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001"),
            CapturedAt = DateTimeOffset.UtcNow,
            Players =
            [
                new Player("global-only", "Catalog, Casey", "QB", "BUF")
            ],
            FreeAgents =
            [
                new Player("available", "Free, Frankie", "RB", "GB", "R"),
                rosterPlayer,
                new Player("different-id", "Riley Roster", "WR", "DET"),
                new Player("ineligible", "Kicker, Kerry", "K", "DAL")
            ],
            Roster = [new RosterPlayer(rosterPlayer, RosterStatus.Active)]
        };

        var players = AvailablePlayerSelector.Select(snapshot,
            new HashSet<string>(["QB", "RB", "WR", "TE"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["available", "roster-id", "different-id", "ineligible"], StringComparer.OrdinalIgnoreCase));

        var player = Assert.Single(players);
        Assert.Equal("available", player.Id);
        Assert.Equal("R", player.Status); // Rookie status does not override free-agent availability.
    }

    [Theory]
    [InlineData("Mahomes, Patrick", "Patrick Mahomes")]
    [InlineData("  Patrick   Mahomes  ", "Patrick Mahomes")]
    [InlineData("", "Player name unavailable")]
    public void AvailablePlayerSelector_FormatsPlayerNamesConsistently(string source, string expected)
    {
        Assert.Equal(expected, AvailablePlayerSelector.DisplayName(source));
    }

    [Fact]
    public void LeagueSettingsParser_ReadsDynamicStarterRanges()
    {
        var settings = MflJsonParser.ParseSettings("""
            {"league":{"name":"Flexible League","startWeek":"1","endWeek":"18","currentWeek":"4",
              "starters":{"count":"9","position":[
                {"name":"QB","limit":"1"},{"name":"RB","limit":"2-4"},
                {"name":"WR","limit":"3-5"},{"name":"TE","limit":"1-3"}]}}}
            """);

        Assert.Equal(9, settings.StarterCount);
        Assert.Equal(4, settings.CurrentWeek);
        Assert.Equal(new LineupPositionRule("RB", 2, 4), settings.LineupRules.Single(x => x.Position == "RB"));
    }

    [Fact]
    public void LineupRules_BuildsFixedSlotsThenLeagueDefinedFlexSlots()
    {
        var settings = new LeagueSettings("League", 1, 18, 20, 0, 0, new Dictionary<string, string>(), 9, 1,
        [
            new("QB", 1, 1), new("RB", 2, 4), new("WR", 3, 5), new("TE", 1, 3)
        ]);

        var slots = LineupRules.BuildSlots(settings);

        Assert.Equal(["QB", "RB1", "RB2", "WR1", "WR2", "WR3", "FLEX1", "FLEX2", "TE"], slots.Select(x => x.Label));
        Assert.Equal(["RB", "TE", "WR"], slots.Single(x => x.Label == "FLEX1").EligiblePositions.Order());
    }

    [Fact]
    public void LineupRules_ActiveWeekPrefersMflLineupWeek()
    {
        var snapshot = new LeagueSnapshot
        {
            Context = new("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001"),
            CapturedAt = DateTimeOffset.UtcNow,
            LineupWeek = 3,
            RankingWeek = 4,
            Settings = new LeagueSettings("League", 1, 18, 20, 0, 0, new Dictionary<string, string>(), CurrentWeek: 2)
        };

        Assert.Equal(3, LineupRules.ActiveWeek(snapshot));
        Assert.Equal(2, LineupRules.ActiveWeek(snapshot with { LineupWeek = 0 }));
    }

    [Fact]
    public void RosterParser_ReturnsOnlySelectedFranchise()
    {
        var players = new Dictionary<string, Player>
        {
            ["p1"] = new("p1", "One", "QB", "BUF"),
            ["p2"] = new("p2", "Two", "RB", "DET")
        };
        var roster = MflJsonParser.ParseRoster("""
            {"rosters":{"franchise":[{"id":"0001","player":{"id":"p1"}},{"id":"0002","player":{"id":"p2"}}]}}
            """, players, "0001");

        Assert.Equal("p1", Assert.Single(roster).Player.Id);
    }

    [Fact]
    public void PlayerRosterStatusParser_ReadsStartersForSelectedTeamAndWeek()
    {
        var lineup = MflJsonParser.ParsePlayerRosterStatus("""
            {"playerRosterStatus":{"player":[
              {"id":"p1","roster_franchise":{"id":"0001","status":"S"}},
              {"id":"p2","roster_franchise":{"id":"0001","status":"NS"}},
              {"id":"p3","roster_franchise":{"id":"0002","status":"S"}}]}}
            """, 6, "0001");

        Assert.Equal(6, lineup.Week);
        Assert.Equal("p1", Assert.Single(lineup.StarterPlayerIds));
    }

    [Fact]
    public void WeeklyResultsLineupParser_ReadsCompleteSelectedFranchiseLineup()
    {
        var lineup = MflJsonParser.ParseWeeklyResultsLineup("""
            {"weeklyResults":{"week":"2","franchise":[
              {"id":"0001","starters":"goff,rb1,wr1,","nonstarters":"hurts,rb2,","player":[
                {"id":"goff","score":"22.45"},{"id":"hurts","score":"0"},{"id":"missing-score","status":"starter"}]},
              {"id":"0002","starters":"other"}]}}
            """, 1, "0001");

        Assert.Equal(2, lineup.Week);
        Assert.Equal(["goff", "rb1", "wr1"], lineup.StarterPlayerIds);
        Assert.DoesNotContain("hurts", lineup.StarterPlayerIds);
        Assert.Equal(22.45m, lineup.PlayerPoints["goff"]);
        Assert.Equal(0m, lineup.PlayerPoints["hurts"]);
        Assert.DoesNotContain("missing-score", lineup.PlayerPoints.Keys);
        var diagnostics = MflJsonParser.InspectWeeklyPlayerScores("""
            {"weeklyResults":{"franchise":{"id":"0001","player":[
              {"id":"goff","score":"22.45"},{"id":"hurts","score":"0"},{"id":"missing-score","status":"starter"}]}}}
            """, "0001");
        Assert.Equal(3, diagnostics.PlayerRecords);
        Assert.Equal(2, diagnostics.NumericScores);
        Assert.Equal(1, diagnostics.NonZeroScores);
        Assert.Equal(22.45m, diagnostics.TotalPoints);
        Assert.Equal("id,score,status", diagnostics.PlayerFields);
    }

    [Fact]
    public void PlayerAvailabilityParser_RequiresFreeAgentAndRejectsAddRestrictions()
    {
        var statuses = MflJsonParser.ParsePlayerAvailability("""
            {"playerRosterStatus":{"player":[
              {"id":"available","is_fa":"1","cant_add":"0","locked":"0"},
              {"id":"rostered","is_fa":"0"},
              {"id":"cant-add","is_fa":"1","roster_franchise":{"id":"0001","cant_add":"1"}},
              {"id":"locked","is_fa":"1","locked":"true"}]}}
            """, "0001");

        Assert.True(statuses.Single(x => x.PlayerId == "available").CanAdd);
        Assert.False(statuses.Single(x => x.PlayerId == "rostered").CanAdd);
        Assert.False(statuses.Single(x => x.PlayerId == "cant-add").CanAdd);
        Assert.False(statuses.Single(x => x.PlayerId == "locked").CanAdd);
    }

    [Fact]
    public void AvailablePlayerSelector_FailsClosedWithoutLeaguePositionRules()
    {
        var snapshot = new LeagueSnapshot
        {
            Context = new LeagueContext("1", SeasonContext.Current(2026), new Uri("https://example.test"), "0001"),
            CapturedAt = DateTimeOffset.UtcNow,
            FreeAgents = [new Player("available", "Free, Frankie", "RB", "GB")]
        };

        Assert.Empty(AvailablePlayerSelector.Select(snapshot, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["available"], StringComparer.OrdinalIgnoreCase)));
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

    private sealed class RecordingExportClient : IMflExportClient
    {
        public List<(string Type, IReadOnlyDictionary<string, string>? Parameters)> Requests { get; } = [];

        public Task<MflResult<string>> ExportAsync(LeagueContext league, string requestType,
            IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            Requests.Add((requestType, parameters));
            var body = requestType switch
            {
                "mfl_status" => "{\"mfl_status\":{\"weeks\":{\"LineupWeek\":\"3\",\"CurrentWeek\":\"2\",\"CompletedWeek\":\"1\"}}}",
                "leagueStandings" => "{\"leagueStandings\":{\"franchise\":{\"id\":\"0001\",\"pf\":\"100\"}}}",
                "rosters" => "{\"rosters\":{\"franchise\":{\"id\":\"0002\",\"player\":[{\"id\":\"qb1\"},{\"id\":\"rb1\"}]}}}",
                "weeklyResults" => "{\"weeklyResults\":{\"week\":\"2\",\"franchise\":[{\"id\":\"0001\",\"score\":\"25\"},{\"id\":\"0002\",\"score\":\"31\",\"starters\":\"qb1\",\"player\":[{\"id\":\"qb1\",\"score\":\"18.5\"},{\"id\":\"rb1\",\"score\":\"12.5\"}]}]}}",
                _ => throw new InvalidOperationException(requestType)
            };
            return Task.FromResult(MflResult<string>.Success(body, requestType));
        }
    }

    private sealed class CancellationExportClient : IMflExportClient
    {
        public Task<MflResult<string>> ExportAsync(LeagueContext league, string requestType,
            IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            Assert.Equal("pendingWaivers", requestType);
            return Task.FromResult(MflResult<string>.Success(
                """{"pendingWaivers":{"blindBidWaiverRequest":{"round":"1","addsDrops":"p1_8_d1,p2_3_0000,p3_1_0000"}}}""",
                "pending"));
        }
    }

    private sealed class RecordingImportClient : IMflImportClient
    {
        public IReadOnlyDictionary<string, string>? Parameters { get; private set; }
        public Task<MflResult<string>> ImportAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            Parameters = parameters;
            return Task.FromResult(MflResult<string>.Success("{}", "import"));
        }
    }

    private sealed class CancellationRefreshService(LeagueContext league, IReadOnlyList<WaiverClaim> waivers) : IMflRefreshService
    {
        public event EventHandler<LeagueSnapshot>? SnapshotUpdated;
        public Task<bool> RefreshPlayerStatusesAsync(LeagueContext requestedLeague, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<RefreshReport> RefreshAllAsync(LeagueContext requestedLeague, RefreshReason reason, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new LeagueSnapshot
            {
                Context = league,
                CapturedAt = now,
                Waivers = waivers,
                Sections = new Dictionary<SnapshotSection, SectionState> { [SnapshotSection.Waivers] = new(false, now) }
            };
            SnapshotUpdated?.Invoke(this, snapshot);
            return Task.FromResult(new RefreshReport(league, reason, now, now,
                [new(SnapshotSection.Waivers, true, null, TimeSpan.Zero)], snapshot));
        }
    }

    private sealed class RecordingJournal : IOperationJournal
    {
        public Task AppendAsync(OperationJournalEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<OperationJournalEntry>> GetRecentAsync(string leagueId, int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OperationJournalEntry>>([]);
    }

    private sealed class UnusedDraftClient : IMflDraftClient
    {
        public Task<MflResult<DraftState>> GetDraftStateAsync(LeagueContext league, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Draft state is not used for waiver cancellation.");
        public Task<MflResult<string>> SubmitAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Draft submission is not used for waiver cancellation.");
    }
}
