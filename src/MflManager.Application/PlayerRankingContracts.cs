using MflManager.Domain;

namespace MflManager.Application;

public sealed record RankingLoadResult(IReadOnlyList<PlayerRanking> Rankings, bool IsStale, string? Message = null);
public interface IPlayerRankingClient
{
    Task<RankingLoadResult> GetRankingsAsync(SeasonContext season, CancellationToken cancellationToken);
}
public interface IPlayerRankingCache
{
    Task<RankingLoadResult> GetAsync(string position, Uri source, Func<string, CancellationToken, Task> extract, CancellationToken cancellationToken);
}
public sealed record PlayerRankingOptions(string CacheDirectory, string ScriptsDirectory, string NodeExecutable = "node");
