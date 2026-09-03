using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MflManager.App;
using MflManager.Application;
using MflManager.Domain;
using MflManager.Terminal;

namespace MflManager.App.ViewModels;

public sealed record DisplayRow(string Primary, string Secondary, string Tertiary, string Status);
public sealed record DraftQueueRow(int Order, string PlayerName, string Position, string Team);
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
    private readonly WindowsCredentialLoginStore _windowsCredentialLogin;
    private int _lastAgentPromptOverall;
    private string _pageTag = "Dashboard";
    private readonly List<string> _draftQueueIds = [];
    private bool _queueHasLocalChanges;

    public ObservableCollection<LeagueSummary> Leagues { get; } = [];
    public ObservableCollection<DisplayRow> Rows { get; } = [];
    public ObservableCollection<DraftQueueRow> DraftQueue { get; } = [];
    public ObservableCollection<DraftBoardRow> DraftBoard { get; } = [];
    public ObservableCollection<OperationJournalEntry> AgentHistory { get; } = [];

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
    [ObservableProperty] public partial string LastRefresh { get; set; } = "Never";
    [ObservableProperty] public partial bool IsAgentPage { get; set; }
    [ObservableProperty] public partial bool IsDraftPage { get; set; }
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

    public MainPageViewModel(IMflAuthenticationClient authentication, IMflLeagueDiscoveryClient discovery,
        IMflRefreshService refresh, ILeagueState state, ISecureSessionStore session,
        IOperationJournal journal, ICodexTerminalService terminal, IMflMutationCoordinator mutations, WindowsCredentialLoginStore windowsCredentialLogin)
    {
        _authentication = authentication;
        _discovery = discovery;
        _refresh = refresh;
        _state = state;
        _session = session;
        _journal = journal;
        _terminal = terminal;
        _mutations = mutations;
        _windowsCredentialLogin = windowsCredentialLogin;
        _state.SnapshotChanged += (_, snapshot) => App.DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
        _terminal.OutputReceived += (_, output) => App.DispatcherQueue.TryEnqueue(() => TerminalText += output.Text);
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
            if (!signedIn) { StatusMessage = "Ready"; IsStatusOpen = false; }
        }
        catch (Exception) { StatusMessage = "Ready"; IsStatusOpen = false; }
    }

    public async Task SelectLeagueAsync(LeagueSummary league, CancellationToken cancellationToken)
    {
        SelectedLeague = league;
        var context = new LeagueContext(league.LeagueId,
            league.Season == DateTimeOffset.Now.Year ? SeasonContext.Current(league.Season) : SeasonContext.Historical(league.Season),
            league.Host, league.FranchiseId, league.IsCommissioner);
        _state.SelectLeague(context);
        await RefreshCoreAsync(RefreshReason.LeagueChanged, cancellationToken);
    }

    [RelayCommand] private async Task RefreshAsync() => await RefreshCoreAsync(RefreshReason.Manual, CancellationToken.None);

    private async Task RefreshCoreAsync(RefreshReason reason, CancellationToken cancellationToken)
    {
        var league = _state.SelectedLeague;
        if (league is null) { StatusMessage = "Select a league first."; IsStatusOpen = true; return; }
        IsBusy = true; StatusMessage = "Refreshing every applicable league section…"; IsStatusOpen = true;
        try
        {
            var report = await _refresh.RefreshAllAsync(league, reason, cancellationToken);
            LastRefresh = report.CompletedAt.ToLocalTime().ToString("g");
            StatusMessage = report.IsSuccess ? "Refresh complete." : report.IsPartial ? "Refresh completed with stale sections; last-good data was preserved." : "Refresh failed; cached data is still visible.";
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
            await SendTerminalAsync("Draft queue assistant instructions: when I ask you to fill or suggest a draft queue, use mfl_get_draft_state, mfl_get_roster, mfl_search_players/mfl_get_free_agents, and available FantasyPros rankings. Default to 5 players unless I specify another count. Honor requested position priorities, liked players, and disliked players (dislikes are hard exclusions). Remove drafted/unavailable players, preserve order, explain briefly, and call mfl_set_draft_queue with the IDs only after presenting a sanitized preview. Never submit a draft pick unless I explicitly ask and the app confirmation policy allows it.", CancellationToken.None);
            await LoadHistoryAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { StatusMessage = "Codex could not start. Verify the Codex CLI is installed and signed in."; IsStatusOpen = true; }
    }

    [RelayCommand] private async Task StopAgentAsync() => await _terminal.StopAsync(CancellationToken.None);
    [RelayCommand] private async Task RestartAgentAsync() { await _terminal.StopAsync(CancellationToken.None); await StartAgentAsync(); }

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
        try { await _terminal.WriteAsync(text + Environment.NewLine, cancellationToken); }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; IsStatusOpen = true; }
    }

    public void SetPage(string tag)
    {
        _pageTag = tag;
        PageTitle = tag;
        IsAgentPage = tag == "Agent";
        IsDraftPage = tag == "Draft";
        IsDataPage = !IsAgentPage;
        IsStandardDataPage = !IsAgentPage && !IsDraftPage;
        ApplySnapshot(_state.CurrentSnapshot);
    }
    public void Filter(string text) { SearchText = text; ApplySnapshot(_state.CurrentSnapshot); }

    private void ApplySnapshot(LeagueSnapshot? snapshot)
    {
        Rows.Clear();
        if (snapshot is null) { PageSubtitle = "Select a league and refresh to load data."; return; }
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
        if (IsDraftAgentEnabled && snapshot.Draft is { } draft && draft.OnTheClockFranchiseId == snapshot.Context.FranchiseId && draft.Overall != _lastAgentPromptOverall)
        {
            _lastAgentPromptOverall = draft.Overall;
            _ = SendTerminalAsync($"You are the draft agent. It is our turn at overall pick {draft.Overall}. Analyze available players using FantasyPros expert consensus rankings, positional scarcity, and our current roster needs. Prefer the top available player that fills the biggest need; do not select an already drafted player. Use mfl_get_draft_state and mfl_get_roster, then prepare the best mfl_submit_draft_pick. The app's confirmation policy still applies.", CancellationToken.None);
        }
        PageSubtitle = $"{snapshot.Settings?.Name ?? snapshot.Context.LeagueId} · {snapshot.Context.Season.Year}" + (snapshot.Context.Season.IsReadOnly ? " · Historical read-only" : string.Empty);
        SetResultsHeaders();
        IEnumerable<DisplayRow> source = _pageTag switch
        {
            "My Team" => snapshot.Roster.Select(x => new DisplayRow(x.Player.Name, $"{x.Player.Position} · {x.Player.NflTeam}", x.Status.ToString(), x.Player.Injury ?? "Available")),
            "Players" => snapshot.Players.Select(x => new DisplayRow(x.NflTeam, x.Position, x.Name, x.Injury ?? x.Status ?? "Available")),
            "Rankings" => snapshot.Rankings.Select(x => new DisplayRow($"#{x.Rank}", x.Position, x.PlayerName, string.IsNullOrWhiteSpace(x.Team) ? "" : x.Team)),
            "Waivers" => snapshot.Waivers.Select(x => new DisplayRow($"Add {x.AddPlayerId}", x.DropPlayerId is null ? "No drop" : $"Drop {x.DropPlayerId}", $"Priority {x.Priority}", x.BidAmount is null ? "Waiver" : $"Bid {x.BidAmount:C}")),
            "Trades" => snapshot.Trades.Select(x => new DisplayRow($"{x.FromFranchiseId} → {x.ToFranchiseId}", $"Offer {x.Offered.Count} / Request {x.Requested.Count}", x.Comments ?? string.Empty, x.ExpiresAt?.ToLocalTime().ToString("g") ?? "Open")),
            "Matchups" => snapshot.Matchups.Select(x => new DisplayRow($"{x.AwayName} at {x.HomeName}", $"Week {x.Week}", $"{x.AwayScore:0.##} – {x.HomeScore:0.##}", x.IsComplete ? "Final" : "Scheduled")),
            "Standings" => snapshot.Standings.Select(x => new DisplayRow($"#{x.Rank} {x.FranchiseName}", $"{x.Wins}-{x.Losses}-{x.Ties}", $"{x.PointsFor:0.##} PF", string.Empty)),
            "League" or "Messages" => snapshot.Transactions.Select(x => new DisplayRow(x.Type, x.Description, x.FranchiseId ?? string.Empty, x.Timestamp.ToLocalTime().ToString("g"))),
            _ => Dashboard(snapshot)
        };
        if (!string.IsNullOrWhiteSpace(SearchText)) source = source.Where(x => x.Primary.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || x.Secondary.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var row in source.Take(500)) Rows.Add(row);
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
        var fantasyProsRanks = snapshot.Rankings
            .Where(x => x.Rank < int.MaxValue)
            .GroupBy(x => NormalizePlayerName(x.PlayerName))
            .ToDictionary(x => x.Key, x => x.Min(r => r.Rank), StringComparer.OrdinalIgnoreCase);
        var availablePlayers = snapshot.Players
            .Where(x => !draftedIds.Contains(x.Id))
            .Select(player => new
            {
                Player = player,
                FantasyProsRank = fantasyProsRanks.GetValueOrDefault(NormalizePlayerName(player.Name), int.MaxValue)
            })
            .ToArray();
        var hasAdp = availablePlayers.Any(x => x.Player.AverageDraftPosition is not null);
        var hasFantasyPros = availablePlayers.Any(x => x.FantasyProsRank < int.MaxValue);
        AvailablePlayersTitle = hasAdp ? "Available by MFL ADP" : hasFantasyPros ? "Available by FantasyPros ECR" : "Available players — ranking unavailable";
        AvailableRankHeader = hasAdp ? "ADP" : hasFantasyPros ? "ECR" : "Rank";
        var available = availablePlayers
            .OrderBy(x => hasAdp ? x.Player.AverageDraftPosition ?? decimal.MaxValue : x.FantasyProsRank)
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
                : hasFantasyPros && candidate?.FantasyProsRank < int.MaxValue ? $"#{candidate.FantasyProsRank}" : string.Empty;
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
            "Players" => ("Team / City", "Position", "Player name", "Status / value"),
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
