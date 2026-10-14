using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MflManager.App.ViewModels;

public partial class LineupPlayerOption(string playerId, string displayName, string details, decimal? points = null, string statusCode = "") : ObservableObject
{
    public string PlayerId { get; } = playerId;
    public string DisplayName { get; } = displayName;
    public string Details { get; } = details;
    public decimal? Points { get; } = points;
    public string StatusCode { get; } = statusCode;
    [ObservableProperty] public partial bool IsStarter { get; set; }
    public string PointsText => Points?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—";
    public override string ToString() => DisplayName;
}

public partial class LineupSlotRow : ObservableObject
{
    private readonly Action<LineupSlotRow, LineupPlayerOption?, LineupPlayerOption?> _selectionChanged;
    public string Label { get; }
    public string AutomationId => $"Lineup{Label}Selector";
    public string AccessibleName => $"{Label} starter";
    public IReadOnlyList<LineupPlayerOption> EligiblePlayers { get; }
    [ObservableProperty] public partial LineupPlayerOption? SelectedPlayer { get; set; }

    public LineupSlotRow(string label, IReadOnlyList<LineupPlayerOption> eligiblePlayers,
        Action<LineupSlotRow, LineupPlayerOption?, LineupPlayerOption?> selectionChanged)
    {
        Label = label;
        EligiblePlayers = eligiblePlayers;
        _selectionChanged = selectionChanged;
    }

    partial void OnSelectedPlayerChanged(LineupPlayerOption? oldValue, LineupPlayerOption? newValue) =>
        _selectionChanged(this, oldValue, newValue);
}

public sealed class LineupSelectionEditor(IReadOnlyList<LineupSlotRow> slots, Action selectionChanged)
{
    private bool _isSwapping;

    public void OnSelectionChanged(LineupSlotRow target, LineupPlayerOption? previous, LineupPlayerOption? selected)
    {
        if (_isSwapping || !slots.Contains(target)) return;
        _isSwapping = true;
        try
        {
            if (selected is not null)
            {
                var source = slots.FirstOrDefault(slot => !ReferenceEquals(slot, target) &&
                    string.Equals(slot.SelectedPlayer?.PlayerId, selected.PlayerId, StringComparison.OrdinalIgnoreCase));
                if (source is not null)
                {
                    // Use the source selector's own option instance; a FLEX replacement
                    // may not be eligible for the player's former positional slot.
                    source.SelectedPlayer = previous is null ? null : source.EligiblePlayers.FirstOrDefault(option =>
                        option.PlayerId.Equals(previous.PlayerId, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            _isSwapping = false;
        }
        selectionChanged();
    }
}
