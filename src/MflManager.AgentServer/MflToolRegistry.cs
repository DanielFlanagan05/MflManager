using System.Text.Json;
using System.Text.Json.Nodes;
using MflManager.Application;
using MflManager.Domain;

namespace MflManager.AgentServer;

public sealed class MflToolRegistry(
    ILeagueState state,
    IMflRefreshService refresh,
    IMflMutationCoordinator mutations,
    IMflCapabilityService capabilities)
{
    private static readonly string[] ToolNames =
    [
        "mfl_list_leagues", "mfl_select_league", "mfl_refresh_all", "mfl_get_dashboard", "mfl_get_league_settings",
        "mfl_get_standings", "mfl_get_matchups", "mfl_get_roster", "mfl_get_lineup", "mfl_search_players",
        "mfl_get_free_agents", "mfl_set_lineup", "mfl_add_drop", "mfl_submit_waiver_claims", "mfl_submit_blind_bids",
        "mfl_move_to_ir", "mfl_move_from_ir", "mfl_promote_taxi_player", "mfl_demote_taxi_player", "mfl_get_draft_state",
        "mfl_set_draft_queue", "mfl_submit_draft_pick", "mfl_get_pending_trades", "mfl_propose_trade", "mfl_respond_to_trade",
        "mfl_get_capabilities"
    ];

    public JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(ToolNames.Select(name => (JsonNode)new JsonObject
        {
            ["name"] = name,
            ["description"] = name.Replace("mfl_", string.Empty, StringComparison.Ordinal).Replace('_', ' '),
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["league_id"] = new JsonObject { ["type"] = "string", ["description"] = "Explicit MFL league id." },
                    ["preview"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                    ["confirmed"] = new JsonObject { ["type"] = "boolean", ["default"] = false }
                },
                ["additionalProperties"] = true
            }
        }).ToArray())
    };

    public async Task<JsonNode> CallAsync(string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (!ToolNames.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown tool: " + name);
        if (name == "mfl_list_leagues") return Content(new { selected = state.SelectedLeague });
        if (name == "mfl_select_league") return SelectLeague(args);
        var league = RequireLeague(args);
        var snapshot = state.CurrentSnapshot;
        return name switch
        {
            "mfl_refresh_all" => Content(await refresh.RefreshAllAsync(league, RefreshReason.Manual, cancellationToken).ConfigureAwait(false)),
            "mfl_get_dashboard" => Content(new { snapshot?.Settings, snapshot?.Standings, snapshot?.Matchups, snapshot?.Draft, snapshot?.CapturedAt }),
            "mfl_get_league_settings" => Content(snapshot?.Settings),
            "mfl_get_standings" => Content(snapshot?.Standings ?? []),
            "mfl_get_matchups" => Content(snapshot?.Matchups ?? []),
            "mfl_get_roster" => Content(snapshot?.Roster ?? []),
            "mfl_get_lineup" => Content(snapshot?.Lineup ?? []),
            "mfl_search_players" => Content(Search(snapshot?.Players ?? [], Required(args, "query"))),
            "mfl_get_free_agents" => Content(Search(snapshot?.FreeAgents ?? [], Optional(args, "query") ?? string.Empty)),
            "mfl_get_draft_state" => Content(snapshot?.Draft),
            "mfl_get_pending_trades" => Content(snapshot?.Trades ?? []),
            "mfl_get_capabilities" => Content(capabilities.GetCapabilities(snapshot, league.IsCommissioner)),
            "mfl_set_lineup" => await Execute(league, new SetLineupMutation(Int(args, "week"), Strings(args, "starters"), Strings(args, "tiebreakers")), args, cancellationToken).ConfigureAwait(false),
            "mfl_add_drop" => await Execute(league, new AddDropMutation(Required(args, "add_player_id"), Optional(args, "drop_player_id")), args, cancellationToken).ConfigureAwait(false),
            "mfl_submit_waiver_claims" => await Execute(league, Waivers(args, false), args, cancellationToken).ConfigureAwait(false),
            "mfl_submit_blind_bids" => await Execute(league, Waivers(args, true), args, cancellationToken).ConfigureAwait(false),
            "mfl_move_to_ir" => await Execute(league, StatusMutation(MutationKind.MoveToInjuredReserve, "ir", "DEACTIVATE", args), args, cancellationToken).ConfigureAwait(false),
            "mfl_move_from_ir" => await Execute(league, StatusMutation(MutationKind.MoveFromInjuredReserve, "ir", "ACTIVATE", args), args, cancellationToken).ConfigureAwait(false),
            "mfl_promote_taxi_player" => await Execute(league, StatusMutation(MutationKind.PromoteTaxiPlayer, "taxi_squad", "PROMOTE", args), args, cancellationToken).ConfigureAwait(false),
            "mfl_demote_taxi_player" => await Execute(league, StatusMutation(MutationKind.DemoteTaxiPlayer, "taxi_squad", "DEMOTE", args), args, cancellationToken).ConfigureAwait(false),
            "mfl_set_draft_queue" => await Execute(league, new PlayerListMutation(MutationKind.SetDraftQueue, "myDraftList", Strings(args, "player_ids")), args, cancellationToken).ConfigureAwait(false),
            "mfl_submit_draft_pick" => await Execute(league, new DraftPickMutation(Required(args, "player_id"), Int(args, "round"), Int(args, "pick"), league.FranchiseId ?? Required(args, "franchise_id"), Optional(args, "comments")), args, cancellationToken).ConfigureAwait(false),
            "mfl_propose_trade" => await Execute(league, new GenericMutation(MutationKind.ProposeTrade, "tradeProposal", ToParameters(args)), args, cancellationToken).ConfigureAwait(false),
            "mfl_respond_to_trade" => await Execute(league, new RespondToTradeMutation(Required(args, "trade_id"), Enum.Parse<TradeResponse>(Required(args, "response"), true)), args, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Tool is not implemented: " + name)
        };
    }

    private JsonNode SelectLeague(JsonObject args)
    {
        var id = Required(args, "league_id");
        var year = args["season"]?.GetValue<int>() ?? DateTimeOffset.UtcNow.Year;
        var host = Optional(args, "host") ?? "https://api.myfantasyleague.com";
        state.SelectLeague(new(id, new(year, year != DateTimeOffset.UtcNow.Year), new Uri(host), Optional(args, "franchise_id"), args["is_commissioner"]?.GetValue<bool>() ?? false));
        return Content(new { selected = state.SelectedLeague });
    }

    private async Task<JsonNode> Execute(LeagueContext league, MflMutation mutation, JsonObject args, CancellationToken cancellationToken)
    {
        if (args["preview"]?.GetValue<bool>() ?? true) return Content(mutations.Preview(league, mutation));
        return Content(await mutations.ExecuteAsync(league, mutation, args["confirmed"]?.GetValue<bool>() ?? false, cancellationToken).ConfigureAwait(false));
    }

    private LeagueContext RequireLeague(JsonObject args)
    {
        var league = state.SelectedLeague ?? throw new InvalidOperationException("No league is selected.");
        var explicitId = Required(args, "league_id");
        if (!string.Equals(explicitId, league.LeagueId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Explicit league context does not match the selected league.");
        return league;
    }

    private static WaiverMutation Waivers(JsonObject args, bool blind) => new(
        (args["claims"] as JsonArray ?? []).OfType<JsonObject>().Select((x, index) => new WaiverClaim(null, Required(x, "add_player_id"), Optional(x, "drop_player_id"), index + 1, x["bid"]?.GetValue<decimal>(), false)).ToArray(), blind, args["append"]?.GetValue<bool>() ?? false);
    private static MflMutation StatusMutation(MutationKind kind, string type, string field, JsonObject args) => new GenericMutation(kind, type, new Dictionary<string, string> { [field] = Required(args, "player_id") });
    private static IReadOnlyList<Player> Search(IReadOnlyList<Player> players, string query) => players.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Position.Equals(query, StringComparison.OrdinalIgnoreCase) || x.NflTeam.Equals(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
    private static IReadOnlyDictionary<string, string> ToParameters(JsonObject args) => args.Where(x => x.Value is JsonValue && x.Key is not "preview" and not "confirmed" and not "league_id").ToDictionary(x => x.Key.ToUpperInvariant(), x => x.Value!.ToString());
    private static string Required(JsonObject args, string name) => Optional(args, name) ?? throw new ArgumentException($"'{name}' is required.");
    private static string? Optional(JsonObject args, string name) => args[name]?.GetValue<string>();
    private static int Int(JsonObject args, string name) => args[name]?.GetValue<int>() ?? throw new ArgumentException($"'{name}' is required.");
    private static IReadOnlyList<string> Strings(JsonObject args, string name) => (args[name] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => x.Length > 0).ToArray();
    private static JsonObject Content(object? value) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) }), ["isError"] = false };
}
