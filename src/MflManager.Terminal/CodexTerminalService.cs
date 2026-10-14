using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MflManager.Terminal;

public enum TerminalConnectionState { Stopped, Starting, Connected, Working, Faulted }

public sealed record TerminalOutput(string Text, bool IsError, DateTimeOffset Timestamp);
public sealed record CodexCliStatus(
    bool IsInstalled,
    bool IsAuthenticated,
    string? Version,
    string? ExecutablePath,
    string? AccountEmail,
    string? AuthenticationType);

public interface ICodexTerminalService : IAsyncDisposable
{
    TerminalConnectionState State { get; }
    event EventHandler<TerminalOutput>? OutputReceived;
    event EventHandler<TerminalConnectionState>? StateChanged;
    Task<CodexCliStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task SignInAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
    Task StartAsync(string repositoryRoot, string? agentServerPipe, CancellationToken cancellationToken);
    Task WriteAsync(string text, CancellationToken cancellationToken);
    Task ClearConversationAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class CodexTerminalService : ICodexTerminalService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _executablePath;
    private string? _repositoryRoot;
    private string? _agentServerPipe;
    private string? _threadId;
    private string? _pendingMessage;
    private volatile bool _stopRequested;
    private CancellationTokenSource? _activeRequest;
    public TerminalConnectionState State { get; private set; }
    public event EventHandler<TerminalOutput>? OutputReceived;
    public event EventHandler<TerminalConnectionState>? StateChanged;

