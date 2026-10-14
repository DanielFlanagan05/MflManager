using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MflManager.AgentServer;

namespace MflManager.UnitTests;

public class AgentPipeTests
{
    [Fact]
    public async Task BrokerSurvivesRepeatedMcpClientDisconnects()
    {
        var pipeName = "mfl-test-" + Guid.NewGuid().ToString("N");
        await using var server = new MflAgentPipeServer(pipeName, new MflToolRegistry(null!, null!, null!, null!));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(timeout.Token);
        for (var i = 0; i < 4; i++)
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
            using var response = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var tools = response.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "mfl_get_rankings" &&
                tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(server.IsRunning);
        }
    }
}
