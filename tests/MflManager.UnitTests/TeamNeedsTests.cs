using System.Text.Json.Nodes;
using MflManager.Application;
using MflManager.AgentServer;
using MflManager.Domain;

namespace MflManager.UnitTests;

public sealed class TeamNeedsTests
{
    private static LeagueSnapshot Snapshot() => new()
    {
        Context = new("123", SeasonContext.Current(2026), new Uri("https://example.test"), "0001"),
        CapturedAt = DateTimeOffset.UtcNow,
        LineupWeek = 5,
        Settings = new("League", 1, 18, 16, 2, 2, new Dictionary<string, string>(), StarterCount: 3,
            StartingPositions: [new("QB", 1, 1), new("RB", 1, 2), new("WR", 0, 2)]),
        NflByeWeeks = new Dictionary<string, int> { ["KCC"] = 5 },
        Roster =
        [
            new(new("qb", "Quarterback", "QB", "KCC"), RosterStatus.Active),
            new(new("rb", "Running Back", "RB", "BUF"), RosterStatus.Active),
            new(new("wr", "Wide Receiver", "WR", "BUF", Injury: "Questionable"), RosterStatus.Active),
            new(new("bench", "Bench Back", "RB", "BUF"), RosterStatus.Active),
            new(new("ir", "Injured Receiver", "WR", "KCC", Injury: "IR"), RosterStatus.Active),
            new(new("taxi", "Taxi Quarterback", "QB", "BUF"), RosterStatus.TaxiSquad)
        ],
        Lineup = [new("QB", "qb", true), new("RB", "rb", true), new("WR", "wr", true)],
        Sections = new Dictionary<SnapshotSection, SectionState>
        {
            [SnapshotSection.Lineup] = new(false, DateTimeOffset.UtcNow),
            [SnapshotSection.Rosters] = new(false, DateTimeOffset.UtcNow),
            [SnapshotSection.Injuries] = new(false, DateTimeOffset.UtcNow),
            [SnapshotSection.NflByeWeeks] = new(false, DateTimeOffset.UtcNow)
        }
    };

    [Fact]
    public void ByeStarterIsUncoveredAndQuestionableFlexHasEligibleBenchBackup()
    {
        var report = TeamNeeds.Build(Snapshot(), 5);
        Assert.True(report.LineupKnown);
        Assert.False(report.RosterStale);
        Assert.False(report.StatusStale);
        Assert.False(report.ByeWeeksStale);
        var qb = Assert.Single(report.Slots, x => x.Slot == "QB");
        Assert.True(qb.NeedsReplacement);
        Assert.Equal("BYE", qb.AvailabilityStatus);
        Assert.Empty(qb.OwnedAlternativePlayerIds);
        Assert.False(Assert.Single(report.Slots, x => x.Slot == "RB").NeedsReplacement);
        var flex = Assert.Single(report.Slots, x => x.Slot == "FLEX1");
        Assert.Equal("Q", flex.AvailabilityStatus);
        Assert.Contains("RB", flex.EligiblePositions);
        Assert.Equal(["bench"], flex.OwnedAlternativePlayerIds);
        Assert.True(Assert.Single(report.Roster, x => x.Player.Id == "wr").AvailabilityRisk);
        var ir = Assert.Single(report.Roster, x => x.Player.Id == "ir");
        Assert.Equal("IR BYE", ir.AvailabilityStatus);
        Assert.True(ir.IsUnavailable);
        Assert.True(Assert.Single(report.Roster, x => x.Player.Id == "taxi").IsUnavailable);
    }

    [Fact]
    public void RequestedWeekRecalculatesByeAndDoesNotMistakeUnknownLineupForEmptySlots()
    {
        var report = TeamNeeds.Build(Snapshot(), 6);
        Assert.False(report.LineupKnown);
        Assert.All(report.Slots, slot => Assert.Null(slot.NeedsReplacement));
        Assert.False(Assert.Single(report.Roster, x => x.Player.Id == "qb").IsBye);
        Assert.Equal("IR", Assert.Single(report.Roster, x => x.Player.Id == "ir").AvailabilityStatus);
    }

    [Fact]
    public void MissingAndStaleSourcesAreExplicitAndFreshEmptyLineupHasUnfilledSlots()
    {
        var snapshot = Snapshot();
        var empty = TeamNeeds.Build(snapshot with { Lineup = [] }, 5);
        Assert.All(empty.Slots, slot => Assert.True(slot.NeedsReplacement));
        var stale = TeamNeeds.Build(snapshot with { Sections = new Dictionary<SnapshotSection, SectionState>() }, 5);
        Assert.False(stale.LineupKnown);
        Assert.True(stale.RosterStale);
        Assert.True(stale.StatusStale);
        Assert.True(stale.ByeWeeksStale);
    }

    [Fact]
    public async Task TypedToolsExposeWeekAwareStatusesAndNeedSummaryAndRejectLeagueMismatch()
    {
        var snapshot = Snapshot();
        var state = new LeagueState();
        state.SelectLeague(snapshot.Context);
        state.UpdateSnapshot(snapshot);
        var tools = new MflToolRegistry(state, null!, null!, null!);
        var args = new JsonObject { ["league_id"] = "123", ["week"] = 5 };
        var report = Payload(await tools.CallAsync("mfl_get_team_needs", args, CancellationToken.None));
        Assert.Equal(5, report["week"]!.GetValue<int>());
        Assert.True(report["lineupKnown"]!.GetValue<bool>());
        Assert.Equal(3, report["slots"]!.AsArray().Count);
        var roster = Payload(await tools.CallAsync("mfl_get_roster", args, CancellationToken.None)).AsArray();
        var ir = roster.Single(x => x!["player"]!["id"]!.GetValue<string>() == "ir")!;
        Assert.Equal("IR BYE", ir["availabilityStatus"]!.GetValue<string>());
        Assert.Equal("IR", ir["player"]!["injury"]!.GetValue<string>());
        Assert.Equal(5, ir["byeWeek"]!.GetValue<int>());
        Assert.NotNull(ir["status"]);
        Assert.True(MflToolRegistry.IsReadOnly("mfl_get_team_needs"));
        Assert.True(MflToolRegistry.IsReadOnly("mfl_refresh_all"));
        Assert.False(MflToolRegistry.IsReadOnly("mfl_add_drop"));
        var metadata = tools.ListTools()["tools"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "mfl_get_team_needs")!;
        Assert.True(metadata["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.NotNull(metadata["inputSchema"]!["properties"]!["week"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CallAsync("mfl_get_team_needs", new JsonObject { ["league_id"] = "other" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.CallAsync("mfl_get_team_needs", new JsonObject { ["league_id"] = "123", ["week"] = 0 }, CancellationToken.None));
    }

    private static JsonNode Payload(JsonNode response) => JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!;
}
