using System.Collections.ObjectModel;

namespace MflManager.Domain;

public sealed record SeasonContext(int Year, bool IsReadOnly)
{
    public static SeasonContext Current(int year) => new(year, false);
    public static SeasonContext Historical(int year) => new(year, true);
}

public sealed record LeagueContext(
    string LeagueId,
    SeasonContext Season,
    Uri Host,
    string? FranchiseId = null,
    bool IsCommissioner = false)
{
    public Uri ExportUri => new(Host, $"/{Season.Year}/export");
    public Uri ImportUri => new(Host, $"/{Season.Year}/import");
    public Uri LiveDraftUri => new(Host, $"/{Season.Year}/live_draft");
}

public sealed record LeagueSummary(
    string LeagueId,
    int Season,
    string Name,
    Uri Host,
    string? FranchiseId,
    bool IsCommissioner);

public sealed record Franchise(
    string Id,
    string Name,
    string? Abbreviation = null,
    string? OwnerName = null,
    string? Email = null,
    string? LogoUrl = null);

public enum RosterStatus { Active, InjuredReserve, TaxiSquad }

public sealed record Player(
    string Id,
    string Name,
    string Position,
    string NflTeam,
    string? Status = null,
    string? Injury = null,
    decimal? ProjectedPoints = null,
    decimal? AverageDraftPosition = null);

public sealed record FantasyProsRanking(string PlayerName, string Team, string Position, int Rank, int? Tier = null);

public sealed record RosterPlayer(
    Player Player,
    RosterStatus Status,
    decimal? Salary = null,
    int? ContractYear = null);

public sealed record LineupSlot(string Position, string? PlayerId, bool IsStarter, bool IsTiebreaker = false);

public sealed record Standing(
    string FranchiseId,
    string FranchiseName,
    int Wins,
    int Losses,
    int Ties,
    decimal PointsFor,
    int Rank);

public sealed record Matchup(
    int Week,
    string HomeFranchiseId,
    string HomeName,
    decimal? HomeScore,
    string AwayFranchiseId,
    string AwayName,
    decimal? AwayScore,
    bool IsComplete);

public sealed record Transaction(
    string Id,
    DateTimeOffset Timestamp,
    string Type,
    string Description,
    string? FranchiseId = null);

public sealed record WaiverClaim(
    string? Id,
    string AddPlayerId,
    string? DropPlayerId,
    int Priority,
    decimal? BidAmount = null,
    bool IsConditional = false);

public enum TradeAssetKind { Player, DraftPick, BlindBidDollars }

public sealed record TradeAsset(TradeAssetKind Kind, string Value, decimal? Amount = null);

public sealed record PendingTrade(
    string Id,
    string FromFranchiseId,
    string ToFranchiseId,
    IReadOnlyList<TradeAsset> Offered,
    IReadOnlyList<TradeAsset> Requested,
    string? Comments,
    DateTimeOffset? ExpiresAt);

public sealed record DraftPick(int Round, int Pick, int Overall, string FranchiseId, string? PlayerId, string? Comments);

public sealed record DraftState(
    int Round,
    int Pick,
    int Overall,
    string OnTheClockFranchiseId,
    DateTimeOffset? ClockExpiresAt,
    bool IsPaused,
    IReadOnlyList<DraftPick> Picks,
    IReadOnlyList<string> Queue);

public sealed record LeagueSettings(
    string Name,
    int StartWeek,
    int EndWeek,
    int RosterSize,
    int InjuredReserveSize,
    int TaxiSquadSize,
    IReadOnlyDictionary<string, string> Values);

public enum SnapshotSection
{
    League,
    Rules,
    Franchises,
    Rosters,
    Lineup,
    Standings,
    Matchups,
    Players,
    Adp,
    FreeAgents,
    Transactions,
    Waivers,
    Trades,
    Draft,
    DraftQueue,
    InjuredReserve,
    TaxiSquad,
    Keepers,
    Messages,
    Polls,
    Calendar,
    NflSchedule
}

public sealed record SectionState(bool IsStale, DateTimeOffset? UpdatedAt, string? Error = null);

public sealed record LeagueSnapshot
{
    public required LeagueContext Context { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public LeagueSettings? Settings { get; init; }
    public IReadOnlyList<Franchise> Franchises { get; init; } = [];
    public IReadOnlyList<RosterPlayer> Roster { get; init; } = [];
    public IReadOnlyList<LineupSlot> Lineup { get; init; } = [];
    public IReadOnlyList<Standing> Standings { get; init; } = [];
    public IReadOnlyList<Matchup> Matchups { get; init; } = [];
    public IReadOnlyList<Player> Players { get; init; } = [];
    public IReadOnlyList<Player> FreeAgents { get; init; } = [];
    public IReadOnlyList<FantasyProsRanking> Rankings { get; init; } = [];
    public IReadOnlyList<Transaction> Transactions { get; init; } = [];
    public IReadOnlyList<WaiverClaim> Waivers { get; init; } = [];
    public IReadOnlyList<PendingTrade> Trades { get; init; } = [];
    public DraftState? Draft { get; init; }
    public IReadOnlyDictionary<SnapshotSection, SectionState> Sections { get; init; } =
        new ReadOnlyDictionary<SnapshotSection, SectionState>(new Dictionary<SnapshotSection, SectionState>());
}
