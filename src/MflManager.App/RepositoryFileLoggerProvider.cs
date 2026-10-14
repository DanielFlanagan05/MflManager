using Microsoft.Extensions.Logging;

namespace MflManager.App;

internal sealed class RepositoryFileLoggerProvider : ILoggerProvider
{
    private const long MaximumDailyLogBytes = 512 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _logDirectory;
    private readonly object _gate = new();
    private DateTime _lastCleanupDate = DateTime.UtcNow.Date;

    private RepositoryFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
        DeleteExpiredLogs();
    }

    public static RepositoryFileLoggerProvider? TryCreate()
    {
        var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        try { return repositoryRoot is null ? null : new RepositoryFileLoggerProvider(Path.Combine(repositoryRoot, "logs")); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public ILogger CreateLogger(string categoryName) => new RepositoryFileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, EventId eventId, string message)
    {
        if (!category.StartsWith("MflManager", StringComparison.Ordinal) || level < LogLevel.Information) return;

        var now = DateTimeOffset.Now;
        var path = Path.Combine(_logDirectory, $"mfl-manager-{now:yyyy-MM-dd}.log");
        var line = $"{now:O}\t{level}\t{category}\t{eventId.Id}\t{SingleLine(message)}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                if (_lastCleanupDate != DateTime.UtcNow.Date)
                {
                    DeleteExpiredLogs();
                    _lastCleanupDate = DateTime.UtcNow.Date;
                }
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumDailyLogBytes) return;
                File.AppendAllText(path, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void DeleteExpiredLogs()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var path in Directory.EnumerateFiles(_logDirectory, "mfl-manager-*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MflManager.slnx"))) return directory.FullName;
        return null;
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed class RepositoryFileLogger(RepositoryFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            category.StartsWith("MflManager", StringComparison.Ordinal) && logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += $" ExceptionType={exception.GetType().Name}";
            provider.Write(category, logLevel, eventId, message);
        }
    }
}
