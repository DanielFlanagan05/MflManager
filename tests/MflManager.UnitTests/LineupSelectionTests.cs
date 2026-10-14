using MflManager.App.ViewModels;

namespace MflManager.UnitTests;

public sealed class LineupSelectionTests
{
    [Fact]
    public void SelectingAnExistingStarterSwapsUsingEachSlotsOwnOptions()
    {
        var (slots, changes) = CreateLineup(["A", "B", "C"], ["a", "b", "c"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        slots[1].SelectedPlayer = slots[1].EligiblePlayers[1];
        var before = changes();

        slots[1].SelectedPlayer = slots[1].EligiblePlayers[0];

        Assert.Same(slots[0].EligiblePlayers[1], slots[0].SelectedPlayer);
        Assert.Same(slots[1].EligiblePlayers[0], slots[1].SelectedPlayer);
        Assert.Equal(before + 1, changes());
        AssertUnique(slots);

        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        Assert.Same(slots[1].EligiblePlayers[1], slots[1].SelectedPlayer);
        AssertUnique(slots);
    }

    [Fact]
    public void SelectingABenchPlayerOnlyReplacesTheTargetSlot()
    {
        var (slots, _) = CreateLineup(["A", "B", "C"], ["A", "B", "C"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        slots[1].SelectedPlayer = slots[1].EligiblePlayers[1];

        slots[1].SelectedPlayer = slots[1].EligiblePlayers[2];

        Assert.Equal("A", slots[0].SelectedPlayer?.PlayerId);
        Assert.Equal("C", slots[1].SelectedPlayer?.PlayerId);
        AssertUnique(slots);
    }

    [Fact]
    public void MovingAStarterToAnEmptySlotClearsTheOriginalSlot()
    {
        var (slots, _) = CreateLineup(["A", "B"], ["A", "B"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];

        slots[1].SelectedPlayer = slots[1].EligiblePlayers[0];

        Assert.Null(slots[0].SelectedPlayer);
        Assert.Equal("A", slots[1].SelectedPlayer?.PlayerId);
        AssertUnique(slots);
    }

    [Fact]
    public void FlexSwapClearsTheOriginalSlotWhenDisplacedPlayerIsIneligible()
    {
        var (slots, _) = CreateLineup(["RB"], ["RB", "WR"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        slots[1].SelectedPlayer = slots[1].EligiblePlayers[1];

        slots[1].SelectedPlayer = slots[1].EligiblePlayers[0];

        Assert.Null(slots[0].SelectedPlayer);
        Assert.Equal("RB", slots[1].SelectedPlayer?.PlayerId);
        AssertUnique(slots);
    }

    [Fact]
    public void EligibleFlexReplacementSwapsBackToTheOriginalSlot()
    {
        var (slots, _) = CreateLineup(["RB", "RB2"], ["RB", "RB2", "WR"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        slots[1].SelectedPlayer = slots[1].EligiblePlayers[1];

        slots[1].SelectedPlayer = slots[1].EligiblePlayers[0];

        Assert.Same(slots[0].EligiblePlayers[1], slots[0].SelectedPlayer);
        Assert.Equal("RB", slots[1].SelectedPlayer?.PlayerId);
        AssertUnique(slots);
    }

    [Fact]
    public void ClearingASelectionLeavesOtherStartersAlone()
    {
        var (slots, _) = CreateLineup(["A", "B"], ["A", "B"]);
        slots[0].SelectedPlayer = slots[0].EligiblePlayers[0];
        slots[1].SelectedPlayer = slots[1].EligiblePlayers[1];

        slots[1].SelectedPlayer = null;

        Assert.Equal("A", slots[0].SelectedPlayer?.PlayerId);
        Assert.Null(slots[1].SelectedPlayer);
    }

    private static (List<LineupSlotRow> Slots, Func<int> Changes) CreateLineup(params string[][] eligibleIds)
    {
        var slots = new List<LineupSlotRow>();
        var changes = 0;
        var editor = new LineupSelectionEditor(slots, () =>
        {
            AssertUnique(slots);
            changes++;
        });
        foreach (var ids in eligibleIds)
            slots.Add(new LineupSlotRow($"Slot{slots.Count}",
                ids.Select(id => new LineupPlayerOption(id, id, "")).ToArray(), editor.OnSelectionChanged));
        return (slots, () => changes);
    }

    private static void AssertUnique(IEnumerable<LineupSlotRow> slots)
    {
        var selectedIds = slots.Where(slot => slot.SelectedPlayer is not null)
            .Select(slot => slot.SelectedPlayer!.PlayerId).ToArray();
        Assert.Equal(selectedIds.Length, selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
