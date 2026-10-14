using System.Collections.Concurrent;
using System.Text.Json;
using MflManager.Application;
using MflManager.Domain;

namespace MflManager.MflApi;

public sealed class MflMutationCoordinator(
    IMflImportClient imports,
    IMflDraftClient drafts,
    IMflRefreshService refresh,
    IOperationJournal journal,
    IConfirmationPolicy confirmationPolicy,
    IMflExportClient exports,
    ILeagueState state) : IMflMutationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _leagueLocks = new(StringComparer.OrdinalIgnoreCase);

    public MutationPreview Preview(LeagueContext league, MflMutation mutation)
    {
        var parameters = mutation.ToParameters().ToDictionary(x => x.Key, x => Redact(x.Key, x.Value));
        var warnings = new List<string>();
        if (league.Season.IsReadOnly) warnings.Add("Historical seasons are read-only.");
        if (mutation is DraftPickMutation pick && pick.FranchiseId != league.FranchiseId && !league.IsCommissioner)
            warnings.Add("The selected franchise is not authorized to make this pick.");
        return new MutationPreview(mutation.Kind, Describe(mutation), parameters,
            mutation.RequiresConfirmation && confirmationPolicy.RequiresConfirmation(mutation.Kind), warnings);
    }

    public async Task<MutationReceipt> ExecuteAsync(LeagueContext league, MflMutation mutation, bool confirmed, CancellationToken cancellationToken)
    {
        var preview = Preview(league, mutation);
        var operationId = Guid.NewGuid();
        if (preview.Warnings.Count > 0) return Receipt(false, string.Join(' ', preview.Warnings), string.Empty, false, false);
        if (preview.RequiresConfirmation && !confirmed) return Receipt(false, "Confirmation is required.", string.Empty, false, false);
        var gate = _leagueLocks.GetOrAdd($"{league.Season.Year}:{league.LeagueId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyDictionary<string, string>? freshParameters = null;
            if (mutation is CancelWaiverClaimMutation cancellation)
            {
                if (state.SelectedLeague != league || string.IsNullOrWhiteSpace(league.FranchiseId))
                    return Receipt(false, "Select the authorized franchise before canceling a claim.", "", false, false);
                var pending = await exports.ExportAsync(league, "pendingWaivers", null, cancellationToken).ConfigureAwait(false);
                if (!pending.IsSuccess)
                    return Receipt(false, "Fresh pending claims could not be verified. No claim was canceled.", pending.CorrelationId, false, false);
                var current = MflJsonParser.ParseWaivers(pending.Value!).ToList();
                var targetIndex = current.FindIndex(candidate => SameClaim(candidate, cancellation.Claim));
                if (targetIndex < 0)
                    return Receipt(false, "This waiver claim is no longer pending. Refresh before trying again.", pending.CorrelationId, false, false);
                current.RemoveAt(targetIndex);
                var remainingRound = current.Where(claim => claim.Round == cancellation.Round)
                    .OrderBy(claim => claim.Priority).ToArray();
                freshParameters = new WaiverMutation(remainingRound, cancellation.BlindBid, false, cancellation.Round).ToParameters();
            }
            if (mutation is ReorderWaiverClaimsMutation reorder)
            {
                if (state.SelectedLeague != league || string.IsNullOrWhiteSpace(league.FranchiseId))
                    return Receipt(false, "Select the authorized franchise before changing claim priority.", "", false, false);
                if (reorder.OriginalOrder.Count < 2 || reorder.OriginalOrder.Count != reorder.DesiredOrder.Count ||
                    reorder.OriginalOrder.Concat(reorder.DesiredOrder).Any(claim => claim.Round != reorder.Round ||
                        (claim.BidAmount is not null) != reorder.BlindBid) ||
                    !SameClaimSet(reorder.OriginalOrder, reorder.DesiredOrder))
                    return Receipt(false, "The requested waiver order is invalid.", "", false, false);
                var pending = await exports.ExportAsync(league, "pendingWaivers", null, cancellationToken).ConfigureAwait(false);
                if (!pending.IsSuccess)
                    return Receipt(false, "Fresh pending claims could not be verified. Priority was not changed.", pending.CorrelationId, false, false);
                var currentRound = MflJsonParser.ParseWaivers(pending.Value!).Where(claim => claim.Round == reorder.Round)
                    .OrderBy(claim => claim.Priority).ToArray();
                if (currentRound.Length != reorder.OriginalOrder.Count ||
                    !currentRound.Zip(reorder.OriginalOrder).All(pair => SameClaimPayload(pair.First, pair.Second)))
                    return Receipt(false, "Pending claims changed since they were displayed. Refresh before reordering.", pending.CorrelationId, false, false);
                freshParameters = reorder.ToParameters();
            }
            if (mutation is EditWaiverClaimMutation edit)
            {
                if (state.SelectedLeague != league || string.IsNullOrWhiteSpace(league.FranchiseId) ||
                    edit.OriginalOrder.Count == 0 || edit.OriginalOrder.Count != edit.DesiredOrder.Count ||
                    edit.OriginalOrder.Concat(edit.DesiredOrder).Any(claim => claim.Round != edit.Round))
                    return Receipt(false, "The waiver edit is invalid or the franchise is not authorized.", "", false, false);
                var changed = edit.OriginalOrder.Zip(edit.DesiredOrder)
                    .Where(pair => !SameClaimPayload(pair.First, pair.Second)).ToArray();
                if (changed.Length != 1 || changed[0].First.AddPlayerId != changed[0].Second.AddPlayerId ||
                    changed[0].First.Priority != changed[0].Second.Priority)
                    return Receipt(false, "Only one claim's bid and drop may be edited at a time.", "", false, false);
                var pending = await exports.ExportAsync(league, "pendingWaivers", null, cancellationToken).ConfigureAwait(false);
                if (!pending.IsSuccess)
                    return Receipt(false, "Fresh pending claims could not be verified. No edit was submitted.", pending.CorrelationId, false, false);
                var currentRound = MflJsonParser.ParseWaivers(pending.Value!).Where(claim => claim.Round == edit.Round)
                    .OrderBy(claim => claim.Priority).ToArray();
                if (currentRound.Length != edit.OriginalOrder.Count ||
                    !currentRound.Zip(edit.OriginalOrder).All(pair => SameClaimPayload(pair.First, pair.Second)))
                    return Receipt(false, "Pending claims changed since they were displayed. Refresh before editing.", pending.CorrelationId, false, false);
                freshParameters = edit.ToParameters();
            }
            var waiverForValidation = mutation switch
            {
                WaiverMutation direct => direct,
                EditWaiverClaimMutation editing => new WaiverMutation(editing.DesiredOrder, true, false, editing.Round),
                _ => null
            };
            if (waiverForValidation is { } waiver)
            {
                if (state.SelectedLeague != league || string.IsNullOrWhiteSpace(league.FranchiseId))
                    return Receipt(false, "Select the authorized franchise before submitting claims.", "", false, false);
                if (waiver.Claims.Count == 0 || waiver.Claims.Any(x => string.IsNullOrWhiteSpace(x.AddPlayerId) || x.Priority < 1 ||
                    waiver.BlindBid && (x.BidAmount is null || x.BidAmount < 0 || x.BidAmount != decimal.Truncate(x.BidAmount.Value))))
                    return Receipt(false, "The waiver claim or bid amount is invalid.", "", false, false);
                var available = await exports.ExportAsync(league, "freeAgents", null, cancellationToken).ConfigureAwait(false);
                var rosters = await exports.ExportAsync(league, "rosters", null, cancellationToken).ConfigureAwait(false);
                var settingsResponse = await exports.ExportAsync(league, "league", null, cancellationToken).ConfigureAwait(false);
                if (!available.IsSuccess || !rosters.IsSuccess || !settingsResponse.IsSuccess)
                    return Receipt(false, "Fresh availability, roster and league rules could not be verified. No claim was submitted.", "", false, false);
                var catalog = state.CurrentSnapshot?.Players.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase) ?? [];
                var availableIds = MflJsonParser.ParseFreeAgents(available.Value!, catalog).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var roster = MflJsonParser.ParseRoster(rosters.Value!, catalog, league.FranchiseId);
                var rules = MflJsonParser.ParseSettings(settingsResponse.Value!);
                var settingsRoot = System.Text.Json.Nodes.JsonNode.Parse(settingsResponse.Value!);
                var owner = JsonTraversal.ObjectsNamed(settingsRoot, "franchise").FirstOrDefault(x => JsonTraversal.Value(x, "id") == league.FranchiseId);
                var leagueSettings = JsonTraversal.ObjectsNamed(settingsRoot, "league").FirstOrDefault();
                decimal? Number(System.Text.Json.Nodes.JsonObject? node, string key) => node is not null &&
                    decimal.TryParse(JsonTraversal.Value(node, key), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
                var balance = Number(owner, "bbidAvailableBalance");
                var increment = Number(leagueSettings, "bbidIncrement");
                if (waiver.BlindBid && (balance is null || waiver.Claims.Any(x => x.BidAmount > balance ||
                    increment > 0 && x.BidAmount % increment != 0)))
                    return Receipt(false, "The bid exceeds the available blind-bid balance or does not match the league's bid increment. No claim was submitted.", "", false, false);
                if (waiver.Claims.Any(x => !availableIds.Contains(x.AddPlayerId) ||
                    x.DropPlayerId is not null && !roster.Any(p => p.Player.Id == x.DropPlayerId) ||
                    x.DropPlayerId is null && rules.RosterSize > 0 && roster.Count(p => p.Status == RosterStatus.Active) >= rules.RosterSize))
                    return Receipt(false, "A player is no longer available, the drop is not owned, or a drop is required by the roster limit. No claim was submitted.", "", false, false);
            }
            if (mutation is DraftPickMutation draftPick)
            {
                var latest = await drafts.GetDraftStateAsync(league, cancellationToken).ConfigureAwait(false);
                if (!latest.IsSuccess || latest.Value is null) return Receipt(false, "Could not verify the current draft state.", latest.CorrelationId, false, false);
                if (latest.Value.Round != draftPick.Round || latest.Value.Pick != draftPick.Pick || latest.Value.OnTheClockFranchiseId != draftPick.FranchiseId)
                    return Receipt(false, "The draft advanced before submission. Refresh and confirm the current pick.", latest.CorrelationId, false, false);
            }
            var parameters = freshParameters ?? mutation.ToParameters();
            if ((mutation is WaiverMutation or CancelWaiverClaimMutation or ReorderWaiverClaimsMutation or EditWaiverClaimMutation) && league.IsCommissioner)
                parameters = new Dictionary<string, string>(parameters) { ["FRANCHISE_ID"] = league.FranchiseId! };
            var submitted = mutation is DraftPickMutation or GenericMutation { UsesLiveDraftEndpoint: true }
                ? await drafts.SubmitAsync(league, mutation.ToParameters(), cancellationToken).ConfigureAwait(false)
                : await imports.ImportAsync(league, parameters, cancellationToken).ConfigureAwait(false);
            if (!submitted.IsSuccess)
            {
                await WriteJournal(false, submitted.UserMessage ?? "Rejected", false).ConfigureAwait(false);
                return Receipt(false, submitted.UserMessage ?? "MFL rejected the request.", submitted.CorrelationId, false, false);
            }
            RefreshReport? report = null;
            try { report = await refresh.RefreshAllAsync(league, RefreshReason.Mutation, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            var refreshed = report is not null;
            var verified = mutation switch
            {
                CancelWaiverClaimMutation canceled =>
                    report?.Snapshot.Sections.GetValueOrDefault(SnapshotSection.Waivers)?.IsStale == false &&
                    !report.Snapshot.Waivers.Any(actual => SameClaim(actual, canceled.Claim)),
                ReorderWaiverClaimsMutation reordered =>
                    report?.Snapshot.Sections.GetValueOrDefault(SnapshotSection.Waivers)?.IsStale == false &&
                    report.Snapshot.Waivers.Where(claim => claim.Round == reordered.Round).OrderBy(claim => claim.Priority)
                        .Select((claim, index) => (claim, index)).All(pair => pair.index < reordered.DesiredOrder.Count &&
                            SameClaimPayload(pair.claim, reordered.DesiredOrder[pair.index])) &&
                    report.Snapshot.Waivers.Count(claim => claim.Round == reordered.Round) == reordered.DesiredOrder.Count,
                EditWaiverClaimMutation edited =>
                    report?.Snapshot.Sections.GetValueOrDefault(SnapshotSection.Waivers)?.IsStale == false &&
                    report.Snapshot.Waivers.Where(claim => claim.Round == edited.Round).OrderBy(claim => claim.Priority)
                        .Select((claim, index) => (claim, index)).All(pair => pair.index < edited.DesiredOrder.Count &&
                            SameClaimPayload(pair.claim, edited.DesiredOrder[pair.index])) &&
                    report.Snapshot.Waivers.Count(claim => claim.Round == edited.Round) == edited.DesiredOrder.Count,
                WaiverMutation requested => report?.Snapshot.Sections.GetValueOrDefault(SnapshotSection.Waivers)?.IsStale == false &&
                    requested.Claims.All(claim => report.Snapshot.Waivers.Any(actual => actual.AddPlayerId == claim.AddPlayerId && actual.DropPlayerId == claim.DropPlayerId && actual.BidAmount == claim.BidAmount)),
                _ => report?.IsSuccess == true
            };
            await WriteJournal(true, submitted.UserMessage ?? "Accepted", verified).ConfigureAwait(false);
            var message = refreshed ? "MFL accepted the mutation and the app refreshed." : "MFL accepted the mutation, but verification refresh failed.";
            return Receipt(true, message, submitted.CorrelationId, refreshed, verified);
        }
        finally { gate.Release(); }

        MutationReceipt Receipt(bool accepted, string message, string correlationId, bool refreshed, bool verified) =>
            new(operationId, accepted, message, correlationId, refreshed, verified, DateTimeOffset.UtcNow);

        Task WriteJournal(bool accepted, string result, bool verified) => journal.AppendAsync(new OperationJournalEntry(
            operationId, DateTimeOffset.UtcNow, league.LeagueId, league.FranchiseId, mutation.Kind,
            JsonSerializer.Serialize(preview.SanitizedParameters), accepted, result, verified), cancellationToken);
    }

    private static string Redact(string key, string value) => key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ? "[REDACTED]" : value;
    private static string Describe(MflMutation mutation) => mutation switch
    {
        DraftPickMutation x => $"Draft player {x.PlayerId} at round {x.Round}, pick {x.Pick} for franchise {x.FranchiseId}.",
        AddDropMutation x => $"Add player {x.AddPlayerId}" + (x.DropPlayerId is null ? "." : $" and drop player {x.DropPlayerId}."),
        SetLineupMutation x => $"Set {x.Starters.Count} starters for week {x.Week}.",
        WaiverMutation x => $"Submit {x.Claims.Count} {(x.BlindBid ? "blind-bid" : "priority")} waiver claim(s).",
        CancelWaiverClaimMutation x => $"Cancel the waiver claim for player {x.Claim.AddPlayerId} in round {x.Round}.",
        ReorderWaiverClaimsMutation x => $"Reorder {x.DesiredOrder.Count} waiver claims in round {x.Round}.",
        EditWaiverClaimMutation x => $"Edit one waiver claim in round {x.Round}.",
        _ => $"Submit {mutation.Kind}."
    };

    private static bool SameClaim(WaiverClaim left, WaiverClaim right) =>
        left.Round == right.Round && left.Priority == right.Priority &&
        string.Equals(left.AddPlayerId, right.AddPlayerId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.DropPlayerId, right.DropPlayerId, StringComparison.OrdinalIgnoreCase) &&
        left.BidAmount == right.BidAmount;

    private static bool SameClaimPayload(WaiverClaim left, WaiverClaim right) =>
        left.Round == right.Round &&
        string.Equals(left.AddPlayerId, right.AddPlayerId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.DropPlayerId, right.DropPlayerId, StringComparison.OrdinalIgnoreCase) &&
        left.BidAmount == right.BidAmount;

    private static bool SameClaimSet(IReadOnlyList<WaiverClaim> left, IReadOnlyList<WaiverClaim> right)
    {
        var remaining = right.ToList();
        foreach (var claim in left)
        {
            var index = remaining.FindIndex(candidate => SameClaimPayload(claim, candidate));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }
}
