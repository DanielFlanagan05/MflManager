using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MflManager.App;
using MflManager.Application;
using MflManager.Domain;
using MflManager.Terminal;
using Microsoft.Extensions.Logging;

namespace MflManager.App.ViewModels;

public sealed record DisplayRow(string Primary, string Secondary, string Tertiary, string Status);
public sealed record AgentChatMessage(string Role, string Text);
public sealed record DraftQueueRow(int Order, string PlayerName, string Position, string Team);
public sealed record BenchPlayerRow(string DisplayName, string Position, string NflTeam, string Status, decimal? Points = null, string StatusCode = "")
{
    public string PointsText => Points?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—";
}
public partial class PendingWaiverRow : ObservableObject
{
    public WaiverClaim Claim { get; }
    public string PlayerName { get; }
    public string BidAmount => Claim.BidAmount?.ToString("C0") ?? "Priority claim";
    public string DropPlayerName { get; }
    public DateTimeOffset? ProcessingAt { get; }
    public IAsyncRelayCommand<WaiverClaim> CancelCommand { get; }
    public IRelayCommand EditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IReadOnlyList<LineupPlayerOption> DropOptions { get; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial string EditBidInput { get; set; }
    [ObservableProperty] public partial LineupPlayerOption? SelectedDropPlayer { get; set; }
    public PendingWaiverRow(WaiverClaim claim, string playerName, string dropPlayerName,
        DateTimeOffset? processingAt, IAsyncRelayCommand<WaiverClaim> cancelCommand,
        IReadOnlyList<LineupPlayerOption> dropOptions, Func<PendingWaiverRow, Task> save)
    {
        Claim = claim; PlayerName = playerName; DropPlayerName = dropPlayerName;
        ProcessingAt = processingAt; CancelCommand = cancelCommand; DropOptions = dropOptions;
        EditBidInput = claim.BidAmount?.ToString("0", CultureInfo.InvariantCulture) ?? "0";
        SelectedDropPlayer = dropOptions.FirstOrDefault(option => option.PlayerId == claim.DropPlayerId) ?? dropOptions.FirstOrDefault();
        EditCommand = new RelayCommand(() => IsEditing = true);
        CancelEditCommand = new RelayCommand(() =>
        {
            EditBidInput = Claim.BidAmount?.ToString("0", CultureInfo.InvariantCulture) ?? "0";
            SelectedDropPlayer = DropOptions.FirstOrDefault(option => option.PlayerId == Claim.DropPlayerId) ?? DropOptions.FirstOrDefault();
            IsEditing = false;
        });
        SaveCommand = new AsyncRelayCommand(() => save(this));
    }
    public string ProcessingDate => ProcessingAt is { } value && value > DateTimeOffset.Now
        ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "Schedule unavailable";
    public string CancelAutomationId => $"CancelWaiver{Claim.AddPlayerId}";
    public string CancelAccessibleName => $"Cancel waiver claim for {PlayerName}";
    public override string ToString() => PlayerName;
}

public sealed record DraftBoardRow(
    string Order,
    string DraftedPlayerName,
    string DraftedPosition,
    string DraftedNflTeam,
    string FantasyTeamName,
    string AvailableAdp,
    string AvailablePlayerName,
    string AvailablePosition,
    string AvailableNflTeam);

public partial class MainPageViewModel : ObservableObject
{
    private readonly IMflAuthenticationClient _authentication;
    private readonly IMflLeagueDiscoveryClient _discovery;
    private readonly IMflRefreshService _refresh;
    private readonly ILeagueState _state;
    private readonly ISecureSessionStore _session;
    private readonly IOperationJournal _journal;
    private readonly ICodexTerminalService _terminal;
    private readonly IMflMutationCoordinator _mutations;
    private readonly IMflWaiverDataService _waiverData;
    private readonly IMflLineupQueryService _lineups;
    private readonly ILeagueRankingsService _leagueRankings;
    private int _leagueRankingsWeek;
    private string _teamViewReturnPageTag = "League Rankings";
    private readonly WindowsCredentialLoginStore _windowsCredentialLogin;
    private readonly ILogger<MainPageViewModel> _logger;
    private int _lastAgentPromptOverall;
    private volatile int _agentConversationVersion;
    private readonly DraftAdviceSession _draftAdvice = new();
    private string _pageTag = "Dashboard";
    private readonly List<string> _draftQueueIds = [];
    private bool _queueHasLocalChanges;
    private int _lineupLoadingWeek;
    private int _lineupLoadVersion;
    private int _lineupPointsWeek;
    private IReadOnlyDictionary<string, decimal> _lineupPlayerPoints = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    private int _lastObservedActiveLineupWeek;
    private bool _suppressLineupWeekSelectionLoad;
    private int _playerAvailabilityLoadVersion;
    private IReadOnlySet<string> _pickupEligiblePlayerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, decimal> _seasonPlayerPoints = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    private int _waiversLoadVersion;
    private decimal? _latestFaabBalance;
    private DateTimeOffset _latestFaabFetchedAt;

    public ObservableCollection<LeagueSummary> Leagues { get; } = [];
    public ObservableCollection<DisplayRow> Rows { get; } = [];
    public ObservableCollection<DraftQueueRow> DraftQueue { get; } = [];
    public ObservableCollection<DraftBoardRow> DraftBoard { get; } = [];
    public ObservableCollection<OperationJournalEntry> AgentHistory { get; } = [];
    public ObservableCollection<AgentChatMessage> AgentMessages { get; } =
        [new("Luna", "Ask about your lineup, available players, or a waiver decision. I’ll use your league data and current-week news. Advice never submits a move.")];
    public ObservableCollection<LineupSlotRow> LineupSlots { get; } = [];
    public ObservableCollection<BenchPlayerRow> BenchPlayers { get; } = [];
    [ObservableProperty] public partial bool IsPlayerStatusStale { get; set; }
    public ObservableCollection<LeagueRankingRow> LeagueRankingRows { get; } = [];
    public ObservableCollection<int> LineupWeeks { get; } = [];
    public IReadOnlyList<string> RankingPositions { get; } = ["QB", "RB", "WR", "TE", "DEF", "K"];
    [ObservableProperty] public partial string SelectedRankingPosition { get; set; } = "QB";
    [ObservableProperty] public partial bool HasRankingPositionFilter { get; set; }
    public ObservableCollection<PendingWaiverRow> PendingWaivers { get; } = [];
    public ObservableCollection<RankedWaiverPlayer> WaiverPlayers { get; } = [];
    public ObservableCollection<LineupPlayerOption> WaiverDropPlayers { get; } = [];
    public ObservableCollection<LineupPlayerOption> WaiverClaimPlayers { get; } = [];
    public Func<string, Task<bool>>? ConfirmWaiverAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmCancelWaiverAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmReorderWaiverAsync { get; set; }
    [ObservableProperty] public partial bool IsWaiversPage { get; set; }
    [ObservableProperty] public partial string WaiversStatus { get; set; } = "Loading pending claims…";
    [ObservableProperty] public partial string WaiverPlayersStatus { get; set; } = "Loading current-week rankings and available players…";
    [ObservableProperty] public partial string WaiverError { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsWaiverErrorOpen { get; set; }
    [ObservableProperty] public partial RankedWaiverPlayer? SelectedWaiverPlayer { get; set; }
    [ObservableProperty] public partial LineupPlayerOption? SelectedWaiverDropPlayer { get; set; }
    [ObservableProperty] public partial LineupPlayerOption? SelectedWaiverAddPlayer { get; set; }
    [ObservableProperty] public partial string WaiverBidInput { get; set; } = string.Empty;
    [ObservableProperty] public partial string FaabBalanceText { get; set; } = "FAAB Balance: —";

    [ObservableProperty] public partial string PageTitle { get; set; } = "Dashboard";
    [ObservableProperty] public partial string PageSubtitle { get; set; } = "Sign in to load your MyFantasyLeague teams.";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Ready";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsSignedIn { get; set; }
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial LeagueSummary? SelectedLeague { get; set; }
    [ObservableProperty] public partial string TerminalText { get; set; } = "MFL agent terminal is stopped.\r\n";
    [ObservableProperty] public partial string TerminalState { get; set; } = "Stopped";
    [ObservableProperty] public partial bool IsAgentWorking { get; set; }
    [ObservableProperty] public partial string CodexAccountStatus { get; set; } = "Checking Codex account…";
    [ObservableProperty] public partial string CodexAccountEmail { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsCodexAuthenticated { get; set; }
    [ObservableProperty] public partial string LastRefresh { get; set; } = "Never";
    [ObservableProperty] public partial bool IsAgentPage { get; set; }
    [ObservableProperty] public partial bool IsDraftPage { get; set; }
    [ObservableProperty] public partial bool IsMyTeamPage { get; set; }
    [ObservableProperty] public partial bool IsLeagueRankingsPage { get; set; }
    [ObservableProperty] public partial bool IsTransactionsPage { get; set; }
    [ObservableProperty] public partial bool IsTeamViewPage { get; set; }
    [ObservableProperty] public partial bool IsDataPage { get; set; } = true;
    [ObservableProperty] public partial bool IsStandardDataPage { get; set; } = true;
    [ObservableProperty] public partial bool IsDraftAgentEnabled { get; set; }
    [ObservableProperty] public partial string DraftQueuePlayerSearch { get; set; } = string.Empty;
    [ObservableProperty] public partial string DraftQueueHead { get; set; } = "No players queued";
    [ObservableProperty] public partial string AvailablePlayersTitle { get; set; } = "Available players";
    [ObservableProperty] public partial string AvailableRankHeader { get; set; } = "Rank";
    [ObservableProperty] public partial string ResultsPrimaryHeader { get; set; } = "Team / City";
    [ObservableProperty] public partial string ResultsSecondaryHeader { get; set; } = "Position";
    [ObservableProperty] public partial string ResultsTertiaryHeader { get; set; } = "Player name";
    [ObservableProperty] public partial string ResultsStatusHeader { get; set; } = "Status / value";
    [ObservableProperty] public partial int SelectedLineupWeek { get; set; }
    [ObservableProperty] public partial bool IsLineupLoading { get; set; }
    [ObservableProperty] public partial string LineupError { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLineupErrorOpen { get; set; }
    [ObservableProperty] public partial bool IsLineupEditable { get; set; }
    [ObservableProperty] public partial bool IsLeagueRankingsLoading { get; set; }
    [ObservableProperty] public partial string LeagueRankingsStatus { get; set; } = "Open this tab to load league rankings.";
    public TeamViewViewModel TeamView { get; }

    public MainPageViewModel(IMflAuthenticationClient authentication, IMflLeagueDiscoveryClient discovery,
        IMflRefreshService refresh, ILeagueState state, ISecureSessionStore session,
        IOperationJournal journal, ICodexTerminalService terminal, IMflMutationCoordinator mutations, IMflWaiverDataService waiverData,
        IMflLineupQueryService lineups, ILeagueRankingsService leagueRankings, TeamViewViewModel teamView,
        WindowsCredentialLoginStore windowsCredentialLogin, ILogger<MainPageViewModel> logger)
    {
        _authentication = authentication;
        _discovery = discovery;
        _refresh = refresh;
        _state = state;
        _session = session;
        _journal = journal;
        _terminal = terminal;
        _mutations = mutations;
        _waiverData = waiverData;
        _lineups = lineups;
        _leagueRankings = leagueRankings;
        TeamView = teamView;
        _windowsCredentialLogin = windowsCredentialLogin;
        _logger = logger;
        _state.SnapshotChanged += (_, snapshot) => App.DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
        _terminal.OutputReceived += (_, output) =>
        {
            var version = _agentConversationVersion;
            App.DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _agentConversationVersion) return;
                TerminalText += output.Text;
                AgentMessages.Add(new(output.IsError ? "Connection issue" : "Luna", output.Text.Trim()));
            });
        };
        _terminal.StateChanged += (_, stateValue) => App.DispatcherQueue.TryEnqueue(() => TerminalState = stateValue.ToString());
    }