    public async Task<CodexCliStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null) return new(false, false, null, null, null, null);

        var version = await RunProbeAsync(executable, ["--version"], cancellationToken).ConfigureAwait(false);
        var account = await ReadAccountAsync(executable, cancellationToken).ConfigureAwait(false);
        if (account.Succeeded)
            return new(true, account.IsAuthenticated, version.Output.Trim(), executable, account.Email, account.Type);

        var authentication = await RunProbeAsync(executable, ["login", "status"], cancellationToken).ConfigureAwait(false);
        return new(true, authentication.ExitCode == 0, version.Output.Trim(), executable, null, null);
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new Win32Exception("Codex CLI is not installed.");
        var info = new ProcessStartInfo { FileName = executable, UseShellExecute = true };
        info.ArgumentList.Add("login");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Codex sign-in did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Codex sign-in was not completed.");
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new Win32Exception("Codex CLI is not installed.");
        var result = await RunProbeAsync(executable, ["logout"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("Codex sign-out was not completed.");
    }

    public async Task StartAsync(string repositoryRoot, string? agentServerPipe, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is TerminalConnectionState.Connected or TerminalConnectionState.Working) return;
            SetState(TerminalConnectionState.Starting);
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.IsInstalled) throw new Win32Exception("Codex CLI is not installed.");
            if (!status.IsAuthenticated) throw new InvalidOperationException("Codex CLI is not signed in.");

            _executablePath = status.ExecutablePath;
            _repositoryRoot = repositoryRoot;
            _agentServerPipe = agentServerPipe;
            _threadId = null;
            _stopRequested = false;
            SetState(TerminalConnectionState.Connected);
        }
        catch
        {
            SetState(TerminalConnectionState.Faulted);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Luna is already responding. Wait or stop the current request.");
        try
        {
            if (_stopRequested || _executablePath is null || _repositoryRoot is null || State == TerminalConnectionState.Stopped)
                throw new InvalidOperationException("Codex agent is not connected.");

            SetState(TerminalConnectionState.Working);
            _pendingMessage = null;
            _activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = _activeRequest.Token;
            var startInfo = CreateExecutionStartInfo(text);
            using var process = new Process { StartInfo = startInfo };
            _process = process;
            if (!process.Start()) throw new InvalidOperationException("Codex did not start.");
            process.StandardInput.Close();
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
            });

            var standardError = ReadErrorsAsync(process, cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                PublishJsonEvent(line);
            await standardError.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopRequested)
            {
                SetState(TerminalConnectionState.Stopped);
                return;
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("Codex ended before completing the request.");
            Publish(_pendingMessage, false);
            _pendingMessage = null;
            SetState(TerminalConnectionState.Connected);
        }
        catch (OperationCanceledException)
        {
            _threadId = null;
            SetState(_stopRequested ? TerminalConnectionState.Stopped : TerminalConnectionState.Connected);
            throw;
        }
        catch
        {
            SetState(TerminalConnectionState.Faulted);
            throw;
        }
        finally
        {
            _process = null;
            _activeRequest?.Dispose();
            _activeRequest = null;
            _gate.Release();
        }
    }

    public async Task ClearConversationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _threadId = null;
            _pendingMessage = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;
        try { _activeRequest?.Cancel(); }
        catch (ObjectDisposedException) { }
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _threadId = null;
            _executablePath = null;
            _repositoryRoot = null;
            SetState(TerminalConnectionState.Stopped);
        }
        finally { _gate.Release(); }
    }

    private ProcessStartInfo CreateExecutionStartInfo(string prompt)
    {
        var info = new ProcessStartInfo
        {
            FileName = _executablePath!, WorkingDirectory = _repositoryRoot!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--ignore-user-config");
        AddConfig(info, "web_search=\"live\"");
        AddConfig(info, "developer_instructions=" + JsonSerializer.Serialize(LunaPrompt.Instructions));
        var bridge = Path.Combine(AppContext.BaseDirectory, "MflManager.AgentCli.dll");
        if (!File.Exists(bridge))
            throw new InvalidOperationException("The MFL agent tool bridge is missing. Rebuild or reinstall MFL Manager.");
        var bridgeExecutable = Path.ChangeExtension(bridge, ".exe");
        var hasAppHost = File.Exists(bridgeExecutable);
        AddConfig(info, "mcp_servers.mfl.command=" + JsonSerializer.Serialize(hasAppHost ? bridgeExecutable : "dotnet"));
        AddConfig(info, "mcp_servers.mfl.args=" + JsonSerializer.Serialize(hasAppHost ? new[] { "--mcp" } : new[] { bridge, "--mcp" }));
        AddConfig(info, "mcp_servers.mfl.env.MFL_AGENT_PIPE=" + JsonSerializer.Serialize(_agentServerPipe ?? "mfl-manager-agent"));
        AddConfig(info, "mcp_servers.mfl.startup_timeout_sec=15");
        // Only allow read tools without an interactive approval UI. Mutations retain
        // the broker's preview/confirmation path and CLI approval requirements.
        AddConfig(info, "mcp_servers.mfl.default_tools_approval_mode=\"auto\"");
        foreach (var tool in new[] { "mfl_list_leagues", "mfl_refresh_all", "mfl_get_dashboard", "mfl_get_league_settings",
                     "mfl_get_standings", "mfl_get_matchups", "mfl_get_roster", "mfl_get_lineup",
                     "mfl_search_players", "mfl_get_free_agents", "mfl_get_draft_state",
                     "mfl_get_pending_trades", "mfl_get_capabilities", "mfl_get_rankings", "mfl_get_team_needs" })
            AddConfig(info, $"mcp_servers.mfl.tools.{tool}.approval_mode=\"approve\"");
        if (_threadId is not null)
        {
            info.ArgumentList.Add("resume");
            info.ArgumentList.Add(_threadId);
        }
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--model");
        info.ArgumentList.Add("gpt-5.6-luna");
        AddConfig(info, "model_reasoning_effort=\"max\"");
        if (_threadId is null)
        {
            info.ArgumentList.Add("--sandbox");
            info.ArgumentList.Add("read-only");
            info.ArgumentList.Add("--cd");
            info.ArgumentList.Add(_repositoryRoot!);
        }
        info.ArgumentList.Add(prompt);
        if (!string.IsNullOrWhiteSpace(_agentServerPipe)) info.Environment["MFL_AGENT_PIPE"] = _agentServerPipe;
        return info;
    }

    private static void AddConfig(ProcessStartInfo info, string value)
    {
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(value);
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                Publish(line + Environment.NewLine, true);
    }

    private void PublishJsonEvent(string line)
    {
        if (_activeRequest?.IsCancellationRequested == true) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type == "thread.started" && root.TryGetProperty("thread_id", out var thread))
                _threadId = thread.GetString();
            else if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                     item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message" &&
                     item.TryGetProperty("text", out var text))
                _pendingMessage = text.GetString() + Environment.NewLine;
            else if (type == "turn.completed")
            {
                // Agent messages can include routine tool-use preambles. Show only
                // the completed answer, keeping this advisory chat concise.
                Publish(_pendingMessage, false);
                _pendingMessage = null;
            }
            else if (type == "error" && root.TryGetProperty("message", out var error))
                Publish(error.GetString() + Environment.NewLine, true);
        }
        catch (JsonException) { Publish(line + Environment.NewLine, false); }
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("MFL_MANAGER_CODEX");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var standalone = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(standalone)) return standalone;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var desktopBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        return Directory.Exists(desktopBin)
            ? Directory.EnumerateFiles(desktopBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    private static async Task<(int ExitCode, string Output)> RunProbeAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Codex status check did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    private static async Task<(bool Succeeded, bool IsAuthenticated, string? Email, string? Type)> ReadAccountAsync(
        string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Codex account check did not start.");
        var errorDrain = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await WriteJsonLineAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new { clientInfo = new { name = "mfl_manager", title = "MFL Manager", version = "1.0.0" } }
            }, timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } initializeLine)
            {
                using var initialize = JsonDocument.Parse(initializeLine);
                if (initialize.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 0) break;
            }

            await WriteJsonLineAsync(process, new { method = "initialized", @params = new { } }, timeout.Token).ConfigureAwait(false);
            await WriteJsonLineAsync(process, new
            {
                method = "account/read",
                id = 1,
                @params = new { refreshToken = false }
            }, timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
                if (!root.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
                    return (true, false, null, null);

                var type = account.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                var email = account.TryGetProperty("email", out var emailValue) && emailValue.ValueKind == JsonValueKind.String
                    ? emailValue.GetString()
                    : null;
                return (true, true, email, type);
            }
            return (false, false, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, false, null, null);
        }
        catch (JsonException)
        {
            return (false, false, null, null);
        }
        catch (IOException)
        {
            return (false, false, null, null);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); }
                catch (InvalidOperationException) { }
            }
            try { await errorDrain.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task WriteJsonLineAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Publish(string? text, bool error)
    {
        if (!string.IsNullOrEmpty(text)) OutputReceived?.Invoke(this, new TerminalOutput(text, error, DateTimeOffset.UtcNow));
    }

    private void SetState(TerminalConnectionState state) { State = state; StateChanged?.Invoke(this, state); }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None).ConfigureAwait(false); _gate.Dispose(); }
}
