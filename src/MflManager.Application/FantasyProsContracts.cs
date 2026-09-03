using MflManager.Domain;

namespace MflManager.Application;

public interface IFantasyProsRankingClient
{
    Task<MflResult<IReadOnlyList<FantasyProsRanking>>> GetConsensusRankingsAsync(SeasonContext season, string position, CancellationToken cancellationToken);
}
