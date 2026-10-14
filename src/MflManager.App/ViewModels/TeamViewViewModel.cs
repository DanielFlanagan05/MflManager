using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.Logging;

namespace MflManager.App.ViewModels;

public partial class TeamViewViewModel(
    ITeamViewService teamViews,
    ILogger<TeamViewViewModel> logger) : ObservableObject
{
    private int _loadVersion;

    public ObservableCollection<TeamPlayerRow> Starters { get; } = [];
    public ObservableCollection<TeamPlayerRow> Bench { get; } = [];

    [ObservableProperty] public partial string TeamName { get; set; } = string.Empty;
    [ObservableProperty] public partial string BackLabel { get; set; } = "Back";
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial string Error { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public async Task LoadAsync(
        LeagueRankingRow team,
        LeagueContext league,
        LeagueSnapshot snapshot,
        int fallbackWeek,
        string returnPageName,
        CancellationToken cancellationToken)
    {
        var loadVersion = ++_loadVersion;
        TeamName = team.TeamName;
        BackLabel = $"Back to {returnPageName}";
        Status = "Loading current roster, lineup, and player points…";
        Error = string.Empty;
        IsErrorOpen = false;
        IsLoading = true;
        Starters.Clear();
        Bench.Clear();

        var players = snapshot.Players.Concat(snapshot.FreeAgents).Concat(snapshot.Roster.Select(x => x.Player))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await teamViews.GetAsync(
                league, team.FranchiseId, fallbackWeek, players, cancellationToken);
            if (loadVersion != _loadVersion) return;
            if (!result.IsSuccess || result.Value is null)
            {
                Error = result.UserMessage ?? "The selected team could not be loaded.";
                IsErrorOpen = true;
                Status = "Team data unavailable.";
                logger.LogWarning("Team view load failed for franchise {FranchiseId}, correlation {CorrelationId}",
                    team.FranchiseId, result.CorrelationId);
                return;
            }

            var rows = TeamViewTable.Build(snapshot.Settings, result.Value);
            foreach (var row in rows.Starters) Starters.Add(row);
            foreach (var row in rows.Bench) Bench.Add(row);
            Status = $"Week {result.Value.Week} · {Starters.Count} lineup slots · {Bench.Count} bench and reserve players";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError("Team view load aborted for franchise {FranchiseId} with {ExceptionType}",
                team.FranchiseId, ex.GetType().Name);
            Error = "The selected team could not be loaded. Details were recorded in local logs.";
            IsErrorOpen = true;
            Status = "Team data unavailable.";
        }
        finally
        {
            if (loadVersion == _loadVersion) IsLoading = false;
        }
    }
}
