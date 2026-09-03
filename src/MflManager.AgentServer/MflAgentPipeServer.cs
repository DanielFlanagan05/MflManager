using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MflManager.AgentServer;

public sealed class MflAgentPipeServer(string pipeName, MflToolRegistry tools) : IAsyncDisposable
{
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    public string PipeName { get; } = pipeName;
    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                var response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Request must be an object.");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>();
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "mfl-manager", ["version"] = "1.0.0" }
                },
                "notifications/initialized" => new JsonObject(),
                "tools/list" => tools.ListTools(),
                "tools/call" => await tools.CallAsync(
                    request["params"]?["name"]?.GetValue<string>() ?? string.Empty,
                    request["params"]?["arguments"] as JsonObject ?? new JsonObject(), cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unknown method: " + method)
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32602, ["message"] = ex.Message }
            }.ToJsonString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_runTask is not null) try { await _runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime?.Dispose();
    }
}
