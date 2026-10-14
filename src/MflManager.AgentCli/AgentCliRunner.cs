using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MflManager.AgentCli;

public sealed class AgentCliRunner(TextReader input, TextWriter output, TextWriter error,
    Func<string, string, CancellationToken, Task<string?>>? exchange = null)
{
    private const string BrokerUnavailable = "MFL Manager broker is unavailable or access was denied. Keep the app open and run the client as the same Windows user.";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args is ["--mcp"]) return await RunMcpAsync(cancellationToken);
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                await output.WriteLineAsync("""
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
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
            };
            var response = await ExchangeAsync(request.ToJsonString(), cancellationToken);
            var parsed = JsonNode.Parse(response) as JsonObject ?? throw new JsonException();
            await output.WriteLineAsync(parsed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return parsed["error"] is not null || parsed["result"]?["isError"]?.GetValue<bool>() == true ? 1 : 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or TimeoutException or IOException)
        {
            await error.WriteLineAsync(BrokerUnavailable);
            return 2;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            await error.WriteLineAsync("MFL Manager broker returned an invalid response.");
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            await error.WriteLineAsync("Invalid command or numeric argument. Run mfl-agent help.");
            return 1;
        }
    }

    private async Task<int> RunMcpAsync(CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            JsonObject request;
            try { request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
            catch (JsonException)
            {
                await WriteErrorAsync(null, -32700, "Invalid JSON-RPC request.");
                continue;
            }
            if (request["id"] is null) continue;
            try
            {
                var response = await ExchangeAsync(line, cancellationToken);
                if (JsonNode.Parse(response) is not JsonObject) throw new JsonException();
                await output.WriteLineAsync(response);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or TimeoutException or IOException)
            {
                await WriteErrorAsync(request["id"], -32000, BrokerUnavailable);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(request["id"], -32000, "MFL Manager broker returned an invalid response.");
            }
        }
        return 0;
    }

    private Task WriteErrorAsync(JsonNode? id, int code, string message) => output.WriteLineAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    }.ToJsonString());

    private async Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
    {
        var pipeName = Environment.GetEnvironmentVariable("MFL_AGENT_PIPE") ?? "mfl-manager-agent";
        if (exchange is not null)
            return await exchange(pipeName, request, cancellationToken) ?? throw new IOException();
        // One connection and one submission per request. Never retry a mutation.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken);
        return await reader.ReadLineAsync(cancellationToken) ?? throw new IOException();
    }
    private static (string Tool, JsonObject Arguments) Parse(string[] args)
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

    private static JsonObject League(string id) => new() { ["league_id"] = id };

}
