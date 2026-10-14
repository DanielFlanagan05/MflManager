namespace MflManager.UiTests;

public sealed class UiContractTests
{
    [Fact]
    public void ActiveRankings_HaveNoLegacyApiOrKeyDependency()
    {
        var root = FindRepositoryRoot();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            Assert.DoesNotContain("Fantasy" + "Pros", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "MainPage.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"RankingPositionFilter\"", xaml);
    }
    [Fact]
    public void Waivers_HaveAlignedPairsAndBidEntryControls()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MflManager.App", "MainPage.xaml"));
        foreach (var id in new[] { "WaiversPanel", "PendingWaiverClaims", "RankedWaiverPlayers", "WaiverBidAmount", "WaiverDropPlayer", "SubmitWaiverBid" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml);
        Assert.Contains("x:DataType=\"viewModels:PendingWaiverRow\"", xaml);
        Assert.Contains("x:DataType=\"application:RankedWaiverPlayer\"", xaml);
        Assert.Contains("x:Name=\"CancelWaiverButton\"", xaml);
        Assert.Contains("Source=\"/Assets/TrashIcon.jpg\"", xaml);
        Assert.Contains("Stretch=\"Uniform\"", xaml);
        Assert.Contains("Command=\"{x:Bind CancelCommand}\"", xaml);
        Assert.Contains("PointerEntered=\"PendingWaiverRow_PointerEntered\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Cancel waiver claim\"", xaml);
        Assert.Contains("Text=\"Copies\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WaiverCopiesHeader\"", xaml);
        Assert.Contains("Text=\"{x:Bind CopiesText, Mode=OneWay}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WaiverProcessingDateHeader\"", xaml);
        Assert.Contains("Text=\"{x:Bind ProcessingDate, Mode=OneWay}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WaiverSubmissionError\"", xaml);
        Assert.Contains("Severity=\"Error\"", xaml);
        foreach (var id in new[] { "FaabBalance", "WaiverSeasonPointsHeader", "EditWaiverDropPlayer", "EditWaiverBidAmount", "EditWaiverClaim", "SaveWaiverClaim", "CancelWaiverEdit" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml);
        Assert.Contains("BeforeTextChanging=\"IntegerBid_BeforeTextChanging\"", xaml);
    }
    [Fact]
    public void MainPage_DefinesCriticalAutomationIdsAndNavigation()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "MainPage.xaml"));
        foreach (var id in new[] { "PrimaryNavigation", "LeagueSelector", "GlobalPlayerSearch", "CodexToolbarAccount", "CodexAccountEmail", "RefreshAllButton", "ResultsHeader", "CurrentPageResults", "MyTeamLineupPanel", "LineupWeekSelector", "SubmitLineupButton", "LineupLoadError", "StartAgentButton", "CodexSignInButton", "CodexSignOutButton", "CodexAccountRefreshButton", "CodexTerminal", "CodexTerminalInput", "SendTerminalInput", "AgentActionHistory" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BothBoolToVisibility(ViewModel.IsAgentPage, ViewModel.IsCodexAuthenticated)", xaml, StringComparison.Ordinal);
        Assert.Contains("InvertBoolToVisibility(ViewModel.IsCodexAuthenticated)", xaml, StringComparison.Ordinal);
        Assert.Contains("BoolToVisibility(ViewModel.IsCodexAuthenticated)", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Starting lineup\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Bench\"", xaml, StringComparison.Ordinal);
        foreach (var id in new[] { "MyTeamStarterStatusHeader", "MyTeamBenchStatusHeader", "MyTeamStarterPointsHeader", "MyTeamBenchPointsHeader" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedPlayer.StatusCode", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Bind StatusCode", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedPlayer.PointsText", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Bind PointsText, Mode=OneWay", xaml, StringComparison.Ordinal);
        foreach (var page in new[] { "Dashboard", "My Team", "Players", "Rankings", "Draft", "Waivers", "Trades", "Matchups", "League Rankings", "Standings", "Transactions", "Messages", "Agent" })
            Assert.Contains($"Tag=\"{page}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Transactions_HasDedicatedFourColumnView()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MflManager.App", "MainPage.xaml"));
        Assert.Contains("Content=\"Transactions\" Tag=\"Transactions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"TransactionsHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationId=\"TransactionsList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Bid amount\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Date / time\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LeagueRankings_HasScopedRefreshAndFourAlignedColumns()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MflManager.App", "MainPage.xaml"));
        foreach (var id in new[] { "NavLeagueRankings", "LeagueRankingsPanel", "RefreshLeagueRankingsButton", "LeagueRankingsStatus", "LeagueRankingsHeader", "LeagueRankingsList" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"application:LeagueRankingRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Current week points\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Overall points\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"LeagueRankingsList_SelectionChanged\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamView_IsReusableReadOnlyLineupWithBackNavigationAndPoints()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "MainPage.xaml"));
        var teamXaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "Views", "TeamView.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"TeamViewPanel\"", mainXaml, StringComparison.Ordinal);
        foreach (var id in new[] { "TeamViewBackButton", "TeamViewStarters", "TeamViewBench", "TeamViewLoadError" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", teamXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Starting lineup\"", teamXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Bench\"", teamXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Current week points\"", teamXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Submit lineup", teamXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launcher_PreservesSelfContainedProjectDefault()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "run-mflmanager.ps1"));
        Assert.DoesNotContain("SelfContained=false", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOTNET_ROOT", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SelfContained=true", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreDataPanels_StayMaterializedAndToggleVisibility()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MflManager.App", "MainPage.xaml"));
        foreach (var name in new[] { "MyTeamPanel", "LeagueRankingsPanel", "SelectedTeamView", "DataResults", "ResultsHeader", "DraftBoardPanel", "AgentPanel" })
            Assert.Contains($"x:Name=\"{name}\" Visibility=", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeResources_DefineAllThreeThemes()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "Themes", "ThemeResources.xaml"));
        Assert.Contains("x:Key=\"Light\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Dark\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HighContrast\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MflManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
