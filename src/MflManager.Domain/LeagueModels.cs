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

public sealed record PlayerRanking(string PlayerName, string Team, string Position, int PositionRank, string Opponent = "", string Venue = "");

public sealed record RosterPlayer(
    Player Player,
    RosterStatus Status,
    decimal? Salary = null,
    int? ContractYear = null);

public sealed record LineupSlot(string Position, string? PlayerId, bool IsStarter, bool IsTiebreaker = false);

public sealed record LineupPositionRule(string Position, int Minimum, int Maximum);

public sealed record WeeklyLineup(
    int Week,
    IReadOnlyList<string> StarterPlayerIds,
    IReadOnlyDictionary<string, decimal> PlayerPoints);

public sealed record MflWeekStatus(
    int LineupWeek,
    int CurrentWeek,
    int UpcomingWeek,
    int CompletedWeek,
    int LiveScoringWeek)
{
    public int ActiveLineupWeek => CurrentWeek > CompletedWeek
        ? CurrentWeek
        : LineupWeek > 0 ? LineupWeek : UpcomingWeek;
}

public sealed record PlayerRosterAvailability(string PlayerId, bool IsFreeAgent, bool CannotAdd, bool IsLocked)
{
    public bool CanAdd => IsFreeAgent && !CannotAdd && !IsLocked;
}

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
    string? FranchiseId = null)
{
    public IReadOnlyList<string> AcquiredPlayerIds { get; init; } = [];
    public IReadOnlyList<string> DroppedPlayerIds { get; init; } = [];
    public decimal? BidAmount { get; init; }
}

public sealed record WaiverClaim(
    string? Id,
    string AddPlayerId,
    string? DropPlayerId,
    int Priority,
    decimal? BidAmount = null,
    bool IsConditional = false,
    int Round = 1);

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
    IReadOnlyDictionary<string, string> Values,
    int StarterCount = 0,
    int CurrentWeek = 0,
    IReadOnlyList<LineupPositionRule>? StartingPositions = null)
{
    public IReadOnlyList<LineupPositionRule> LineupRules => StartingPositions ?? [];
    public int RostersPerPlayer => Values.TryGetValue("rostersPerPlayer", out var value) &&
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var copies)
            ? copies : 0;
    public string PlayerLimitUnit => Values.GetValueOrDefault("playerLimitUnit", string.Empty);
}

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
    NflSchedule,
    Injuries,
    NflByeWeeks
}

public sealed record SectionState(bool IsStale, DateTimeOffset? UpdatedAt, string? Error = null);

public sealed record LeagueSnapshot
{
    public IReadOnlyDictionary<string, int> NflByeWeeks { get; init; } = new Dictionary<string, int>();
    public int RankingWeek { get; init; }
    public int LineupWeek { get; init; }
    public bool AvailablePlayersReady { get; init; }
    public bool WeeklyRankingsReady { get; init; }
    public bool RankingsStale { get; init; }
    public string RankingSource { get; init; } = string.Empty;
    public string? AvailablePlayersError { get; init; }
    public required LeagueContext Context { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public LeagueSettings? Settings { get; init; }
    public IReadOnlyList<Franchise> Franchises { get; init; } = [];
    public IReadOnlyList<RosterPlayer> Roster { get; init; } = [];
    public IReadOnlyList<LineupSlot> Lineup { get; init; } = [];
    // Separate provenance: LineupWeek can advance while last-good scores remain cached.
    // Older snapshots deserialize to zero and must be revalidated before displaying scores.
    public int WeeklyPlayerPointsWeek { get; init; }
    public IReadOnlyDictionary<string, decimal> WeeklyPlayerPoints { get; init; } =
        new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>());
    public bool HasPointsForWeek(int week) => week > 0 && WeeklyPlayerPointsWeek == week;
    public IReadOnlyList<Standing> Standings { get; init; } = [];
    public IReadOnlyList<Matchup> Matchups { get; init; } = [];
    public IReadOnlyList<Player> Players { get; init; } = [];
    public IReadOnlyList<Player> FreeAgents { get; init; } = [];
    public IReadOnlyDictionary<string, int> FreeAgentCopies { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
    public IReadOnlyList<PlayerRanking> Rankings { get; init; } = [];
    public IReadOnlyList<Transaction> Transactions { get; init; } = [];
    public IReadOnlyList<WaiverClaim> Waivers { get; init; } = [];
    public DateTimeOffset? NextWaiverProcessingAt { get; init; }
    public decimal? FaabBalance { get; init; }
    public IReadOnlyList<PendingTrade> Trades { get; init; } = [];
    public DraftState? Draft { get; init; }
    public IReadOnlyDictionary<SnapshotSection, SectionState> Sections { get; init; } =
        new ReadOnlyDictionary<SnapshotSection, SectionState>(new Dictionary<SnapshotSection, SectionState>());
}
