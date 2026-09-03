using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MflManager.Domain;

namespace MflManager.MflApi;

public sealed class SingleOrArrayJsonConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType == JsonTokenType.StartArray)
            return JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? [];
        var item = JsonSerializer.Deserialize<T>(ref reader, options);
        return item is null ? [] : [item];
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

public static class MflJsonParser
{
    private static readonly HashSet<string> DisplayPositions = new(StringComparer.OrdinalIgnoreCase)
        { "WR", "RB", "QB", "TE", "K", "DEF" };

    public static IReadOnlyList<Player> ParsePlayers(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "player").Select(x =>
        {
            var position = NormalizePosition(JsonTraversal.Value(x, "position", "position_code", "slot"));
            return new Player(JsonTraversal.Value(x, "id") ?? string.Empty,
                JsonTraversal.Value(x, "name") ?? "Unknown player", position,
                JsonTraversal.Value(x, "team", "team_code", "nfl_team") ?? string.Empty,
                JsonTraversal.Value(x, "status"), JsonTraversal.Value(x, "injury", "injury_status"),
                Decimal(x, "score", "projectedScore"), Decimal(x, "averagePick", "adp"));
        }).Where(x => x.Id.Length > 0 && DisplayPositions.Contains(x.Position)).DistinctBy(x => x.Id).ToArray();
    }

    private static string NormalizePosition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = raw.ToUpperInvariant().Split([' ', '/', '-', '_', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var position = tokens.FirstOrDefault(DisplayPositions.Contains);
        if (position is not null) return position;
        // MFL's players export commonly encodes team positions compactly (for
        // example TMWR/TMRB/TMQB). Strip the team marker and retain only the
        // display position instead of filtering these valid players out.
        foreach (var code in DisplayPositions.OrderByDescending(x => x.Length))
            if (tokens.Any(token => token.EndsWith(code, StringComparison.OrdinalIgnoreCase))) return code;
        return tokens.Any(x => x is "D" or "DST") ? "DEF" : string.Empty;
    }

    public static IReadOnlyList<Franchise> ParseFranchises(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "franchise").Select(x => new Franchise(
            JsonTraversal.Value(x, "id") ?? string.Empty,
            JsonTraversal.Value(x, "name") ?? "Unknown franchise",
            JsonTraversal.Value(x, "abbrev"),
            JsonTraversal.Value(x, "owner_name", "ownerName"),
            JsonTraversal.Value(x, "email"),
            JsonTraversal.Value(x, "logo", "icon"))).Where(x => x.Id.Length > 0).DistinctBy(x => x.Id).ToArray();
    }

    public static LeagueSettings ParseSettings(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var league = JsonTraversal.ObjectsNamed(root, "league").FirstOrDefault() ?? root;
        var values = league.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return new LeagueSettings(
            JsonTraversal.Value(league, "name") ?? "League",
            Int(league, "startWeek", "start_week") ?? 1,
            Int(league, "endWeek", "end_week") ?? 18,
            Int(league, "rosterSize", "roster_size") ?? 0,
            Int(league, "injuredReserve", "ir_size") ?? 0,
            Int(league, "taxiSquad", "taxi_squad") ?? 0,
            values);
    }

    public static IReadOnlyList<RosterPlayer> ParseRoster(string json, IReadOnlyDictionary<string, Player> players)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "player").Select(x =>
        {
            var id = JsonTraversal.Value(x, "id") ?? string.Empty;
            var statusText = JsonTraversal.Value(x, "status") ?? "ROSTER";
            var status = statusText.Equals("IR", StringComparison.OrdinalIgnoreCase) ? RosterStatus.InjuredReserve :
                statusText.Equals("TAXI_SQUAD", StringComparison.OrdinalIgnoreCase) || statusText.Equals("TAXI", StringComparison.OrdinalIgnoreCase)
                    ? RosterStatus.TaxiSquad : RosterStatus.Active;
            var player = players.GetValueOrDefault(id) ?? new Player(id, "Player " + id, string.Empty, string.Empty);
            return new RosterPlayer(player, status, Decimal(x, "salary"), Int(x, "contractYear", "contract_year"));
        }).Where(x => x.Player.Id.Length > 0).ToArray();
    }

    public static IReadOnlyList<Standing> ParseStandings(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "franchise").Select((x, index) => new Standing(
            JsonTraversal.Value(x, "id") ?? string.Empty,
            JsonTraversal.Value(x, "name") ?? JsonTraversal.Value(x, "id") ?? "Franchise",
            Int(x, "h2hw", "wins") ?? 0,
            Int(x, "h2hl", "losses") ?? 0,
            Int(x, "h2ht", "ties") ?? 0,
            Decimal(x, "pf", "points") ?? 0,
            Int(x, "rank") ?? index + 1)).Where(x => x.FranchiseId.Length > 0).ToArray();
    }

    public static DraftState ParseDraft(string json)
    {
        var root = JsonNode.Parse(json);
        var picks = JsonTraversal.ObjectsNamed(root, "draftPick")
            .Select((x, index) => new DraftPick(
                Int(x, "round") ?? 0,
                Int(x, "pick") ?? 0,
                Int(x, "overallPickNumber", "overall") ?? index + 1,
                JsonTraversal.Value(x, "franchise", "franchise_id") ?? string.Empty,
                JsonTraversal.Value(x, "player"),
                JsonTraversal.Value(x, "comments")))
            .DistinctBy(x => (x.Round, x.Pick, x.FranchiseId, x.PlayerId))
            .OrderBy(x => x.Round).ThenBy(x => x.Pick).ThenBy(x => x.Overall).ToArray();
        var next = picks.LastOrDefault()?.Overall + 1 ?? 1;
        var rounds = picks.LastOrDefault()?.Round ?? 1;
        var pick = picks.LastOrDefault()?.Pick + 1 ?? 1;
        var otc = JsonTraversal.ObjectsNamed(root, "draftResults").Select(x => JsonTraversal.Value(x, "franchiseOnTheClock", "onTheClock")).FirstOrDefault(x => x is not null) ?? string.Empty;
        return new DraftState(rounds, pick, next, otc, null, false, picks, []);
    }

    public static IReadOnlyDictionary<string, decimal> ParseAdp(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "player")
            .Select(x => (Id: JsonTraversal.Value(x, "id", "player"), Adp: Decimal(x, "averagePick", "average_pick", "adp")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && x.Adp is not null)
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Adp!.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parses the personal MFL draft list while preserving its saved order.</summary>
    public static IReadOnlyList<string> ParseDraftQueue(string json)
    {
        var root = JsonNode.Parse(json);
        var ids = new List<string>();
        CollectDraftQueueIds(root, ids);
        return ids.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectDraftQueueIds(JsonNode? node, List<string> ids)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key.Equals("player", StringComparison.OrdinalIgnoreCase))
                    CollectPlayerIdValues(pair.Value, ids);
                else
                    CollectDraftQueueIds(pair.Value, ids);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) CollectDraftQueueIds(item, ids);
        }
    }

    private static void CollectPlayerIdValues(JsonNode? node, List<string> ids)
    {
        if (node is JsonValue value)
        {
            var id = value.ToString();
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) CollectPlayerIdValues(item, ids);
        }
        else if (node is JsonObject obj)
        {
            var id = JsonTraversal.Value(obj, "id", "player");
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            else foreach (var child in obj) CollectPlayerIdValues(child.Value, ids);
        }
    }

    public static IReadOnlyList<Transaction> ParseTransactions(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "transaction").Select(x => new Transaction(
            JsonTraversal.Value(x, "id", "timestamp") ?? Guid.NewGuid().ToString("N"),
            DateTimeOffset.TryParse(JsonTraversal.Value(x, "timestamp"), out var date) ? date : DateTimeOffset.UtcNow,
            JsonTraversal.Value(x, "type") ?? "Transaction",
            JsonTraversal.Value(x, "description") ?? JsonTraversal.Value(x, "transaction") ?? string.Empty,
            JsonTraversal.Value(x, "franchise", "franchise_id"))).ToArray();
    }

    public static IReadOnlyList<LineupSlot> ParseLineup(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "player").Select(x =>
        {
            var id = JsonTraversal.Value(x, "id", "player") ?? string.Empty;
            var status = JsonTraversal.Value(x, "status", "lineupStatus") ?? string.Empty;
            var starter = status.Contains("starter", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                JsonTraversal.Value(x, "isStarter", "is_starter") == "1";
            return new LineupSlot(JsonTraversal.Value(x, "position", "slot") ?? string.Empty, id, starter,
                status.Contains("tiebreak", StringComparison.OrdinalIgnoreCase));
        }).Where(x => x.PlayerId is not null && x.IsStarter).DistinctBy(x => x.PlayerId).ToArray();
    }

    public static IReadOnlyList<Matchup> ParseMatchups(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "matchup").Select((matchup, index) =>
        {
            var teams = JsonTraversal.ObjectsNamed(matchup, "franchise").Take(2).ToArray();
            if (teams.Length < 2) return null;
            var week = Int(matchup, "week") ?? Int(teams[0], "week") ?? 0;
            var home = teams[0]; var away = teams[1];
            return new Matchup(week,
                JsonTraversal.Value(home, "id", "franchise_id") ?? string.Empty,
                JsonTraversal.Value(home, "name") ?? JsonTraversal.Value(home, "id") ?? "Home",
                Decimal(home, "score", "result", "points"),
                JsonTraversal.Value(away, "id", "franchise_id") ?? string.Empty,
                JsonTraversal.Value(away, "name") ?? JsonTraversal.Value(away, "id") ?? "Away",
                Decimal(away, "score", "result", "points"),
                (JsonTraversal.Value(matchup, "status") ?? string.Empty).Contains("final", StringComparison.OrdinalIgnoreCase));
        }).OfType<Matchup>().ToArray();
    }

    public static IReadOnlyList<WaiverClaim> ParseWaivers(string json)
    {
        var root = JsonNode.Parse(json);
        var candidates = JsonTraversal.ObjectsNamed(root, "waiverRequest")
            .Concat(JsonTraversal.ObjectsNamed(root, "claim"))
            .Concat(JsonTraversal.ObjectsNamed(root, "pick"));
        return candidates.Select((x, index) =>
        {
            var encoded = JsonTraversal.Value(x, "pick", "value", "request")?.Split('_');
            return new WaiverClaim(
                JsonTraversal.Value(x, "id"),
                JsonTraversal.Value(x, "add", "add_player", "player") ?? encoded?.ElementAtOrDefault(0) ?? string.Empty,
                NullPlayer(JsonTraversal.Value(x, "drop", "drop_player") ?? encoded?.ElementAtOrDefault(encoded.Length > 2 ? 2 : 1)),
                Int(x, "priority", "round") ?? index + 1,
                Decimal(x, "bid", "amount") ?? (encoded?.Length > 2 && decimal.TryParse(encoded[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var bid) ? bid : null),
                (JsonTraversal.Value(x, "conditional") ?? "0") == "1");
        }).Where(x => x.AddPlayerId.Length > 0).ToArray();
    }

    public static IReadOnlyList<PendingTrade> ParseTrades(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "trade").Concat(JsonTraversal.ObjectsNamed(root, "tradeProposal"))
            .Select(x => new PendingTrade(
                JsonTraversal.Value(x, "id", "trade_id") ?? string.Empty,
                JsonTraversal.Value(x, "from", "franchise", "franchise_id") ?? string.Empty,
                JsonTraversal.Value(x, "to", "offeredTo", "offered_to") ?? string.Empty,
                ParseAssets(JsonTraversal.Value(x, "willGiveUp", "will_give_up", "offered")),
                ParseAssets(JsonTraversal.Value(x, "willReceive", "will_receive", "requested")),
                JsonTraversal.Value(x, "comments"),
                DateTimeOffset.TryParse(JsonTraversal.Value(x, "expires"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) ? expires : null))
            .Where(x => x.Id.Length > 0).DistinctBy(x => x.Id).ToArray();
    }

    private static IReadOnlyList<TradeAsset> ParseAssets(string? value) => string.IsNullOrWhiteSpace(value) ? [] :
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => new TradeAsset(TradeAssetKind.Player, x)).ToArray();

    private static string? NullPlayer(string? value) => value is null or "" or "0000" ? null : value;

    private static int? Int(JsonObject value, params string[] names) =>
        int.TryParse(JsonTraversal.Value(value, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static decimal? Decimal(JsonObject value, params string[] names) =>
        decimal.TryParse(JsonTraversal.Value(value, names), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
}
