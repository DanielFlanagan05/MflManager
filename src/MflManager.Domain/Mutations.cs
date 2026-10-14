namespace MflManager.Domain;

public enum MutationKind
{
    SetLineup,
    AddDrop,
    SubmitWaivers,
    SubmitBlindBids,
    CancelWaiverClaim,
    ReorderWaiverClaims,
    EditWaiverClaim,
    MoveToInjuredReserve,
    MoveFromInjuredReserve,
    PromoteTaxiPlayer,
    DemoteTaxiPlayer,
    SetKeepers,
    SetWatchList,
    SetDraftQueue,
    SubmitDraftPick,
    ProposeTrade,
    RespondToTrade,
    SetTradeBait,
    PostMessage,
    RespondToPoll,
    CommissionerDraftControl,
    CommissionerAdjustment
}

public abstract record MflMutation(MutationKind Kind, bool RequiresConfirmation = true)
{
    public abstract IReadOnlyDictionary<string, string> ToParameters();
}

public sealed record SetLineupMutation(int Week, IReadOnlyList<string> Starters, IReadOnlyList<string> Tiebreakers)
    : MflMutation(MutationKind.SetLineup, false)
{
    public override IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["TYPE"] = "lineup",
        ["W"] = Week.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["STARTERS"] = string.Join(',', Starters),
        ["TIEBREAKERS"] = string.Join(',', Tiebreakers)
    };
}

public sealed record AddDropMutation(string AddPlayerId, string? DropPlayerId)
    : MflMutation(MutationKind.AddDrop)
{
    public override IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["TYPE"] = "fcfsWaiver",
        ["ADD"] = AddPlayerId,
        ["DROP"] = DropPlayerId ?? string.Empty
    };
}

public sealed record WaiverMutation(IReadOnlyList<WaiverClaim> Claims, bool BlindBid, bool Append, int Round = 1)
    : MflMutation(BlindBid ? MutationKind.SubmitBlindBids : MutationKind.SubmitWaivers)
{
    public override IReadOnlyDictionary<string, string> ToParameters()
    {
        var payload = string.Join(',', Claims.OrderBy(c => c.Priority).Select(c => BlindBid
            ? string.Join('_', c.AddPlayerId,
                c.BidAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
                c.DropPlayerId ?? "0000")
            : string.Join('_', c.AddPlayerId, c.DropPlayerId ?? "0000")));
        return new Dictionary<string, string>
        {
            ["TYPE"] = BlindBid ? "blindBidWaiverRequest" : "waiverRequest",
            ["ROUND"] = Round.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PICKS"] = payload,
            ["REPLACE"] = Append ? "0" : "1"
        };
    }
}

public sealed record CancelWaiverClaimMutation(
    WaiverClaim Claim,
    IReadOnlyList<WaiverClaim> RemainingClaims,
    bool BlindBid,
    int Round)
    : MflMutation(MutationKind.CancelWaiverClaim)
{
    public override IReadOnlyDictionary<string, string> ToParameters() =>
        new WaiverMutation(RemainingClaims, BlindBid, false, Round).ToParameters();
}

public sealed record ReorderWaiverClaimsMutation(
    IReadOnlyList<WaiverClaim> OriginalOrder,
    IReadOnlyList<WaiverClaim> DesiredOrder,
    bool BlindBid,
    int Round)
    : MflMutation(MutationKind.ReorderWaiverClaims)
{
    public override IReadOnlyDictionary<string, string> ToParameters() =>
        new WaiverMutation(DesiredOrder.Select((claim, index) => claim with { Priority = index + 1 }).ToArray(),
            BlindBid, false, Round).ToParameters();
}

public sealed record EditWaiverClaimMutation(
    IReadOnlyList<WaiverClaim> OriginalOrder,
    IReadOnlyList<WaiverClaim> DesiredOrder,
    int Round)
    : MflMutation(MutationKind.EditWaiverClaim)
{
    public override IReadOnlyDictionary<string, string> ToParameters() =>
        new WaiverMutation(DesiredOrder, true, false, Round).ToParameters();
}

public sealed record PlayerListMutation(MutationKind MutationKind, string ImportType, IReadOnlyList<string> PlayerIds)
    : MflMutation(MutationKind)
{
    public override IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["TYPE"] = ImportType,
        [ImportType == "keepers" ? "KEEP" : "PLAYERS"] = string.Join(',', PlayerIds)
    };
}

public sealed record DraftPickMutation(string PlayerId, int Round, int Pick, string FranchiseId, string? Comments = null)
    : MflMutation(MutationKind.SubmitDraftPick)
{
    public override IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["CMD"] = "DRAFT",
        ["PLAYER_PICK"] = PlayerId,
        ["FRANCHISE_PICK"] = FranchiseId,
        ["ROUND"] = Round.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["PICK"] = Pick.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["COMMENTS"] = Comments ?? string.Empty,
        ["JSON"] = "1"
    };
}

public enum TradeResponse { Accept, Reject, Revoke }

public sealed record RespondToTradeMutation(string TradeId, TradeResponse Response)
    : MflMutation(MutationKind.RespondToTrade)
{
    public override IReadOnlyDictionary<string, string> ToParameters() => new Dictionary<string, string>
    {
        ["TYPE"] = "tradeResponse",
        ["TRADE_ID"] = TradeId,
        ["RESPONSE"] = Response.ToString().ToUpperInvariant()
    };
}

public sealed record GenericMutation(
    MutationKind MutationKind,
    string RequestType,
    IReadOnlyDictionary<string, string> Parameters,
    bool ConfirmationRequired = true,
    bool UsesLiveDraftEndpoint = false)
    : MflMutation(MutationKind, ConfirmationRequired)
{
    public override IReadOnlyDictionary<string, string> ToParameters()
    {
        var result = new Dictionary<string, string>(Parameters) { [UsesLiveDraftEndpoint ? "CMD" : "TYPE"] = RequestType };
        return result;
    }
}

public sealed record MutationPreview(
    MutationKind Kind,
    string Summary,
    IReadOnlyDictionary<string, string> SanitizedParameters,
    bool RequiresConfirmation,
    IReadOnlyList<string> Warnings);

public sealed record MutationReceipt(
    Guid OperationId,
    bool Accepted,
    string Message,
    string CorrelationId,
    bool RefreshSucceeded,
    bool Verified,
    DateTimeOffset CompletedAt);

public sealed record OperationJournalEntry(
    Guid OperationId,
    DateTimeOffset Timestamp,
    string LeagueId,
    string? FranchiseId,
    MutationKind Operation,
    string SanitizedRequestJson,
    bool Accepted,
    string Result,
    bool Verified);
