using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var exitCode = await RunAsync(args, CancellationToken.None);
return exitCode;

static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            mfl-agent leagues
            mfl-agent select <league-id> [season] [host]
            mfl-agent roster <league-id>
            mfl-agent lineup <league-id>
            mfl-agent players search <league-id> <query>
            mfl-agent draft state <league-id>
            mfl-agent draft queue <league-id> <player-id>...
            mfl-agent draft pick <league-id> <player-id> <round> <pick> [--confirm]
            mfl-agent refresh <league-id>
            """);
        return 0;
    }

    var (tool, arguments) = Parse(args);
    var pipeName = Environment.GetEnvironmentVariable("MFL_AGENT_PIPE") ?? "mfl-manager-agent";
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
    try { await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false); }
    catch (TimeoutException) { Console.Error.WriteLine("MFL Manager agent broker is not running."); return 2; }
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    var request = new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
    };
    await writer.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
    var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    Console.WriteLine(JsonNode.Parse(response ?? "{}")?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return response?.Contains("\"error\"", StringComparison.Ordinal) == true ? 1 : 0;
}

static (string Tool, JsonObject Arguments) Parse(string[] args)
{
    var confirm = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);
    return args switch
    {
        ["leagues", ..] => ("mfl_list_leagues", new()),
        ["select", var league, var season, var host, ..] => ("mfl_select_league", new() { ["league_id"] = league, ["season"] = int.Parse(season, System.Globalization.CultureInfo.InvariantCulture), ["host"] = host }),
        ["select", var league, var season, ..] => ("mfl_select_league", new() { ["league_id"] = league, ["season"] = int.Parse(season, System.Globalization.CultureInfo.InvariantCulture) }),
        ["select", var league, ..] => ("mfl_select_league", new() { ["league_id"] = league }),
        ["roster", var league, ..] => ("mfl_get_roster", League(league)),
        ["lineup", var league, ..] => ("mfl_get_lineup", League(league)),
        ["players", "search", var league, ..] => ("mfl_search_players", new() { ["league_id"] = league, ["query"] = string.Join(' ', args.Skip(3)) }),
        ["draft", "state", var league, ..] => ("mfl_get_draft_state", League(league)),
        ["draft", "queue", var league, ..] => ("mfl_set_draft_queue", new() { ["league_id"] = league, ["player_ids"] = new JsonArray(args.Skip(3).Where(x => x != "--confirm").Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["preview"] = !confirm, ["confirmed"] = confirm }),
        ["draft", "pick", var league, var player, var round, var pick, ..] => ("mfl_submit_draft_pick", new() { ["league_id"] = league, ["player_id"] = player, ["round"] = int.Parse(round, System.Globalization.CultureInfo.InvariantCulture), ["pick"] = int.Parse(pick, System.Globalization.CultureInfo.InvariantCulture), ["preview"] = !confirm, ["confirmed"] = confirm }),
        ["refresh", var league, ..] => ("mfl_refresh_all", League(league)),
        _ => throw new ArgumentException("Unknown command. Run mfl-agent help.")
    };
}

static JsonObject League(string id) => new() { ["league_id"] = id };