    public async Task<bool> LoginAsync(string username, string password, bool remember, CancellationToken cancellationToken)
    {
        IsBusy = true; StatusMessage = "Signing in…"; IsStatusOpen = true;
        try
        {
            var season = SeasonContext.Current(DateTimeOffset.Now.Year);
            var result = await _authentication.LoginAsync(season, new(username, password, remember), cancellationToken);
            if (!result.IsSuccess) { StatusMessage = result.UserMessage ?? "Sign-in failed."; return false; }
            if (remember) await _session.SaveAsync(username, "saved-session", cancellationToken);
            var leagues = await _discovery.GetMyLeaguesAsync(season, cancellationToken);
            if (!leagues.IsSuccess) { StatusMessage = leagues.UserMessage ?? "Could not load leagues."; return false; }
            Leagues.Clear(); foreach (var league in leagues.Value ?? []) Leagues.Add(league);
            IsSignedIn = true;
            StatusMessage = Leagues.Count == 0 ? "Signed in, but no leagues were returned." : $"Signed in. Found {Leagues.Count} league(s).";
            if (Leagues.Count > 0) { SelectedLeague = Leagues[0]; await SelectLeagueAsync(Leagues[0], cancellationToken); }
            return true;
        }
        finally { IsBusy = false; }
    }

    public async Task TryWindowsCredentialLoginAsync(CancellationToken cancellationToken)
    {
        var credential = _windowsCredentialLogin.Load();
        if (credential is null) return;
        try
        {
            var signedIn = await LoginAsync(credential.Value.Username, credential.Value.Password, true, cancellationToken);
            if (!signedIn) IsStatusOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Saved-session startup failed with {ExceptionType}", ex.GetType().Name);
            StatusMessage = "Startup data loading failed. Details were recorded in local logs.";
            IsStatusOpen = true;
        }
    }

    public async Task SelectLeagueAsync(LeagueSummary league, CancellationToken cancellationToken)
    {
        SelectedLeague = league;
        _lastObservedActiveLineupWeek = 0;
        SelectedLineupWeek = 0;
        _lineupPointsWeek = 0;
        _lineupPlayerPoints = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var context = new LeagueContext(league.LeagueId,
            league.Season == DateTimeOffset.Now.Year ? SeasonContext.Current(league.Season) : SeasonContext.Historical(league.Season),
            league.Host, league.FranchiseId, league.IsCommissioner);
        _latestFaabBalance = null;
        _latestFaabFetchedAt = DateTimeOffset.MinValue;
        _seasonPlayerPoints = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        _state.SelectLeague(context);
        await RefreshCoreAsync(RefreshReason.LeagueChanged, cancellationToken);
        if (IsTeamViewPage) await SetPageAsync("League Rankings", cancellationToken);
        else if (IsWaiversPage) await LoadWaiversDataAsync(cancellationToken);
        else if (IsLeagueRankingsPage) await RefreshLeagueRankingsAsync(cancellationToken);
    }

    [RelayCommand] private async Task RefreshAsync() => await RefreshCoreAsync(RefreshReason.Manual, CancellationToken.None);

