using System.Net;
using MflManager.Domain;

namespace MflManager.Application;

public sealed record MflResult<T>(
    bool IsSuccess,
    T? Value,
    string? MflErrorCode,
    string? UserMessage,
    HttpStatusCode? StatusCode,
    string CorrelationId)
{
    public static MflResult<T> Success(T value, string correlationId, HttpStatusCode status = HttpStatusCode.OK) =>
        new(true, value, null, null, status, correlationId);

    public static MflResult<T> Failure(string message, string correlationId, string? code = null, HttpStatusCode? status = null) =>
        new(false, default, code, message, status, correlationId);
}

public sealed record MflCredentials(string Username, string Password, bool Remember);

public sealed record AuthenticatedSession(string Username, string UserId, DateTimeOffset AuthenticatedAt);

public interface IMflAuthenticationClient
{
    Task<MflResult<AuthenticatedSession>> LoginAsync(SeasonContext season, MflCredentials credentials, CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public interface IMflLeagueDiscoveryClient
{
    Task<MflResult<IReadOnlyList<LeagueSummary>>> GetMyLeaguesAsync(SeasonContext season, CancellationToken cancellationToken);
}

public interface IMflExportClient
{
    Task<MflResult<string>> ExportAsync(LeagueContext league, string requestType, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken);
}

public sealed record WaiverSupplementalData(decimal? FaabBalance, bool BalanceLoaded,
    IReadOnlyDictionary<string, decimal> SeasonPlayerPoints, bool PointsLoaded);

public interface IMflWaiverDataService
{
    Task<WaiverSupplementalData> GetAsync(LeagueContext league, CancellationToken cancellationToken);
}

public interface IMflImportClient
{
    Task<MflResult<string>> ImportAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}

public interface IMflDraftClient
{
    Task<MflResult<DraftState>> GetDraftStateAsync(LeagueContext league, CancellationToken cancellationToken);
    Task<MflResult<string>> SubmitAsync(LeagueContext league, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}

public interface IMflCapabilityService
{
    CapabilitySet GetCapabilities(LeagueSnapshot? snapshot, bool isCommissioner);
}

public interface ILeagueSnapshotStore
{
    Task<LeagueSnapshot?> GetAsync(LeagueContext league, CancellationToken cancellationToken);
    Task SaveAsync(LeagueSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IOperationJournal
{
    Task AppendAsync(OperationJournalEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperationJournalEntry>> GetRecentAsync(string leagueId, int count, CancellationToken cancellationToken);
}

public interface ISecureSessionStore
{
    Task SaveAsync(string username, string userId, CancellationToken cancellationToken);
    Task<(string Username, string UserId)?> LoadAsync(CancellationToken cancellationToken);
    Task ForgetAsync(CancellationToken cancellationToken);
}

public interface IMflCookieStore
{
    Task SaveAsync(string cookieValue, CancellationToken cancellationToken);
    Task<string?> LoadAsync(CancellationToken cancellationToken);
    Task ForgetAsync(CancellationToken cancellationToken);
}

public enum RefreshReason { Login, LeagueChanged, Manual, Mutation, StaleData, Force }

public sealed record RefreshSectionResult(SnapshotSection Section, bool IsSuccess, string? Error, TimeSpan Duration);

public sealed record RefreshReport(
    LeagueContext League,
    RefreshReason Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<RefreshSectionResult> Sections,
    LeagueSnapshot Snapshot)
{
    public bool IsSuccess => Sections.All(x => x.IsSuccess);
    public bool IsPartial => Sections.Any(x => x.IsSuccess) && Sections.Any(x => !x.IsSuccess);
}

public sealed record RefreshProgress(SnapshotSection Section, int Completed, int Total, string Message);

public interface IMflRefreshService
{
    event EventHandler<LeagueSnapshot>? SnapshotUpdated;
    Task<RefreshReport> RefreshAllAsync(LeagueContext league, RefreshReason reason, CancellationToken cancellationToken);
    Task<bool> RefreshPlayerStatusesAsync(LeagueContext league, CancellationToken cancellationToken);
}

public interface IMflLineupQueryService
{
    Task<MflResult<WeeklyLineup>> GetAsync(LeagueContext league, int week, IReadOnlyList<string> rosterPlayerIds,
        CancellationToken cancellationToken, string? weeklyResultsBody = null);
    Task<MflResult<IReadOnlyList<PlayerRosterAvailability>>> GetAvailabilityAsync(
        LeagueContext league, int week, IReadOnlyList<string> playerIds, CancellationToken cancellationToken);
}

public interface ILeagueRankingsService
{
    Task<MflResult<LeagueRankingData>> GetAsync(
        LeagueContext league, int fallbackWeek, CancellationToken cancellationToken);
}

public interface ITeamViewService
{
    Task<MflResult<TeamViewData>> GetAsync(
        LeagueContext league,
        string franchiseId,
        int fallbackWeek,
        IReadOnlyDictionary<string, Player> players,
        CancellationToken cancellationToken);
}

public interface IMflMutationCoordinator
{
    MutationPreview Preview(LeagueContext league, MflMutation mutation);
    Task<MutationReceipt> ExecuteAsync(LeagueContext league, MflMutation mutation, bool confirmed, CancellationToken cancellationToken);
}

public interface IConfirmationPolicy
{
    bool RequiresConfirmation(MutationKind kind);
}

public interface ILeagueState
{
    LeagueContext? SelectedLeague { get; }
    LeagueSnapshot? CurrentSnapshot { get; }
    event EventHandler<LeagueContext?>? LeagueChanged;
    event EventHandler<LeagueSnapshot?>? SnapshotChanged;
    void SelectLeague(LeagueContext? context);
    void UpdateSnapshot(LeagueSnapshot snapshot);
}
