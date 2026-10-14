using MflManager.Domain;

namespace MflManager.Application;

public sealed class LeagueState : ILeagueState
{
    private readonly object _gate = new();
    private LeagueContext? _selectedLeague;
    private LeagueSnapshot? _snapshot;

    public LeagueContext? SelectedLeague { get { lock (_gate) return _selectedLeague; } }
    public LeagueSnapshot? CurrentSnapshot { get { lock (_gate) return _snapshot; } }

    public event EventHandler<LeagueContext?>? LeagueChanged;
    public event EventHandler<LeagueSnapshot?>? SnapshotChanged;

    public void SelectLeague(LeagueContext? context)
    {
        lock (_gate)
        {
            _selectedLeague = context;
            _snapshot = null;
        }
        LeagueChanged?.Invoke(this, context);
        SnapshotChanged?.Invoke(this, null);
    }

    public void UpdateSnapshot(LeagueSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_selectedLeague != snapshot.Context)
                return;
            _snapshot = snapshot;
        }
        SnapshotChanged?.Invoke(this, snapshot);
    }
}

public sealed class ConfirmationPolicy : IConfirmationPolicy
{
    private readonly HashSet<MutationKind> _disabled = [];

    public bool RequiresConfirmation(MutationKind kind) => !_disabled.Contains(kind) && kind is
        MutationKind.SubmitDraftPick or MutationKind.AddDrop or MutationKind.SubmitWaivers or
        MutationKind.SubmitBlindBids or MutationKind.CancelWaiverClaim or MutationKind.ReorderWaiverClaims or MutationKind.EditWaiverClaim or MutationKind.ProposeTrade or MutationKind.RespondToTrade or
        MutationKind.CommissionerDraftControl or MutationKind.CommissionerAdjustment;

    public void SetEnabled(MutationKind kind, bool enabled)
    {
        if (enabled) _disabled.Remove(kind); else _disabled.Add(kind);
    }
}
