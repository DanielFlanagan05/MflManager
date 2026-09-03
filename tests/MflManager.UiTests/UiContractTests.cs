namespace MflManager.UiTests;

public sealed class UiContractTests
{
    [Fact]
    public void MainPage_DefinesCriticalAutomationIdsAndNavigation()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MflManager.App", "MainPage.xaml"));
        foreach (var id in new[] { "PrimaryNavigation", "LeagueSelector", "GlobalPlayerSearch", "RefreshAllButton", "ResultsHeader", "CurrentPageResults", "StartAgentButton", "CodexTerminal", "CodexTerminalInput", "SendTerminalInput", "AgentActionHistory" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Team / City\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Position\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Player name\"", xaml, StringComparison.Ordinal);
        foreach (var page in new[] { "Dashboard", "My Team", "Players", "Draft", "Waivers", "Trades", "Matchups", "Standings", "League", "Messages", "Agent" })
            Assert.Contains($"Tag=\"{page}\"", xaml, StringComparison.Ordinal);
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