    private async Task RefreshCoreAsync(RefreshReason reason, CancellationToken cancellationToken)
    {
        var league = _state.SelectedLeague;
        if (league is null) { StatusMessage = "Select a league first."; IsStatusOpen = true; return; }
        IsBusy = true; StatusMessage = "Refreshing every applicable league section…"; IsStatusOpen = true;
        try
        {
            if (_pageTag == "Players") _pickupEligiblePlayerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var report = await _refresh.RefreshAllAsync(league, reason, cancellationToken);
            LastRefresh = report.CompletedAt.ToLocalTime().ToString("g");
            StatusMessage = report.IsSuccess ? "Refresh complete." : report.IsPartial ? "Refresh completed with stale sections; last-good data was preserved." : "Refresh failed; cached data is still visible.";
            if (IsMyTeamPage && (!report.Snapshot.HasPointsForWeek(SelectedLineupWeek) || report.Snapshot.WeeklyPlayerPoints.Count == 0))
                await LoadLineupWeekAsync(SelectedLineupWeek, cancellationToken);
        }
        catch (WeeklyPointsValidationException ex)
        {
            ClearLineupPoints();
            if (_state.CurrentSnapshot is { } snapshot) ShowRosterWithoutStarters(snapshot);
            ShowLineupError(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Refresh aborted with {ExceptionType}", ex.GetType().Name);
            StatusMessage = "Refresh failed. Cached data remains available; details were recorded in local logs.";
            IsStatusOpen = true;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _authentication.SignOutAsync(CancellationToken.None);
        await _session.ForgetAsync(CancellationToken.None);
        _state.SelectLeague(null); Leagues.Clear(); Rows.Clear(); SelectedLeague = null; IsSignedIn = false;
        StatusMessage = "Signed out and removed the saved MFL session."; IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task StartAgentAsync()
    {
        try
        {
            await _terminal.StartAsync(FindRepositoryRoot(), "mfl-manager-agent", CancellationToken.None);
            CodexAccountStatus = "Signed in to Codex";
            await LoadHistoryAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await RefreshCodexStatusAsync(CancellationToken.None);
            StatusMessage = ex is System.ComponentModel.Win32Exception
                ? "Codex CLI is not installed. Install Codex, then try again."
                : ex.Message;
            IsStatusOpen = true;
        }
    }

    [RelayCommand] private async Task StopAgentAsync()
    {
        IsDraftAgentEnabled = false;
        await _terminal.StopAsync(CancellationToken.None);
    }
    [RelayCommand] private async Task RestartAgentAsync() { await _terminal.StopAsync(CancellationToken.None); await StartAgentAsync(); }

    [RelayCommand]
    private async Task ClearAgentConversationAsync()
    {
        if (IsAgentWorking) return;
        IsAgentWorking = true;
        try
        {
            await _terminal.ClearConversationAsync(CancellationToken.None);
            _agentConversationVersion++;
            AgentMessages.Clear();
            TerminalText = string.Empty;
        }
        finally { IsAgentWorking = false; }
    }

    [RelayCommand]
    private async Task SignInToCodexAsync()
    {
        try
        {
            CodexAccountStatus = "Waiting for ChatGPT sign-in…";
            await _terminal.SignInAsync(CancellationToken.None);
            await RefreshCodexStatusAsync(CancellationToken.None);
            StatusMessage = "Codex sign-in completed. You can start Luna now.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await RefreshCodexStatusAsync(CancellationToken.None);
            StatusMessage = ex is System.ComponentModel.Win32Exception
                ? "Codex CLI is not installed."
                : "Codex sign-in was not completed.";
        }
        IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task SignOutOfCodexAsync()
    {
        try
        {
            await StopAgentAsync();
            await _terminal.SignOutAsync(CancellationToken.None);
            await RefreshCodexStatusAsync(CancellationToken.None);
            StatusMessage = "Signed out of Codex.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await RefreshCodexStatusAsync(CancellationToken.None);
            StatusMessage = ex is System.ComponentModel.Win32Exception
                ? "Codex CLI is not installed."
                : "Codex sign-out was not completed.";
        }
        IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task CheckCodexAccountAsync() => await RefreshCodexStatusAsync(CancellationToken.None);

    [RelayCommand]
    private async Task SaveDraftQueueAsync()
    {
        var league = _state.SelectedLeague;
        if (league is null) { StatusMessage = "Select a league first."; IsStatusOpen = true; return; }
        var receipt = await _mutations.ExecuteAsync(league, new PlayerListMutation(MutationKind.SetDraftQueue, "myDraftList", _draftQueueIds), false, CancellationToken.None);
        if (receipt.Accepted) _queueHasLocalChanges = false;
        StatusMessage = receipt.Message; IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task SyncDraftQueueAsync()
    {
        _queueHasLocalChanges = false;
        await RefreshCoreAsync(RefreshReason.Manual, CancellationToken.None);
    }

    [RelayCommand]
    private void AddDraftQueuePlayer()
    {
        var snapshot = _state.CurrentSnapshot;
        var query = DraftQueuePlayerSearch.Trim();
        if (snapshot is null || string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = "Enter a player name after the league data has loaded."; IsStatusOpen = true; return;
        }
        var drafted = snapshot.Draft?.Picks.Select(x => x.PlayerId).Where(x => x is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var candidates = snapshot.Players.Concat(snapshot.FreeAgents).Concat(snapshot.Roster.Select(x => x.Player));
        var player = candidates.FirstOrDefault(x => x.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) ??
            candidates.FirstOrDefault(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (player is null) { StatusMessage = "No available player matched that name."; IsStatusOpen = true; return; }
        if (drafted.Contains(player.Id)) { StatusMessage = $"{player.Name} has already been drafted."; IsStatusOpen = true; return; }
        if (_draftQueueIds.Contains(player.Id, StringComparer.OrdinalIgnoreCase)) { StatusMessage = $"{player.Name} is already in the queue."; IsStatusOpen = true; return; }
        _draftQueueIds.Add(player.Id);
        _queueHasLocalChanges = true;
        DraftQueuePlayerSearch = string.Empty;
        UpdateDraftQueueRows(snapshot);
    }

    public async Task SendTerminalAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAgentWorking) return;
        IsAgentWorking = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_terminal.State is TerminalConnectionState.Stopped or TerminalConnectionState.Faulted)
                await _terminal.StartAsync(FindRepositoryRoot(), "mfl-manager-agent", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AgentMessages.Add(new("You", text.Trim()));
            var snapshot = _state.CurrentSnapshot;
            var context = LunaPrompt.Context(DateTimeOffset.UtcNow, _state.SelectedLeague?.LeagueId,
                _state.SelectedLeague?.Season.Year, snapshot is null ? null : LineupRules.ActiveWeek(snapshot), IsDraftAgentEnabled);
            await _terminal.WriteAsync(context + "\n\nUser request: " + text, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = ex.Message;
            IsStatusOpen = true;
            AgentMessages.Add(new("Connection issue", ex.Message));
        }
        finally { IsAgentWorking = false; }
    }

    partial void OnIsDraftAgentEnabledChanged(bool value)
    {
        _draftAdvice.SetEnabled(value);
        _lastAgentPromptOverall = -1;
        if (value && _state.CurrentSnapshot is { } snapshot) ApplySnapshot(snapshot);
    }

    private async Task SendDraftAdviceAsync(string text)
    {
        using var request = _draftAdvice.TryBegin();
        if (request is null) return;
        try { await SendTerminalAsync(text, request.Token); }
        finally { _draftAdvice.Complete(request); }
    }

    private async Task RefreshCodexStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _terminal.GetStatusAsync(cancellationToken);
        IsCodexAuthenticated = status.IsAuthenticated;
        CodexAccountEmail = status.IsAuthenticated
            ? status.AccountEmail ?? (status.AuthenticationType == "apiKey" ? "Signed in with API key" : "ChatGPT account")
            : string.Empty;
        CodexAccountStatus = !status.IsInstalled ? "Codex CLI not installed"
            : status.IsAuthenticated ? $"Signed in · {status.Version}" : $"Sign-in required · {status.Version}";
    }

    public async Task SetPageAsync(string tag, CancellationToken cancellationToken = default)
    {
        _pageTag = tag;
        PageTitle = tag;
        IsAgentPage = tag == "Agent";
        IsDraftPage = tag == "Draft";
        IsMyTeamPage = tag == "My Team";
        IsLeagueRankingsPage = tag == "League Rankings";
        IsTransactionsPage = tag == "Transactions";
        IsTeamViewPage = tag == "Team View";
        IsWaiversPage = tag == "Waivers";
        HasRankingPositionFilter = tag is "Waivers" or "Players" or "Rankings" or "Draft";
        IsDataPage = !IsAgentPage;
        IsStandardDataPage = !IsAgentPage && !IsDraftPage && !IsMyTeamPage && !IsWaiversPage && !IsLeagueRankingsPage && !IsTeamViewPage && !IsTransactionsPage;
        ApplySnapshot(_state.CurrentSnapshot);
        if (IsAgentPage)
        {
            try { await RefreshCodexStatusAsync(cancellationToken); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                CodexAccountStatus = "Codex status unavailable";
            }
        }
        else if (IsLeagueRankingsPage) await RefreshLeagueRankingsAsync(cancellationToken);
        else if (IsMyTeamPage && _state.SelectedLeague is { } league)
        {
            var statusRefreshFailed = false;
            try { await _refresh.RefreshPlayerStatusesAsync(league, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Player status refresh failed with {ExceptionType}", ex.GetType().Name);
                statusRefreshFailed = true;
            }
            ApplySnapshot(_state.CurrentSnapshot);
            if (statusRefreshFailed) IsPlayerStatusStale = true;
            if (_state.CurrentSnapshot is { } snapshot &&
                (!snapshot.HasPointsForWeek(SelectedLineupWeek) || snapshot.WeeklyPlayerPoints.Count == 0))
                await LoadLineupWeekAsync(SelectedLineupWeek, cancellationToken);
        }
        _logger.LogInformation("UI selected page {Page}: {RowCount} rows, roster {RosterCount}, players {PlayerCount}, free agents {FreeAgentCount}",
            tag, Rows.Count, _state.CurrentSnapshot?.Roster.Count ?? 0, _state.CurrentSnapshot?.Players.Count ?? 0,
            _state.CurrentSnapshot?.FreeAgents.Count ?? 0);
    }

    [RelayCommand]
    private async Task RefreshLeagueRankingsAsync()
        => await RefreshLeagueRankingsAsync(CancellationToken.None);

    private async Task RefreshLeagueRankingsAsync(CancellationToken cancellationToken)
    {
        if (IsLeagueRankingsLoading) return;
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (league is null || snapshot is null)
        {
            LeagueRankingsStatus = "Select a league before loading league rankings.";
            return;
        }

        IsLeagueRankingsLoading = true;
        LeagueRankingsStatus = "Refreshing current-week and overall league points…";
        try
        {
            var result = await _leagueRankings.GetAsync(league, LineupRules.ActiveWeek(snapshot), cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                LeagueRankingsStatus = result.UserMessage ?? "League rankings could not be refreshed.";
                _logger.LogWarning("League rankings refresh failed with code {ErrorCode}, correlation {CorrelationId}",
                    result.MflErrorCode ?? "HTTP_OR_NETWORK", result.CorrelationId);
                return;
            }

            var rows = LeagueRankingTable.Build(result.Value, snapshot.Franchises, league.FranchiseId);
            _leagueRankingsWeek = result.Value.Week;
            LeagueRankingRows.Clear();
            foreach (var row in rows) LeagueRankingRows.Add(row);
            LeagueRankingsStatus = $"Week {result.Value.Week} points · {rows.Count} teams · refreshed {DateTimeOffset.Now:t}";
            _logger.LogInformation("League rankings rendered {TeamCount} teams for week {Week}; current team highlighted {HasCurrentTeam}",
                rows.Count, result.Value.Week, rows.Any(x => x.IsMyTeam));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError("League rankings refresh aborted with {ExceptionType}", ex.GetType().Name);
            LeagueRankingsStatus = "League rankings could not be refreshed. Existing results were preserved.";
        }
        finally { IsLeagueRankingsLoading = false; }
    }

    public async Task OpenTeamViewAsync(LeagueRankingRow team, CancellationToken cancellationToken)
    {
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (league is null || snapshot is null) return;

        _teamViewReturnPageTag = _pageTag;
        _pageTag = "Team View";
        PageTitle = team.TeamName;
        PageSubtitle = $"{snapshot.Settings?.Name ?? snapshot.Context.LeagueId} · read-only team view";
        IsAgentPage = false;
        IsDraftPage = false;
        IsMyTeamPage = false;
        IsLeagueRankingsPage = false;
        IsTeamViewPage = true;
        IsWaiversPage = false;
        HasRankingPositionFilter = false;
        IsDataPage = true;
        IsStandardDataPage = false;
        await TeamView.LoadAsync(team, league, snapshot,
            _leagueRankingsWeek > 0 ? _leagueRankingsWeek : LineupRules.ActiveWeek(snapshot),
            _teamViewReturnPageTag, cancellationToken);
    }

    public Task ReturnFromTeamViewAsync(CancellationToken cancellationToken) =>
        SetPageAsync(_teamViewReturnPageTag, cancellationToken);
    public void Filter(string text) { SearchText = text; ApplySnapshot(_state.CurrentSnapshot); }

    private void ApplySnapshot(LeagueSnapshot? snapshot)
    {
        Rows.Clear();
        PendingWaivers.Clear();
        WaiverPlayers.Clear();
        if (snapshot is null)
        {
            ++_lineupLoadVersion;
            IsLineupLoading = false;
            ClearLineupPoints();
            LineupSlots.Clear();
            BenchPlayers.Clear();
            IsPlayerStatusStale = false;
            FaabBalanceText = "FAAB Balance: —";
            PageSubtitle = "Select a league and refresh to load data.";
            return;
        }
        IsPlayerStatusStale = !snapshot.Sections.TryGetValue(SnapshotSection.Injuries, out var injurySection) || injurySection.IsStale;
        var leagueUpdatedAt = snapshot.Sections.GetValueOrDefault(SnapshotSection.League)?.UpdatedAt;
        if (leagueUpdatedAt > _latestFaabFetchedAt)
        {
            _latestFaabBalance = snapshot.FaabBalance;
            _latestFaabFetchedAt = leagueUpdatedAt.Value;
        }
        FaabBalanceText = _latestFaabBalance is { } balance ? $"FAAB Balance: {balance:C0}" : "FAAB Balance: —";
        _pickupEligiblePlayerIds = snapshot.AvailablePlayersReady
            ? snapshot.FreeAgents.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playerNames = snapshot.Players.Concat(snapshot.Roster.Select(x => x.Player)).DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => AvailablePlayerSelector.DisplayName(x.Name), StringComparer.OrdinalIgnoreCase);
        var editDropOptions = new[] { new LineupPlayerOption("", "No drop", "") }
            .Concat(snapshot.Roster.Select(x => new LineupPlayerOption(x.Player.Id,
                AvailablePlayerSelector.DisplayName(x.Player.Name), x.Player.Position))).ToArray();
        foreach (var claim in snapshot.Waivers.OrderBy(x => x.Round).ThenBy(x => x.Priority))
            PendingWaivers.Add(new(claim, playerNames.GetValueOrDefault(claim.AddPlayerId, $"Player {claim.AddPlayerId}"),
                claim.DropPlayerId is { } dropId ? playerNames.GetValueOrDefault(dropId, $"Player {dropId}") : "No drop",
                snapshot.NextWaiverProcessingAt, CancelWaiverCommand, editDropOptions, SaveEditedWaiverAsync));
        var rankedPlayers = WaiverPlayerSelector.Select(snapshot, SelectedRankingPosition);
        foreach (var player in rankedPlayers)
            WaiverPlayers.Add(player with { SeasonPoints = _seasonPlayerPoints.TryGetValue(player.Player.Id, out var points) ? points : null });
        var claimPlayers = WaiverPlayerSelector.Select(snapshot, SelectedRankingPosition, int.MaxValue)
            .Select(x => new LineupPlayerOption(x.Player.Id, x.DisplayName, x.PositionRank)).ToArray();
        if (!WaiverClaimPlayers.Select(x => x.PlayerId).SequenceEqual(claimPlayers.Select(x => x.PlayerId)))
        {
            var selectionId = SelectedWaiverAddPlayer?.PlayerId;
            WaiverClaimPlayers.Clear();
            foreach (var availablePlayer in claimPlayers) WaiverClaimPlayers.Add(availablePlayer);
            SelectedWaiverAddPlayer = WaiverClaimPlayers.FirstOrDefault(x => x.PlayerId == selectionId);
        }
        var waiverState = snapshot.Sections.GetValueOrDefault(SnapshotSection.Waivers);
        WaiversStatus = waiverState is null ? "Loading pending claims…" : waiverState.IsStale
            ? "Pending claims could not be refreshed; showing cached claims." : PendingWaivers.Count == 0 ? "No pending waiver claims." : $"{PendingWaivers.Count} pending claim(s) for your team.";
        WaiverPlayersStatus = snapshot.AvailablePlayersError ?? (!snapshot.AvailablePlayersReady
            ? "Loading positional rankings and available players…" : $"Week {snapshot.RankingWeek} · {WaiverPlayers.Count} available {SelectedRankingPosition} players · Fantasy Footballers");
        var drops = snapshot.Roster.Select(x => new LineupPlayerOption(x.Player.Id, AvailablePlayerSelector.DisplayName(x.Player.Name), x.Player.Position)).ToArray();
        if (!WaiverDropPlayers.Skip(1).Select(x => x.PlayerId).SequenceEqual(drops.Select(x => x.PlayerId)))
        {
            WaiverDropPlayers.Clear();
            WaiverDropPlayers.Add(new("", "No drop", ""));
            foreach (var drop in drops) WaiverDropPlayers.Add(drop);
            SelectedWaiverDropPlayer = WaiverDropPlayers[0];
        }
        if (snapshot.Draft is { } currentDraft)
        {
            var picked = currentDraft.Picks.Select(x => x.PlayerId).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!_queueHasLocalChanges)
            {
                _draftQueueIds.Clear();
                _draftQueueIds.AddRange(currentDraft.Queue);
            }
            _draftQueueIds.RemoveAll(picked.Contains);
            UpdateDraftQueueRows(snapshot);
        }
        UpdateDraftBoard(snapshot);
        if (IsDraftAgentEnabled && !IsAgentWorking && _terminal.State == TerminalConnectionState.Connected &&
            snapshot.Draft is { } draft && draft.OnTheClockFranchiseId == snapshot.Context.FranchiseId && draft.Overall != _lastAgentPromptOverall)
        {
            _lastAgentPromptOverall = draft.Overall;
            _ = SendDraftAdviceAsync($"Automatic draft advice: our turn at overall pick {draft.Overall}. Read fresh draft state, roster and rankings. Recommend up to three available players based on team needs and positional scarcity. Advice only: do not call any mutation or submit a pick.");
        }
        PageSubtitle = $"{snapshot.Settings?.Name ?? snapshot.Context.LeagueId} · {snapshot.Context.Season.Year}" + (snapshot.Context.Season.IsReadOnly ? " · Historical read-only" : string.Empty);
        if (_pageTag == "Players" && (!snapshot.AvailablePlayersReady || snapshot.AvailablePlayersError is not null)) PageSubtitle += $" · {WaiverPlayersStatus}";
        IsLineupEditable = !snapshot.Context.Season.IsReadOnly;
        ConfigureLineupWeeks(snapshot);
        if (IsMyTeamPage)
        {
            var slots = LineupRules.BuildSlots(snapshot.Settings);
            if (!IsLineupLoading && slots.Count > 0 && SelectedLineupWeek == LineupRules.ActiveWeek(snapshot))
            {
                if (snapshot.HasPointsForWeek(SelectedLineupWeek))
                {
                    _lineupPointsWeek = snapshot.WeeklyPlayerPointsWeek;
                    _lineupPlayerPoints = snapshot.WeeklyPlayerPoints;
                }
                else ClearLineupPoints();
                PopulateLineup(snapshot, slots, snapshot.Lineup.Where(x => x.IsStarter).Select(x => x.PlayerId!).ToArray());
            }
            else if (slots.Count == 0) ShowRosterWithoutStarters(snapshot);
        }
        SetResultsHeaders();
        IEnumerable<DisplayRow> source = _pageTag switch
        {
            "My Team" => snapshot.Roster.Select(x => new DisplayRow(x.Player.Name, $"{x.Player.Position} · {x.Player.NflTeam}", x.Status.ToString(), x.Player.Injury ?? "Available")),
            "Players" => WaiverPlayerSelector.Select(snapshot, SelectedRankingPosition, int.MaxValue)
                .Select(x => new DisplayRow(AvailablePlayerSelector.DisplayName(x.Player.Name), x.Player.Position, x.Player.NflTeam, x.PositionRank)),
            "Rankings" => snapshot.WeeklyRankingsReady ? snapshot.Rankings.Where(x => x.Position == SelectedRankingPosition).OrderBy(x => x.PositionRank)
                .Select(x => new DisplayRow($"{x.Position} {x.PositionRank}", x.PlayerName, x.Team, $"{x.Venue} · {x.Opponent}")) : [],
            "Waivers" => snapshot.Waivers.Select(x => new DisplayRow($"Add {x.AddPlayerId}", x.DropPlayerId is null ? "No drop" : $"Drop {x.DropPlayerId}", $"Priority {x.Priority}", x.BidAmount is null ? "Waiver" : $"Bid {x.BidAmount:C}")),
            "Trades" => snapshot.Trades.Select(x => new DisplayRow($"{x.FromFranchiseId} → {x.ToFranchiseId}", $"Offer {x.Offered.Count} / Request {x.Requested.Count}", x.Comments ?? string.Empty, x.ExpiresAt?.ToLocalTime().ToString("g") ?? "Open")),
            "Matchups" => snapshot.Matchups.Select(x => new DisplayRow($"{x.AwayName} at {x.HomeName}", $"Week {x.Week}", $"{x.AwayScore:0.##} – {x.HomeScore:0.##}", x.IsComplete ? "Final" : "Scheduled")),
            "Standings" => snapshot.Standings.Select(x => new DisplayRow($"#{x.Rank} {x.FranchiseName}", $"{x.Wins}-{x.Losses}-{x.Ties}", $"{x.PointsFor:0.##} PF", string.Empty)),
            "Transactions" => TransactionTable.Build(snapshot).Select(x => new DisplayRow(x.TeamName, x.Description, x.BidText, x.DateText)),
            "Messages" => snapshot.Transactions.Select(x => new DisplayRow(x.Type, x.Description, x.FranchiseId ?? string.Empty, x.Timestamp.ToLocalTime().ToString("g"))),
            _ => Dashboard(snapshot)
        };
        if (!string.IsNullOrWhiteSpace(SearchText)) source = source.Where(x => x.Primary.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || x.Secondary.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var row in IsTransactionsPage ? source : source.Take(500)) Rows.Add(row);
        _logger.LogDebug("UI page {Page} displays {RowCount} rows from roster {RosterCount}, players {PlayerCount}, free agents {FreeAgentCount}",
            _pageTag, Rows.Count, snapshot.Roster.Count, snapshot.Players.Count, snapshot.FreeAgents.Count);
    }

    private async Task LoadPlayerAvailabilityAsync(CancellationToken cancellationToken)
    {
        var snapshot = _state.CurrentSnapshot;
        var league = _state.SelectedLeague;
        if (snapshot is null || league is null) return;

        var version = ++_playerAvailabilityLoadVersion;
        _pickupEligiblePlayerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ApplySnapshot(snapshot);
        var week = LineupRules.ActiveWeek(snapshot);
        var result = await _lineups.GetAvailabilityAsync(league, week,
            snapshot.FreeAgents.Select(x => x.Id).ToArray(), cancellationToken);
        if (version != _playerAvailabilityLoadVersion || _state.SelectedLeague != league) return;

        if (!result.IsSuccess)
        {
            StatusMessage = result.UserMessage ?? "Player availability could not be verified with MFL.";
            IsStatusOpen = true;
            ApplySnapshot(snapshot);
            return;
        }

        _pickupEligiblePlayerIds = (result.Value ?? []).Where(x => x.CanAdd).Select(x => x.PlayerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplySnapshot(snapshot);
    }

    private async Task LoadWaiversDataAsync(CancellationToken cancellationToken)
    {
        var league = _state.SelectedLeague;
        if (league is null) return;
        var version = ++_waiversLoadVersion;
        try
        {
            var data = await _waiverData.GetAsync(league, cancellationToken);
            if (version != _waiversLoadVersion || _state.SelectedLeague != league) return;
            if (data.BalanceLoaded)
            {
                _latestFaabBalance = data.FaabBalance;
                _latestFaabFetchedAt = DateTimeOffset.UtcNow;
                FaabBalanceText = data.FaabBalance is { } value ? $"FAAB Balance: {value:C0}" : "FAAB Balance: —";
            }
            if (data.PointsLoaded)
            {
                _seasonPlayerPoints = data.SeasonPlayerPoints;
                if (_state.CurrentSnapshot is { } snapshot)
                {
                    var selectedId = SelectedWaiverPlayer?.Player.Id;
                    WaiverPlayers.Clear();
                    foreach (var player in WaiverPlayerSelector.Select(snapshot, SelectedRankingPosition))
                        WaiverPlayers.Add(player with
                        {
                            SeasonPoints = _seasonPlayerPoints.TryGetValue(player.Player.Id, out var points) ? points : null
                        });
                    SelectedWaiverPlayer = WaiverPlayers.FirstOrDefault(player => player.Player.Id == selectedId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Waivers supplementary data could not be loaded: {ExceptionType}", ex.GetType().Name);
        }
    }

    public async Task ChangeLineupWeekAsync(int week, CancellationToken cancellationToken)
    {
        if (week <= 0 || _suppressLineupWeekSelectionLoad) return;
        SelectedLineupWeek = week;
        await LoadLineupWeekAsync(week, cancellationToken);
    }

    partial void OnSelectedLineupWeekChanged(int value)
    {
        // Invalidate both existing rows and any request still completing for the old selection.
        ++_lineupLoadVersion;
        IsLineupLoading = false;
        ClearLineupPoints();
        if (_state.CurrentSnapshot is { } snapshot) ShowRosterWithoutStarters(snapshot);
    }

    private void ClearLineupPoints()
    {
        _lineupPointsWeek = 0;
        _lineupPlayerPoints = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task SubmitWaiverAsync()
    {
        if (IsBusy) return;
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        var player = SelectedWaiverAddPlayer;
        if (league is null || snapshot is null || player is null || !snapshot.AvailablePlayersReady)
        { ShowWaiverError("Choose an available player and enter a valid nonnegative bid."); return; }
        if (!TryValidateWaiverBid(WaiverBidInput, _latestFaabBalance, out var amount, out var bidError))
        { ShowWaiverError(bidError ?? "Enter a valid bid."); return; }

        IsWaiverErrorOpen = false;
        IsBusy = true;
        MflResult<IReadOnlyList<PlayerRosterAvailability>> availability;
        try
        {
            availability = await _lineups.GetAvailabilityAsync(league, LineupRules.ActiveWeek(snapshot), [player.PlayerId], CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Waiver availability check failed with {ExceptionType}", ex.GetType().Name);
            ShowWaiverError("MFL could not verify that this player can be added to waivers.");
            IsBusy = false;
            return;
        }
        if (!availability.IsSuccess)
        {
            ShowWaiverError(availability.UserMessage ?? "MFL could not verify that this player can be added to waivers.");
            IsBusy = false;
            return;
        }
        var playerAvailability = availability.Value?.SingleOrDefault(candidate =>
            candidate.PlayerId.Equals(player.PlayerId, StringComparison.OrdinalIgnoreCase));
        if (playerAvailability?.CanAdd != true)
        {
            ShowWaiverError("This player cannot be added to waivers right now. Waiver requests may be locked, or the player may no longer be eligible.");
            IsBusy = false;
            return;
        }

        var drop = SelectedWaiverDropPlayer;
        var claim = new WaiverClaim(null, player.PlayerId, string.IsNullOrEmpty(drop?.PlayerId) ? null : drop.PlayerId, 1, amount);
        var mutation = new WaiverMutation([claim], true, true);
        var preview = _mutations.Preview(league, mutation);
        if (preview.Warnings.Count > 0) { ShowWaiverError(string.Join(' ', preview.Warnings)); IsBusy = false; return; }
        var confirmed = !preview.RequiresConfirmation || ConfirmWaiverAsync is not null && await ConfirmWaiverAsync(
            $"Append a {amount:C2} bid for {player.DisplayName}.\nDrop: {drop?.DisplayName ?? "No drop"}.\nExisting pending claims will be preserved.");
        if (!confirmed || _state.SelectedLeague != league) { IsBusy = false; return; }
        try
        {
            var receipt = await _mutations.ExecuteAsync(league, mutation, confirmed, CancellationToken.None);
            if (receipt.Accepted) { StatusMessage = receipt.Message; IsStatusOpen = true; }
            else ShowWaiverError(receipt.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError("Waiver submission interrupted with {ExceptionType}; no automatic retry", ex.GetType().Name);
            ShowWaiverError("Waiver submission could not be verified. Refresh pending claims before trying again.");
        }
        finally { IsBusy = false; }
    }

    private void ShowWaiverError(string message)
    {
        WaiverError = message;
        IsWaiverErrorOpen = true;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private static bool TryValidateWaiverBid(string input, decimal? balance, out decimal amount, out string? error)
    {
        amount = 0;
        error = null;
        if (!string.IsNullOrWhiteSpace(input) &&
            (input.Any(character => character is < '0' or > '9') ||
             !decimal.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out amount)))
        { error = "Enter a whole-dollar, nonnegative bid using digits only."; return false; }
        if (balance is null) { error = "FAAB balance is unavailable. Refresh waivers before bidding."; return false; }
        if (amount > balance) { error = $"Bid exceeds the available FAAB balance of {balance:C0}."; return false; }
        return true;
    }

    private async Task SaveEditedWaiverAsync(PendingWaiverRow row)
    {
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (IsBusy || league is null || snapshot is null) return;
        if (!TryValidateWaiverBid(row.EditBidInput, _latestFaabBalance, out var amount, out var error))
        { ShowWaiverError(error ?? "Enter a valid bid."); return; }
        var original = snapshot.Waivers.Where(claim => claim.Round == row.Claim.Round)
            .OrderBy(claim => claim.Priority).ToArray();
        var index = Array.FindIndex(original, claim => claim == row.Claim);
        if (index < 0) { ShowWaiverError("This claim is no longer pending. Refresh before editing."); return; }
        var edited = row.Claim with
        {
            BidAmount = amount,
            DropPlayerId = string.IsNullOrWhiteSpace(row.SelectedDropPlayer?.PlayerId) ? null : row.SelectedDropPlayer.PlayerId
        };
        if (edited == row.Claim) { row.IsEditing = false; return; }
        var desired = original.ToArray();
        desired[index] = edited;
        var mutation = new EditWaiverClaimMutation(original, desired, row.Claim.Round);
        var preview = _mutations.Preview(league, mutation);
        if (preview.Warnings.Count > 0) { ShowWaiverError(string.Join(' ', preview.Warnings)); return; }
        var confirmed = !preview.RequiresConfirmation || ConfirmWaiverAsync is not null && await ConfirmWaiverAsync(
            $"Change the bid for {row.PlayerName} to {amount:C0} and drop {row.SelectedDropPlayer?.DisplayName ?? "no player"}? Other pending claims will be preserved.");
        if (!confirmed || _state.SelectedLeague != league) return;
        IsBusy = true;
        try
        {
            var receipt = await _mutations.ExecuteAsync(league, mutation, confirmed, CancellationToken.None);
            if (!receipt.Accepted) ShowWaiverError(receipt.Message);
            else
            {
                row.IsEditing = false;
                StatusMessage = receipt.Verified ? "Waiver claim edit saved and verified." : "MFL accepted the edit, but refreshed claims could not verify it.";
                IsStatusOpen = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Waiver edit interrupted with {ExceptionType}; no automatic retry", ex.GetType().Name);
            ShowWaiverError("Waiver edit could not be verified. Refresh pending claims before trying again.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CancelWaiverAsync(WaiverClaim claim)
    {
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (IsBusy || league is null || snapshot is null) return;
        var pending = snapshot.Waivers.ToList();
        if (!pending.Remove(claim))
        {
            StatusMessage = "This waiver claim is no longer pending. Refresh before trying again.";
            IsStatusOpen = true;
            return;
        }
        var remaining = pending.Where(candidate => candidate.Round == claim.Round)
            .OrderBy(candidate => candidate.Priority).ToArray();
        var playerName = PendingWaivers.FirstOrDefault(row => row.Claim == claim)?.PlayerName ?? $"Player {claim.AddPlayerId}";
        var mutation = new CancelWaiverClaimMutation(claim, remaining, claim.BidAmount is not null, claim.Round);
        var preview = _mutations.Preview(league, mutation);
        if (preview.Warnings.Count > 0)
        {
            StatusMessage = string.Join(' ', preview.Warnings);
            IsStatusOpen = true;
            return;
        }
        var detail = claim.BidAmount is { } bid ? $" ({bid:C2})" : string.Empty;
        var confirmed = !preview.RequiresConfirmation || ConfirmCancelWaiverAsync is not null &&
            await ConfirmCancelWaiverAsync($"Cancel the pending waiver claim for {playerName}{detail}?\nOther claims in round {claim.Round} will be preserved.");
        if (!confirmed || _state.SelectedLeague != league) return;
        IsBusy = true;
        try
        {
            var receipt = await _mutations.ExecuteAsync(league, mutation, confirmed, CancellationToken.None);
            StatusMessage = receipt.Accepted && receipt.RefreshSucceeded && !receipt.Verified
                ? "MFL accepted the cancellation, but the refreshed pending claims could not verify it."
                : receipt.Message;
            IsStatusOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Waiver cancellation interrupted with {ExceptionType}; no automatic retry", ex.GetType().Name);
            StatusMessage = "Waiver cancellation could not be verified. Refresh pending claims before trying again.";
            IsStatusOpen = true;
        }
        finally { IsBusy = false; }
    }

    public async Task ReorderPendingWaiversAsync(IReadOnlyList<WaiverClaim> visibleOrder)
    {
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (IsBusy || league is null || snapshot is null) { ApplySnapshot(snapshot); return; }
        var original = snapshot.Waivers.OrderBy(claim => claim.Round).ThenBy(claim => claim.Priority).ToArray();
        if (visibleOrder.Count != original.Length) { ApplySnapshot(snapshot); return; }
        if (visibleOrder.SequenceEqual(original)) return;
        var changedRounds = original.Select(claim => claim.Round).Distinct()
            .Where(round => !visibleOrder.Where(claim => claim.Round == round)
                .SequenceEqual(original.Where(claim => claim.Round == round))).ToArray();
        if (changedRounds.Length != 1 || !visibleOrder.Select(claim => claim.Round)
            .SequenceEqual(original.Select(claim => claim.Round)))
        {
            ApplySnapshot(snapshot);
            ShowWaiverError("Claims can only be reordered within the same waiver round.");
            return;
        }
        var round = changedRounds[0];
        var before = original.Where(claim => claim.Round == round).ToArray();
        var after = visibleOrder.Where(claim => claim.Round == round).ToArray();
        var mutation = new ReorderWaiverClaimsMutation(before, after, before[0].BidAmount is not null, round);
        var preview = _mutations.Preview(league, mutation);
        if (preview.Warnings.Count > 0)
        {
            ApplySnapshot(snapshot);
            ShowWaiverError(string.Join(' ', preview.Warnings));
            return;
        }
        var description = string.Join("\n", after.Select((claim, index) =>
            $"{index + 1}. {PendingWaivers.FirstOrDefault(row => row.Claim == claim)?.PlayerName ?? $"Player {claim.AddPlayerId}"}"));
        var confirmed = !preview.RequiresConfirmation || ConfirmReorderWaiverAsync is not null &&
            await ConfirmReorderWaiverAsync($"Set waiver priority for round {round}:\n{description}\n\nIf claims share a drop player, MFL processes the first available claim first.");
        if (!confirmed || _state.SelectedLeague != league) { ApplySnapshot(_state.CurrentSnapshot); return; }
        IsBusy = true;
        try
        {
            var receipt = await _mutations.ExecuteAsync(league, mutation, confirmed, CancellationToken.None);
            if (!receipt.Accepted || !receipt.Verified) ApplySnapshot(_state.CurrentSnapshot);
            if (!receipt.Accepted) ShowWaiverError(receipt.Message);
            else { StatusMessage = receipt.Verified ? "Waiver priority updated and verified." : "MFL accepted the new priority, but the refreshed order could not be verified."; IsStatusOpen = true; }
        }
        catch (Exception ex)
        {
            _logger.LogError("Waiver reorder interrupted with {ExceptionType}; no automatic retry", ex.GetType().Name);
            ApplySnapshot(_state.CurrentSnapshot);
            ShowWaiverError("Waiver priority could not be verified. Refresh pending claims before trying again.");
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedWaiverPlayerChanged(RankedWaiverPlayer? value)
    {
        if (value is not null) SelectedWaiverAddPlayer = WaiverClaimPlayers.FirstOrDefault(x => x.PlayerId == value.Player.Id);
    }
    partial void OnSelectedRankingPositionChanged(string value) => ApplySnapshot(_state.CurrentSnapshot);

    [RelayCommand]
    private async Task SubmitLineupAsync()
    {
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (league is null || snapshot is null) { ShowLineupError("Select a league and load its roster first."); return; }
        if (LineupSlots.Count == 0) { ShowLineupError("MFL did not return starting-lineup requirements for this league."); return; }
        if (LineupSlots.Any(x => x.SelectedPlayer is null)) { ShowLineupError("Choose a player for every starting slot before submitting."); return; }
        var starterIds = LineupSlots.Select(x => x.SelectedPlayer!.PlayerId).ToArray();
        if (starterIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != starterIds.Length)
        { ShowLineupError("A player can only be assigned to one starting slot."); return; }
        var activeIds = snapshot.Roster.Where(x => x.Status == RosterStatus.Active).Select(x => x.Player.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (starterIds.Any(id => !activeIds.Contains(id))) { ShowLineupError("Every starter must be on this team's active MFL roster."); return; }

        IsBusy = true;
        try
        {
            var receipt = await _mutations.ExecuteAsync(league, new SetLineupMutation(SelectedLineupWeek, starterIds, []), false, CancellationToken.None);
            StatusMessage = receipt.Message;
            IsStatusOpen = true;
            if (!receipt.Accepted) { ShowLineupError(receipt.Message); return; }
            await LoadLineupWeekAsync(SelectedLineupWeek, CancellationToken.None);
        }
        finally { IsBusy = false; }
    }

    private void ConfigureLineupWeeks(LeagueSnapshot snapshot)
    {
        if (snapshot.Settings is not { } settings) return;
        var expected = Enumerable.Range(settings.StartWeek, Math.Max(0, settings.EndWeek - settings.StartWeek + 1)).ToArray();
        if (!LineupWeeks.SequenceEqual(expected))
        {
            LineupWeeks.Clear();
            foreach (var week in expected) LineupWeeks.Add(week);
        }
        if (expected.Length == 0) return;
        var preferred = Math.Clamp(LineupRules.ActiveWeek(snapshot), settings.StartWeek, settings.EndWeek);
        var followsActiveWeek = SelectedLineupWeek <= 0 || _lastObservedActiveLineupWeek <= 0 ||
            SelectedLineupWeek == _lastObservedActiveLineupWeek;
        _lastObservedActiveLineupWeek = preferred;
        if (followsActiveWeek || !expected.Contains(SelectedLineupWeek))
        {
            _suppressLineupWeekSelectionLoad = true;
            try { SelectedLineupWeek = preferred; }
            finally { _suppressLineupWeekSelectionLoad = false; }
        }
    }

    private async Task LoadLineupWeekAsync(int week, CancellationToken cancellationToken)
    {
        if (IsLineupLoading && _lineupLoadingWeek == week) return;
        var loadVersion = ++_lineupLoadVersion;
        _lineupLoadingWeek = week;
        var league = _state.SelectedLeague;
        var snapshot = _state.CurrentSnapshot;
        if (league is null || snapshot is null) { ShowLineupError("Select a league and refresh to load the roster from MFL."); return; }
        ClearLineupPoints();
        ShowRosterWithoutStarters(snapshot);
        if (week <= 0) { ShowLineupError("MFL did not provide a valid lineup week."); return; }
        var definitions = LineupRules.BuildSlots(snapshot.Settings);
        if (definitions.Count == 0) { ShowLineupError("MFL did not return usable starting-lineup requirements for this league."); return; }

        IsLineupLoading = true;
        IsLineupErrorOpen = false;
        try
        {
            var activeRoster = snapshot.Roster.Where(x => x.Status == RosterStatus.Active).ToArray();
            var result = await _lineups.GetAsync(league, week, activeRoster.Select(x => x.Player.Id).ToArray(), cancellationToken);
            if (loadVersion != _lineupLoadVersion || _state.SelectedLeague != league || SelectedLineupWeek != week) return;
            if (!result.IsSuccess || result.Value is null)
            {
                ShowRosterWithoutStarters(snapshot);
                ShowLineupError(result.UserMessage ?? "The starting lineup could not be loaded from MFL.");
                return;
            }
            if (loadVersion == _lineupLoadVersion)
            {
                if (result.Value.Week != week) throw new WeeklyPointsValidationException(week);
                _lineupPointsWeek = result.Value.Week;
                _lineupPlayerPoints = result.Value.PlayerPoints;
                PopulateLineup(snapshot, definitions, result.Value.StarterPlayerIds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WeeklyPointsValidationException ex)
        {
            if (loadVersion != _lineupLoadVersion || _state.SelectedLeague != league || SelectedLineupWeek != week) return;
            ClearLineupPoints();
            ShowRosterWithoutStarters(snapshot);
            ShowLineupError(ex.Message);
        }
        finally { if (loadVersion == _lineupLoadVersion) IsLineupLoading = false; }
    }

    private void ShowRosterWithoutStarters(LeagueSnapshot snapshot)
    {
        LineupSlots.Clear();
        BenchPlayers.Clear();
        foreach (var rosterPlayer in LineupRules.OrderRoster(snapshot.Roster))
            BenchPlayers.Add(new(AvailablePlayerSelector.DisplayName(rosterPlayer.Player.Name), rosterPlayer.Player.Position,
                rosterPlayer.Player.NflTeam, $"{(rosterPlayer.Status == RosterStatus.Active ? "Roster" : rosterPlayer.Status.ToString())}{RosterRank(snapshot, rosterPlayer.Player)}",
                PlayerPoints(rosterPlayer.Player.Id), PlayerAvailability.Code(rosterPlayer.Player, SelectedLineupWeek, snapshot.NflByeWeeks)));
    }

    private void PopulateLineup(LeagueSnapshot snapshot, IReadOnlyList<LineupSlotDefinition> definitions, IReadOnlyList<string> starterIds)
    {
        var activeRoster = snapshot.Roster.Where(x => x.Status == RosterStatus.Active).ToArray();
        var assignments = LineupRules.AssignStarters(snapshot.Settings, snapshot.Roster, starterIds);
        var rows = new List<LineupSlotRow>();
        var editor = new LineupSelectionEditor(rows, UpdateBenchPlayers);
        foreach (var definition in definitions)
        {
            var options = activeRoster.Where(x => definition.EligiblePositions.Contains(LineupRules.NormalizePosition(x.Player.Position)))
                .Select(x => new LineupPlayerOption(x.Player.Id, $"{AvailablePlayerSelector.DisplayName(x.Player.Name)}{RosterRank(snapshot, x.Player)}", $"{x.Player.Position} · {x.Player.NflTeam}", PlayerPoints(x.Player.Id), PlayerAvailability.Code(x.Player, SelectedLineupWeek, snapshot.NflByeWeeks)))
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            rows.Add(new LineupSlotRow(definition.Label, options, editor.OnSelectionChanged));
        }
        for (var index = 0; index < assignments.Count && index < rows.Count; index++)
        {
            var assignedPlayer = assignments[index].Player;
            if (assignedPlayer is not null)
                rows[index].SelectedPlayer = rows[index].EligiblePlayers.FirstOrDefault(x =>
                    x.PlayerId.Equals(assignedPlayer.Player.Id, StringComparison.OrdinalIgnoreCase));
        }
        LineupSlots.Clear();
        foreach (var row in rows)
            LineupSlots.Add(row);
        UpdateBenchPlayers();
        _logger.LogInformation("My Team rendered {SlotCount} lineup slots, {EligiblePlayerCount} eligible options, {BenchCount} bench players",
            LineupSlots.Count, LineupSlots.Sum(x => x.EligiblePlayers.Count), BenchPlayers.Count);
        IsLineupErrorOpen = false;
    }

    private void UpdateBenchPlayers()
    {
        var snapshot = _state.CurrentSnapshot;
        if (snapshot is null) return;
        var selected = LineupSlots.Select(x => x.SelectedPlayer?.PlayerId).Where(x => x is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in LineupSlots.SelectMany(x => x.EligiblePlayers))
            option.IsStarter = selected.Contains(option.PlayerId);
        BenchPlayers.Clear();
        foreach (var rosterPlayer in LineupRules.OrderRoster(snapshot.Roster.Where(x => !selected.Contains(x.Player.Id))))
            BenchPlayers.Add(new(AvailablePlayerSelector.DisplayName(rosterPlayer.Player.Name), rosterPlayer.Player.Position,
                rosterPlayer.Player.NflTeam, $"{(rosterPlayer.Status == RosterStatus.Active ? "Bench" : rosterPlayer.Status.ToString())}{RosterRank(snapshot, rosterPlayer.Player)}",
                PlayerPoints(rosterPlayer.Player.Id), PlayerAvailability.Code(rosterPlayer.Player, SelectedLineupWeek, snapshot.NflByeWeeks)));
    }

    private decimal? PlayerPoints(string playerId) =>
        _lineupPointsWeek == SelectedLineupWeek && _lineupPlayerPoints.TryGetValue(playerId, out var points)
            ? points
            : null;

    private static string RosterRank(LeagueSnapshot snapshot, Player player)
    {
        var identity = WaiverPlayerSelector.Identity(player.Name, player.NflTeam, player.Position);
        var matches = snapshot.Rankings.Where(x => WaiverPlayerSelector.Identity(x.PlayerName, x.Team, x.Position) == identity).Take(2).ToArray();
        return matches.Length == 1 ? $" · {WaiverPlayerSelector.NormalizePosition(player.Position)} {matches[0].PositionRank}{(snapshot.RankingsStale ? " (stale)" : "")}" : "";
    }

    private void ShowLineupError(string message)
    {
        _logger.LogWarning("My Team lineup unavailable; {RosterCount} owned players remain available for roster display",
            _state.CurrentSnapshot?.Roster.Count ?? 0);
        LineupError = message;
        IsLineupErrorOpen = true;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private static IEnumerable<DisplayRow> Dashboard(LeagueSnapshot snapshot)
    {
        var standing = snapshot.Standings.FirstOrDefault(x => x.FranchiseId == snapshot.Context.FranchiseId);
        yield return new("Team record", standing is null ? "Not available" : $"{standing.Wins}-{standing.Losses}-{standing.Ties}", standing is null ? string.Empty : $"Rank #{standing.Rank}", "Current");
        yield return new("Roster", $"{snapshot.Roster.Count} players", $"{snapshot.Roster.Count(x => x.Status == RosterStatus.InjuredReserve)} on IR", "Current");
        yield return new("Pending actions", $"{snapshot.Waivers.Count} waivers", $"{snapshot.Trades.Count} trades", snapshot.Sections.Values.Any(x => x.IsStale) ? "Some data stale" : "Up to date");
        if (snapshot.Draft is not null) yield return new("Draft", $"Round {snapshot.Draft.Round}, pick {snapshot.Draft.Pick}", $"On clock: {snapshot.Draft.OnTheClockFranchiseId}", snapshot.Draft.IsPaused ? "Paused" : "Live");
    }

    private void UpdateDraftBoard(LeagueSnapshot snapshot)
    {
        var players = snapshot.Players.Concat(snapshot.FreeAgents).Concat(snapshot.Roster.Select(x => x.Player))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var franchises = snapshot.Franchises.ToDictionary(x => x.Id, x => x.Name, StringComparer.OrdinalIgnoreCase);
        var picks = snapshot.Draft?.Picks
            .Select((pick, index) =>
            {
                var overall = pick.Overall > 0 ? pick.Overall : index + 1;
                var round = pick.Round > 0 ? pick.Round : ((overall - 1) / 12) + 1;
                var withinRound = pick.Pick > 0 ? pick.Pick : ((overall - 1) % 12) + 1;
                return (pick, round, withinRound);
            })
            .GroupBy(x => (x.round, x.withinRound))
            .ToDictionary(x => x.Key, x => x.Last()) ?? [];
        var draftedIds = snapshot.Draft?.Picks.Select(x => x.PlayerId).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var positionalRanks = snapshot.WeeklyRankingsReady ? snapshot.Rankings
            .GroupBy(x => WaiverPlayerSelector.Identity(x.PlayerName, x.Team, x.Position))
            .Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single().PositionRank) : [];
        var availablePlayers = snapshot.Players
            .Where(x => !draftedIds.Contains(x.Id) && WaiverPlayerSelector.NormalizePosition(x.Position) == SelectedRankingPosition)
            .Select(player => new
            {
                Player = player,
                PositionRank = positionalRanks.GetValueOrDefault(WaiverPlayerSelector.Identity(player.Name, player.NflTeam, player.Position), int.MaxValue)
            })
            .ToArray();
        var hasAdp = availablePlayers.Any(x => x.Player.AverageDraftPosition is not null);
        var hasRankings = availablePlayers.Any(x => x.PositionRank < int.MaxValue);
        AvailablePlayersTitle = hasAdp ? "Available by MFL ADP" : hasRankings ? "Available by Fantasy Footballers position rank" : "Available players — ranking unavailable";
        AvailableRankHeader = hasAdp ? "ADP" : "Position rank";
        var available = availablePlayers
            .OrderBy(x => hasAdp ? x.Player.AverageDraftPosition ?? decimal.MaxValue : x.PositionRank)
            .ThenBy(x => x.Player.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(192)
            .ToArray();

        DraftBoard.Clear();
        for (var index = 0; index < 192; index++)
        {
            var round = (index / 12) + 1;
            var withinRound = (index % 12) + 1;
            var order = $"{round}.{withinRound}";
            var draftedName = string.Empty;
            var draftedPosition = string.Empty;
            var draftedTeam = string.Empty;
            var fantasyTeam = string.Empty;
            if (picks.TryGetValue((round, withinRound), out var selected))
            {
                if (selected.pick.PlayerId is not null && players.TryGetValue(selected.pick.PlayerId, out var player))
                {
                    draftedName = player.Name;
                    draftedPosition = player.Position;
                    draftedTeam = player.NflTeam;
                }
                else draftedName = "Player details unavailable";
                fantasyTeam = franchises.GetValueOrDefault(selected.pick.FranchiseId, "Team unavailable");
            }
            var candidate = available.ElementAtOrDefault(index);
            var availableRank = hasAdp
                ? candidate?.Player.AverageDraftPosition?.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty
                : hasRankings && candidate?.PositionRank < int.MaxValue ? $"{candidate.Player.Position} {candidate.PositionRank}" : string.Empty;
            DraftBoard.Add(new DraftBoardRow(
                order,
                draftedName,
                draftedPosition,
                draftedTeam,
                fantasyTeam,
                availableRank,
                candidate?.Player.Name ?? string.Empty,
                candidate?.Player.Position ?? string.Empty,
                candidate?.Player.NflTeam ?? string.Empty));
        }
    }

    private static string NormalizePlayerName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private void SetResultsHeaders()
    {
        (ResultsPrimaryHeader, ResultsSecondaryHeader, ResultsTertiaryHeader, ResultsStatusHeader) = _pageTag switch
        {
            "Draft" => ("Round / pick", "Player", "Position", "Team"),
            "Players" => ("Player name", "Position", "NFL team", "Position rank"),
            "Rankings" => ("Position rank", "Player name", "NFL team", "Opponent"),
            "Transactions" => ("Team", "Transaction", "Bid amount", "Date / time"),
            _ => ("Column 1", "Column 2", "Column 3", "Status / value")
        };
    }

    private void UpdateDraftQueueRows(LeagueSnapshot snapshot)
    {
        var players = snapshot.Players.Concat(snapshot.FreeAgents).Concat(snapshot.Roster.Select(x => x.Player))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        DraftQueue.Clear();
        foreach (var (id, index) in _draftQueueIds.Select((id, index) => (id, index)))
        {
            if (players.TryGetValue(id, out var player))
                DraftQueue.Add(new DraftQueueRow(index + 1, player.Name, player.Position, player.NflTeam));
            else
                DraftQueue.Add(new DraftQueueRow(index + 1, "Player details unavailable", "—", "—"));
        }
        DraftQueueHead = DraftQueue.FirstOrDefault()?.PlayerName ?? "No players queued";
    }

    private async Task LoadHistoryAsync()
    {
        var league = _state.SelectedLeague; if (league is null) return;
        var entries = await _journal.GetRecentAsync(league.LeagueId, 100, CancellationToken.None);
        AgentHistory.Clear(); foreach (var entry in entries) AgentHistory.Add(entry);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? Environment.CurrentDirectory;
    }
}
