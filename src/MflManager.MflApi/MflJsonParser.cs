using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MflManager.Application;
using MflManager.Domain;

namespace MflManager.MflApi;

internal sealed record WeeklyPlayerScoreDiagnostics(
    int PlayerRecords,
    int NumericScores,
    int NonZeroScores,
    decimal TotalPoints,
    string PlayerFields);

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

    public static IReadOnlyDictionary<string, string> ParseInjuries(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is null || !JsonTraversal.ObjectsNamed(root, "injuries").Any())
            throw new JsonException("MFL did not return an injuries export.");
        return JsonTraversal.ObjectsNamed(root, "injury")
            .Concat(JsonTraversal.ObjectsNamed(root, "player"))
            .Select(x => (Id: JsonTraversal.Value(x, "id", "player", "player_id"),
                Status: JsonTraversal.Value(x, "status", "injury", "injury_status")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Status))
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Status!, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, int> ParseNflByeWeeks(string json, int season)
    {
        var root = JsonNode.Parse(json);
        var byes = JsonTraversal.ObjectsNamed(root, "nflByeWeeks").FirstOrDefault();
        if (byes is null || Int(byes, "year") != season)
            throw new JsonException("MFL did not return bye weeks for the selected season.");
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in JsonTraversal.ObjectsNamed(byes, "team"))
        {
            var id = JsonTraversal.Value(team, "id")?.Trim().ToUpperInvariant();
            var week = Int(team, "bye_week");
            if (string.IsNullOrWhiteSpace(id) || week is not > 0 or > 18 || !result.TryAdd(id, week.Value))
                throw new JsonException("MFL returned an invalid NFL bye-week entry.");
        }
        if (result.Count == 0) throw new JsonException("MFL did not return NFL bye-week entries.");
        return result;
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

    public static IReadOnlyList<Player> ParseFreeAgents(string json, IReadOnlyDictionary<string, Player> players)
    {
        var detailedPlayers = ParsePlayers(json).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        return JsonTraversal.ObjectsNamed(JsonNode.Parse(json), "player")
            .Select(x => JsonTraversal.Value(x, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => players.GetValueOrDefault(id!) ?? detailedPlayers.GetValueOrDefault(id!))
            .OfType<Player>().DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Calculates available league copies from MFL's rostersPerPlayer setting and
    /// the number of roster occurrences. The freeAgents response identifies which
    /// players have at least one copy available, but does not include a quantity.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ParseFreeAgentCopies(
        string freeAgentsJson, string rostersJson, int rostersPerPlayer)
    {
        if (rostersPerPlayer <= 0) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rosteredCopies = JsonTraversal.ObjectsNamed(JsonNode.Parse(rostersJson), "player")
            .Select(x => JsonTraversal.Value(x, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return JsonTraversal.ObjectsNamed(JsonNode.Parse(freeAgentsJson), "player")
            .Select(x => JsonTraversal.Value(x, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id!, id => Math.Max(0, rostersPerPlayer - rosteredCopies.GetValueOrDefault(id!, 0)),
                StringComparer.OrdinalIgnoreCase);
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
        var starters = JsonTraversal.ObjectsNamed(league, "starters").FirstOrDefault();
        var positionRules = starters is null ? [] : JsonTraversal.ObjectsNamed(starters, "position")
            .Select(position =>
            {
                var name = JsonTraversal.Value(position, "name", "position", "id") ?? string.Empty;
                var (minimum, maximum) = ParseRange(JsonTraversal.Value(position, "limit", "count", "value"),
                    Int(position, "minimum", "min"), Int(position, "maximum", "max"));
                return new LineupPositionRule(name, minimum, maximum);
            })
            .Where(x => x.Position.Length > 0 && x.Maximum > 0)
            .ToArray();
        return new LeagueSettings(
            JsonTraversal.Value(league, "name") ?? "League",
            Int(league, "startWeek", "start_week") ?? 1,
            Int(league, "endWeek", "end_week") ?? 18,
            Int(league, "rosterSize", "roster_size") ?? 0,
            Int(league, "injuredReserve", "ir_size") ?? 0,
            Int(league, "taxiSquad", "taxi_squad") ?? 0,
            values,
            starters is null ? 0 : Int(starters, "count") ?? 0,
            Int(league, "currentWeek", "current_week", "week") ?? 0,
            positionRules);
    }

    public static IReadOnlyList<RosterPlayer> ParseRoster(string json, IReadOnlyDictionary<string, Player> players, string? franchiseId = null)
    {
        var root = JsonNode.Parse(json);
        JsonNode? rosterRoot = root;
        if (!string.IsNullOrWhiteSpace(franchiseId))
        {
            rosterRoot = JsonTraversal.ObjectsNamed(root, "franchise")
                .FirstOrDefault(x => string.Equals(JsonTraversal.Value(x, "id", "franchise_id"), franchiseId, StringComparison.OrdinalIgnoreCase));
            if (rosterRoot is null) return [];
        }
        return JsonTraversal.ObjectsNamed(rosterRoot, "player").Select(x =>
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

    public static IReadOnlyDictionary<string, decimal> ParseWeeklyScores(string json)
    {
        var root = JsonNode.Parse(json);
        var weeklyResults = JsonTraversal.ObjectsNamed(root, "weeklyResults").FirstOrDefault();
        return JsonTraversal.ObjectsNamed(weeklyResults ?? root, "franchise")
            .Select(x => (Id: JsonTraversal.Value(x, "id", "franchise_id") ?? string.Empty,
                Score: Decimal(x, "score", "points") ?? 0m))
            .Where(x => x.Id.Length > 0)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Score, StringComparer.OrdinalIgnoreCase);
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
        return JsonTraversal.ObjectsNamed(root, "transaction").Select(x =>
        {
            var type = JsonTraversal.Value(x, "type") ?? "Transaction";
            var encoded = (JsonTraversal.Value(x, "transaction") ?? string.Empty).Split('|');
            var isBlindBid = type.Equals("BBID_WAIVER", StringComparison.OrdinalIgnoreCase);
            var isPickup = isBlindBid || type.Equals("FREE_AGENT", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("WAIVER", StringComparison.OrdinalIgnoreCase);
            static string[] PlayerIds(string? value) => (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => id != "0").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new Transaction(
            JsonTraversal.Value(x, "id", "timestamp") ?? Guid.NewGuid().ToString("N"),
            Timestamp(x, "timestamp") ?? DateTimeOffset.MinValue,
            type,
            JsonTraversal.Value(x, "description") ?? JsonTraversal.Value(x, "transaction") ?? string.Empty,
            JsonTraversal.Value(x, "franchise", "franchise_id"))
            {
                AcquiredPlayerIds = isPickup ? PlayerIds(encoded.ElementAtOrDefault(0)) : [],
                DroppedPlayerIds = isPickup ? PlayerIds(encoded.ElementAtOrDefault(isBlindBid ? 2 : 1)) : [],
                BidAmount = isBlindBid && decimal.TryParse(encoded.ElementAtOrDefault(1), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var bid) ? bid : null
            };
        }).ToArray();
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

    public static WeeklyLineup ParsePlayerRosterStatus(string json, int week, string? franchiseId)
    {
        var root = JsonNode.Parse(json);
        var starters = PlayerStatusNodes(root).Where(player =>
        {
            var statuses = JsonTraversal.ObjectsNamed(player, "roster_franchise");
            return statuses.Any(status =>
                (string.IsNullOrWhiteSpace(franchiseId) || string.Equals(JsonTraversal.Value(status, "id", "franchise", "franchise_id"), franchiseId, StringComparison.OrdinalIgnoreCase)) &&
                (JsonTraversal.Value(status, "status") ?? string.Empty) is var value &&
                (value.Equals("S", StringComparison.OrdinalIgnoreCase) || value.Equals("STARTER", StringComparison.OrdinalIgnoreCase)));
        }).Select(player => JsonTraversal.Value(player, "id", "player_id", "player"))
          .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>()
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new WeeklyLineup(week, starters, new Dictionary<string, decimal>());
    }

    public static WeeklyLineup ParseWeeklyResultsLineup(string json, int requestedWeek, string? franchiseId)
    {
        var root = JsonNode.Parse(json);
        var weeklyResults = JsonTraversal.ObjectsNamed(root, "weeklyResults").FirstOrDefault();
        // A requested week is not evidence of the week returned by MFL.
        var week = weeklyResults is null ? 0 : Int(weeklyResults, "week") ?? 0;
        var franchise = JsonTraversal.ObjectsNamed(weeklyResults ?? root, "franchise")
            .FirstOrDefault(x => string.IsNullOrWhiteSpace(franchiseId) ||
                string.Equals(JsonTraversal.Value(x, "id", "franchise_id"), franchiseId, StringComparison.OrdinalIgnoreCase));
        var starters = (franchise is null ? null : JsonTraversal.Value(franchise, "starters"))
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var points = franchise is null
            ? new Dictionary<string, decimal>()
            : JsonTraversal.ObjectsNamed(franchise, "player")
                .Select(x => (Id: JsonTraversal.Value(x, "id", "player_id", "player") ?? string.Empty,
                    Points: Decimal(x, "score", "points")))
                .Where(x => x.Id.Length > 0 && x.Points.HasValue)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Points!.Value, StringComparer.OrdinalIgnoreCase);
        return new WeeklyLineup(week, starters, points);
    }

    public static WeeklyLineup ParseLiveScoringLineup(string json, string? franchiseId)
    {
        var root = JsonNode.Parse(json);
        var live = JsonTraversal.ObjectsNamed(root, "liveScoring").FirstOrDefault();
        var week = live is null ? 0 : Int(live, "week") ?? 0;
        var franchise = JsonTraversal.ObjectsNamed(live, "franchise")
            .FirstOrDefault(x => string.Equals(JsonTraversal.Value(x, "id"), franchiseId, StringComparison.OrdinalIgnoreCase));
        if (franchise is null) throw new JsonException("MFL live scoring did not return the selected franchise.");
        var players = JsonTraversal.ObjectsNamed(franchise, "player").ToArray();
        var points = players.Select(x => (Id: JsonTraversal.Value(x, "id"), Points: Decimal(x, "score", "points")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && x.Points.HasValue)
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Points!.Value, StringComparer.OrdinalIgnoreCase);
        var starters = players.Where(x => string.Equals(JsonTraversal.Value(x, "status"), "starter", StringComparison.OrdinalIgnoreCase))
            .Select(x => JsonTraversal.Value(x, "id")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new WeeklyLineup(week, starters, points);
    }

    public static WeeklyTeamResult ParseWeeklyTeamResult(string json, int requestedWeek, string franchiseId)
    {
        var root = JsonNode.Parse(json);
        var weeklyResults = JsonTraversal.ObjectsNamed(root, "weeklyResults").FirstOrDefault();
        var week = weeklyResults is null ? requestedWeek : Int(weeklyResults, "week") ?? requestedWeek;
        var franchise = JsonTraversal.ObjectsNamed(weeklyResults ?? root, "franchise")
            .FirstOrDefault(x => string.Equals(
                JsonTraversal.Value(x, "id", "franchise_id"), franchiseId, StringComparison.OrdinalIgnoreCase));
        if (franchise is null) return new WeeklyTeamResult(week, [], new Dictionary<string, decimal>());

        var starters = (JsonTraversal.Value(franchise, "starters") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var points = JsonTraversal.ObjectsNamed(franchise, "player")
            .Select(x => (Id: JsonTraversal.Value(x, "id", "player_id", "player") ?? string.Empty,
                Points: Decimal(x, "score", "points")))
            .Where(x => x.Id.Length > 0 && x.Points.HasValue)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Points!.Value, StringComparer.OrdinalIgnoreCase);
        return new WeeklyTeamResult(week, starters, points);
    }

    internal static WeeklyPlayerScoreDiagnostics InspectWeeklyPlayerScores(string json, string? franchiseId)
    {
        var root = JsonNode.Parse(json);
        var weeklyResults = JsonTraversal.ObjectsNamed(root, "weeklyResults").FirstOrDefault();
        var franchise = JsonTraversal.ObjectsNamed(weeklyResults ?? root, "franchise")
            .FirstOrDefault(x => string.IsNullOrWhiteSpace(franchiseId) ||
                string.Equals(JsonTraversal.Value(x, "id", "franchise_id"), franchiseId, StringComparison.OrdinalIgnoreCase));
        if (franchise is null) return new(0, 0, 0, 0m, string.Empty);

        var players = JsonTraversal.ObjectsNamed(franchise, "player").ToArray();
        var scores = players.Select(x => Decimal(x, "score", "points")).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var fields = string.Join(',', players.SelectMany(x => x.Select(field => field.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return new(players.Length, scores.Length, scores.Count(x => x != 0m), scores.Sum(), fields);
    }

    public static MflWeekStatus ParseMflWeekStatus(string json)
    {
        var root = JsonNode.Parse(json);
        var weeks = JsonTraversal.ObjectsNamed(root, "weeks").FirstOrDefault();
        return weeks is null
            ? new MflWeekStatus(0, 0, 0, 0, 0)
            : new MflWeekStatus(
                Int(weeks, "LineupWeek", "lineupWeek") ?? 0,
                Int(weeks, "CurrentWeek", "currentWeek") ?? 0,
                Int(weeks, "UpcomingWeek", "upcomingWeek") ?? 0,
                Int(weeks, "CompletedWeek", "completedWeek") ?? 0,
                Int(weeks, "LiveScoringWeek", "liveScoringWeek") ?? 0);
    }

    public static IReadOnlyList<PlayerRosterAvailability> ParsePlayerAvailability(string json, string? franchiseId)
    {
        var root = JsonNode.Parse(json);
        return PlayerStatusNodes(root).Select(player =>
        {
            var id = JsonTraversal.Value(player, "id", "player_id", "player") ?? string.Empty;
            var franchiseStatuses = JsonTraversal.ObjectsNamed(player, "roster_franchise")
                .Where(status => string.IsNullOrWhiteSpace(franchiseId) ||
                    string.Equals(JsonTraversal.Value(status, "id", "franchise", "franchise_id"), franchiseId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new PlayerRosterAvailability(
                id,
                Flag(player, "is_fa", "isFa", "free_agent", "freeAgent"),
                Flag(player, "cant_add", "cantAdd") || franchiseStatuses.Any(x => Flag(x, "cant_add", "cantAdd")),
                Flag(player, "locked", "is_locked", "isLocked") || franchiseStatuses.Any(x => Flag(x, "locked", "is_locked", "isLocked")));
        }).Where(x => x.PlayerId.Length > 0)
          .GroupBy(x => x.PlayerId, StringComparer.OrdinalIgnoreCase)
          .Select(x => new PlayerRosterAvailability(x.Key, x.Any(y => y.IsFreeAgent), x.Any(y => y.CannotAdd), x.Any(y => y.IsLocked)))
          .ToArray();
    }

    private static IEnumerable<JsonObject> PlayerStatusNodes(JsonNode? root) =>
        JsonTraversal.ObjectsNamed(root, "player")
            .Concat(JsonTraversal.ObjectsNamed(root, "playerRosterStatus"))
            .Concat(JsonTraversal.ObjectsNamed(root, "playerStatus"))
            .Where(x => x.Any(pair => pair.Value is JsonValue &&
                (pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("player_id", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("player", StringComparison.OrdinalIgnoreCase))));

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
            .Concat(JsonTraversal.ObjectsNamed(root, "blindBidWaiverRequest"))
            .Concat(JsonTraversal.ObjectsNamed(root, "blindBidWaiver"))
            .Concat(JsonTraversal.ObjectsNamed(root, "bid"))
            .Concat(JsonTraversal.ObjectsNamed(root, "claim"))
            .Concat(JsonTraversal.ObjectsNamed(root, "pick"));
        var encodedClaims = JsonTraversal.ObjectsNamed(root, "blindBidWaiverRequest")
            .Concat(JsonTraversal.ObjectsNamed(root, "waiverRequest"))
            .SelectMany(x =>
            {
                var round = Int(x, "round") ?? 1;
                return (JsonTraversal.Value(x, "addsDrops", "picks") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select((pick, index) =>
                {
                    var fields = pick.Split('_');
                    return new WaiverClaim(null, fields[0], NullPlayer(fields.ElementAtOrDefault(fields.Length > 2 ? 2 : 1)), index + 1,
                        fields.Length > 2 && decimal.TryParse(fields[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var bid) ? bid : null,
                        Round: round);
                });
            });
        return candidates.Where(x => !x.Any(p => p.Key.Equals("pick", StringComparison.OrdinalIgnoreCase) && p.Value is not JsonValue)).Select((x, index) =>
        {
            var encoded = JsonTraversal.Value(x, "pick", "value", "request")?.Split('_');
            return new WaiverClaim(
                JsonTraversal.Value(x, "id"),
                JsonTraversal.Value(x, "add", "add_player", "player", "player_id") ?? encoded?.ElementAtOrDefault(0) ?? string.Empty,
                NullPlayer(JsonTraversal.Value(x, "drop", "drop_player") ?? encoded?.ElementAtOrDefault(encoded.Length > 2 ? 2 : 1)),
                Int(x, "priority") ?? index + 1,
                Decimal(x, "bid", "amount", "bidAmount") ?? (encoded?.Length > 2 && decimal.TryParse(encoded[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var bid) ? bid : null),
                (JsonTraversal.Value(x, "conditional") ?? "0") == "1",
                Int(x, "round") ?? 1);
        }).Concat(encodedClaims).Where(x => x.AddPlayerId.Length > 0).Distinct().ToArray();
    }

    public static DateTimeOffset? ParseNextWaiverProcessingAt(string json, DateTimeOffset now)
    {
        var root = JsonNode.Parse(json);
        var events = JsonTraversal.ObjectsNamed(root, "event")
            .Concat(JsonTraversal.ObjectsNamed(root, "calendarEvent"))
            .Distinct()
            .Select(value => new
            {
                Value = value,
                Description = string.Join(' ', new[]
                {
                    JsonTraversal.Value(value, "name", "title", "description", "event", "eventName"),
                    JsonTraversal.Value(value, "type", "eventType", "event_type", "action")
                }.Where(text => !string.IsNullOrWhiteSpace(text)))
            })
            .Where(candidate => candidate.Description.Contains("waiver", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new
            {
                candidate.Description,
                ProcessingAt = CalendarTime(candidate.Value, now,
                    candidate.Description.Contains("process", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Description.Contains("run", StringComparison.OrdinalIgnoreCase))
            })
            .Where(candidate => candidate.ProcessingAt > now &&
                !candidate.Description.Contains("lock", StringComparison.OrdinalIgnoreCase) &&
                !candidate.Description.Contains("first come", StringComparison.OrdinalIgnoreCase) &&
                !candidate.Description.Contains("fcfs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.ProcessingAt)
            .ToArray();

        return events.FirstOrDefault(candidate =>
                   candidate.Description.Contains("process", StringComparison.OrdinalIgnoreCase) ||
                   candidate.Description.Contains("run", StringComparison.OrdinalIgnoreCase))?.ProcessingAt
               ?? events.FirstOrDefault()?.ProcessingAt;
    }

    public static decimal? ParseFaabBalance(string json, string? franchiseId)
    {
        if (string.IsNullOrWhiteSpace(franchiseId)) return null;
        var root = JsonNode.Parse(json);
        var franchise = JsonTraversal.ObjectsNamed(root, "franchise")
            .FirstOrDefault(value => JsonTraversal.Value(value, "id") == franchiseId);
        return franchise is null ? null : Decimal(franchise, "bbidAvailableBalance");
    }

    public static IReadOnlyDictionary<string, decimal> ParseSeasonPlayerPoints(string json)
    {
        var root = JsonNode.Parse(json);
        return JsonTraversal.ObjectsNamed(root, "playerScore").Concat(JsonTraversal.ObjectsNamed(root, "player"))
            .Select(value => (Id: JsonTraversal.Value(value, "id"), Points: Decimal(value, "score", "points")))
            .Where(value => !string.IsNullOrWhiteSpace(value.Id) && value.Points is not null)
            .GroupBy(value => value.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Points!.Value, StringComparer.OrdinalIgnoreCase);
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

    private static (int Minimum, int Maximum) ParseRange(string? value, int? explicitMinimum, int? explicitMaximum)
    {
        if (explicitMinimum is not null || explicitMaximum is not null)
        {
            var minimum = explicitMinimum ?? explicitMaximum ?? 0;
            return (minimum, Math.Max(minimum, explicitMaximum ?? minimum));
        }
        var parts = (value ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)) return (0, 0);
        var last = parts.Length > 1 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : first;
        return (Math.Min(first, last), Math.Max(first, last));
    }

    private static int? Int(JsonObject value, params string[] names) =>
        int.TryParse(JsonTraversal.Value(value, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static decimal? Decimal(JsonObject value, params string[] names) =>
        decimal.TryParse(JsonTraversal.Value(value, names), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static DateTimeOffset? CalendarTime(JsonObject value, DateTimeOffset now, bool preferStart)
    {
        var start = Timestamp(value, "startTime", "start_time", "start", "timestamp", "time", "date");
        var end = Timestamp(value, "endTime", "end_time", "end");
        // MFL uses the start/end pair as season-long bounds for some recurring events.
        // The season end is not the next processing occurrence; do not present it as one.
        if (start is { } first && end is { } last && last - first > TimeSpan.FromDays(2))
        {
            var frequency = JsonTraversal.Value(value, "happens", "frequency", "recurrence")?.Trim();
            var interval = frequency?.ToLowerInvariant() switch
            {
                "daily" or "every day" => TimeSpan.FromDays(1),
                "weekly" or "every week" => TimeSpan.FromDays(7),
                _ => TimeSpan.Zero
            };
            if (interval == TimeSpan.Zero) return null;
            var count = Math.Max(0, Math.Ceiling((now - first).TotalSeconds / interval.TotalSeconds));
            var occurrence = first.AddSeconds(count * interval.TotalSeconds);
            return occurrence <= last ? occurrence : null;
        }
        return preferStart ? start ?? end : end ?? start;
    }

    private static DateTimeOffset? Timestamp(JsonObject value, params string[] names)
    {
        var raw = JsonTraversal.Value(value, names);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try { return unix > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    private static bool Flag(JsonObject value, params string[] names)
    {
        var raw = JsonTraversal.Value(value, names);
        return raw is not null && (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("y", StringComparison.OrdinalIgnoreCase));
    }
}
