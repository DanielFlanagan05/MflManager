using MflManager.Domain;

namespace MflManager.Application;

public sealed class MflCapabilityService : IMflCapabilityService
{
    private static readonly MflCapability[] OwnerCapabilities =
    [
        Read("league", "League", CapabilityAccess.Owner, "League", "mfl_get_league_settings"),
        Read("rules", "League", CapabilityAccess.Owner, "League", "mfl_get_league_settings"),
        Read("rosters", "League", CapabilityAccess.Owner, "My Team", "mfl_get_roster"),
        Read("freeAgents", "Players", CapabilityAccess.Owner, "Players", "mfl_get_free_agents"),
        Read("schedule", "League", CapabilityAccess.Owner, "Matchups", "mfl_get_matchups"),
        Read("calendar", "League", CapabilityAccess.Owner, "League", null),
        Read("transactions", "Transactions", CapabilityAccess.Owner, "League", null),
        Read("pendingWaivers", "Transactions", CapabilityAccess.Owner, "Waivers", null),
        Read("pendingTrades", "Transactions", CapabilityAccess.Owner, "Trades", "mfl_get_pending_trades"),
        Read("tradeBait", "Transactions", CapabilityAccess.Owner, "Trades", null),
        Read("leagueStandings", "League", CapabilityAccess.Owner, "Standings", "mfl_get_standings"),
        Read("weeklyResults", "League", CapabilityAccess.Owner, "Matchups", "mfl_get_matchups"),
        Read("players", "NFL", CapabilityAccess.Public, "Players", "mfl_search_players"),
        Read("injuries", "NFL", CapabilityAccess.Public, "Players", null),
        Read("playerRanks", "NFL", CapabilityAccess.Owner, "Players", null),
        Read("projectedScores", "NFL", CapabilityAccess.Owner, "Players", null),
        Read("adp", "NFL", CapabilityAccess.Public, "Players", null),
        Read("aav", "NFL", CapabilityAccess.Public, "Players", null),
        Read("draftResults", "Draft", CapabilityAccess.Owner, "Draft", "mfl_get_draft_state"),
        Read("myDraftList", "Draft", CapabilityAccess.Owner, "Draft", "mfl_get_draft_state"),
        Read("keepers", "Players", CapabilityAccess.Owner, "My Team", null),
        Read("myWatchList", "Players", CapabilityAccess.Owner, "Players", null),
        Mutation("lineup", "Roster", CapabilityAccess.Owner, ["W", "STARTERS"], "My Team", "mfl_set_lineup"),
        Mutation("fcfsWaiver", "Roster", CapabilityAccess.Owner, ["ADD"], "Players", "mfl_add_drop"),
        Mutation("waiverRequest", "Waivers", CapabilityAccess.Owner, ["ROUND", "PICKS"], "Waivers", "mfl_submit_waiver_claims"),
        Mutation("blindBidWaiverRequest", "Waivers", CapabilityAccess.Owner, ["PICKS"], "Waivers", "mfl_submit_blind_bids"),
        Mutation("ir", "Roster", CapabilityAccess.Owner, [], "My Team", "mfl_move_to_ir"),
        Mutation("taxi_squad", "Roster", CapabilityAccess.Owner, [], "My Team", "mfl_promote_taxi_player"),
        Mutation("keepers", "Players", CapabilityAccess.Owner, ["KEEP"], "My Team", null),
        Mutation("myWatchList", "Players", CapabilityAccess.Owner, [], "Players", null),
        Mutation("myDraftList", "Draft", CapabilityAccess.Owner, ["PLAYERS"], "Draft", "mfl_set_draft_queue"),
        Mutation("live_draft", "Draft", CapabilityAccess.Owner, ["CMD", "PLAYER_PICK", "ROUND", "PICK"], "Draft", "mfl_submit_draft_pick"),
        Mutation("tradeProposal", "Trades", CapabilityAccess.Owner, ["OFFERED_TO"], "Trades", "mfl_propose_trade"),
        Mutation("tradeResponse", "Trades", CapabilityAccess.Owner, ["TRADE_ID", "RESPONSE"], "Trades", "mfl_respond_to_trade"),
        Mutation("tradeBait", "Trades", CapabilityAccess.Owner, [], "Trades", null),
        Mutation("messageBoard", "Communications", CapabilityAccess.Owner, ["SUBJECT", "BODY"], "Messages", null),
        Mutation("pollVote", "Communications", CapabilityAccess.Owner, ["POLL_ID", "ANSWER_ID"], "Messages", null)
    ];

    private static readonly MflCapability[] CommissionerCapabilities =
    [
        Mutation("live_draft:PAUSE", "Commissioner", CapabilityAccess.Commissioner, ["CMD"], "Draft", null),
        Mutation("live_draft:RESUME", "Commissioner", CapabilityAccess.Commissioner, ["CMD"], "Draft", null),
        Mutation("live_draft:SKIP", "Commissioner", CapabilityAccess.Commissioner, ["CMD"], "Draft", null),
        Mutation("live_draft:UNDO", "Commissioner", CapabilityAccess.Commissioner, ["CMD"], "Draft", null),
        Mutation("accounting", "Commissioner", CapabilityAccess.Commissioner, ["FRANCHISE", "AMOUNT", "DESCRIPTION"], "League", null),
        Mutation("salaryAdj", "Commissioner", CapabilityAccess.Commissioner, ["DATA"], "League", null),
        Mutation("playerScoreAdjustment", "Commissioner", CapabilityAccess.Commissioner, ["PLAYER", "WEEK", "POINTS"], "League", null),
        Mutation("franchiseScoreAdjustment", "Commissioner", CapabilityAccess.Commissioner, ["FRANCHISE", "WEEK", "POINTS"], "League", null)
    ];

    public CapabilitySet GetCapabilities(LeagueSnapshot? snapshot, bool isCommissioner)
    {
        var result = new List<MflCapability>(OwnerCapabilities);
        if (isCommissioner) result.AddRange(CommissionerCapabilities);
        return new CapabilitySet(result);
    }

    private static MflCapability Read(string type, string category, CapabilityAccess access, string ui, string? tool) =>
        new(type, category, false, access, ["L"], "JSON/XML", ui, tool, true);

    private static MflCapability Mutation(string type, string category, CapabilityAccess access, IReadOnlyList<string> required, string ui, string? tool) =>
        new(type, category, true, access, ["L", .. required], "JSON/XML", ui, tool, true);
}
