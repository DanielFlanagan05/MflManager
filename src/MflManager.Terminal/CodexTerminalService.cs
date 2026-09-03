using System.Diagnostics;
using System.Text;

namespace MflManager.Terminal;

public enum TerminalConnectionState { Stopped, Starting, Connected, Faulted }

public sealed record TerminalOutput(string Text, bool IsError, DateTimeOffset Timestamp);

public interface ICodexTerminalService : IAsyncDisposable
{
    TerminalConnectionState State { get; }
    event EventHandler<TerminalOutput>? OutputReceived;
    event EventHandler<TerminalConnectionState>? StateChanged;
    Task StartAsync(string repositoryRoot, string? agentServerPipe, CancellationToken cancellationToken);
    Task WriteAsync(string text, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class CodexTerminalService : ICodexTerminalService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    public TerminalConnectionState State { get; private set; }
    public event EventHandler<TerminalOutput>? OutputReceived;
    public event EventHandler<TerminalConnectionState>? StateChanged;

    public async Task StartAsync(string repositoryRoot, string? agentServerPipe, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false }) return;
            SetState(TerminalConnectionState.Starting);
            var startInfo = new ProcessStartInfo
            {
                FileName = "codex",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("gpt-5.6-luna");
            startInfo.ArgumentList.Add("--cd");
            startInfo.ArgumentList.Add(repositoryRoot);
            if (!string.IsNullOrWhiteSpace(agentServerPipe)) startInfo.Environment["MFL_AGENT_PIPE"] = agentServerPipe;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Publish(e.Data, false);
            process.ErrorDataReceived += (_, e) => Publish(e.Data, true);
            process.Exited += (_, _) => SetState(process.ExitCode == 0 ? TerminalConnectionState.Stopped : TerminalConnectionState.Faulted);
            if (!process.Start()) { SetState(TerminalConnectionState.Faulted); throw new InvalidOperationException("Codex did not start."); }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
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
        var process = _process;
        if (process is null || process.HasExited) throw new InvalidOperationException("Codex terminal is not connected.");
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process;
            _process = null;
            if (process is not null && !process.HasExited)
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            }
            process?.Dispose();
            SetState(TerminalConnectionState.Stopped);
        }
        finally { _gate.Release(); }
    }

    private void Publish(string? text, bool error)
    {
        if (text is not null) OutputReceived?.Invoke(this, new TerminalOutput(text + Environment.NewLine, error, DateTimeOffset.UtcNow));
    }

    private void SetState(TerminalConnectionState state) { State = state; StateChanged?.Invoke(this, state); }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None).ConfigureAwait(false); _gate.Dispose(); }
}
