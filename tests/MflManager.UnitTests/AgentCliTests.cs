using System.Text.Json.Nodes;
using MflManager.AgentCli;

namespace MflManager.UnitTests;

public sealed class AgentCliTests
{
    [Theory]
    [InlineData("denied")]
    [InlineData("disconnected")]
    [InlineData("timeout")]
    [InlineData("eof")]
    [InlineData("invalid")]
    public async Task CliFailures_ReturnSanitizedErrorWithoutUnhandledExceptionOrRetry(string failure)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var calls = 0;
        var runner = new AgentCliRunner(new StringReader(""), output, error, (_, _, _) =>
        {
            calls++;
            return Fail(failure);
        });
        Assert.Equal(2, await runner.RunAsync(["leagues"], CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("sensitive-detail", error.ToString());
        Assert.Contains("MFL Manager broker", error.ToString());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("disconnected")]
    [InlineData("timeout")]
    [InlineData("eof")]
    [InlineData("invalid")]
    public async Task McpFailure_ReturnsCorrelatedErrorAndContinuesNextRequest(string failure)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var calls = 0;
        var input = new StringReader("{\"id\":1,\"method\":\"tools/list\"}\n{\"id\":2,\"method\":\"tools/list\"}\n");
        var runner = new AgentCliRunner(input, output, error, (_, _, _) =>
            ++calls == 1 ? Fail(failure) : Task.FromResult<string?>("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}"));
        Assert.Equal(0, await runner.RunAsync(["--mcp"], CancellationToken.None));
        var replies = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.Equal(2, replies.Length);
        Assert.Equal(1, replies[0]["id"]!.GetValue<int>());
        Assert.Equal(-32000, replies[0]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(replies[1]["result"]);
        Assert.Equal(2, calls);
        Assert.Equal("", error.ToString());
        Assert.DoesNotContain("sensitive-detail", output.ToString());
    }

    [Fact]
    public async Task MalformedMcpInput_ReturnsParseErrorAndIgnoresNotifications()
    {
        var output = new StringWriter();
        var runner = new AgentCliRunner(new StringReader("invalid\n[]\n{\"method\":\"notifications/initialized\"}\n"), output, new StringWriter(),
            (_, _, _) => throw new Exception("Notifications must not reach the broker."));
        Assert.Equal(0, await runner.RunAsync(["--mcp"], CancellationToken.None));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            var response = JsonNode.Parse(line)!;
            Assert.Null(response["id"]);
            Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
        });
    }

    [Theory]
    [InlineData("unknown", "")]
    [InlineData("select", "not-a-season")]
    [InlineData("select", "999999999999999999999")]
    public async Task InvalidCommand_ReturnsUsageError(string command, string season)
    {
        var error = new StringWriter();
        var runner = new AgentCliRunner(new StringReader(""), new StringWriter(), error,
            (_, _, _) => throw new Exception("Invalid commands must not connect."));
        Assert.Equal(1, await runner.RunAsync([command, "123", season], CancellationToken.None));
        Assert.Contains("mfl-agent help", error.ToString());
    }

    private static Task<string?> Fail(string failure) => failure switch
    {
        "denied" => throw new UnauthorizedAccessException("sensitive-detail"),
        "disconnected" => throw new IOException("sensitive-detail"),
        "timeout" => throw new TimeoutException("sensitive-detail"),
        "eof" => Task.FromResult<string?>(null),
        _ => Task.FromResult<string?>("sensitive-detail")
    };
}
