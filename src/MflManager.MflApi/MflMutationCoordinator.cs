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
    IConfirmationPolicy confirmationPolicy) : IMflMutationCoordinator
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
            if (mutation is DraftPickMutation draftPick)
            {
                var latest = await drafts.GetDraftStateAsync(league, cancellationToken).ConfigureAwait(false);
                if (!latest.IsSuccess || latest.Value is null) return Receipt(false, "Could not verify the current draft state.", latest.CorrelationId, false, false);
                if (latest.Value.Round != draftPick.Round || latest.Value.Pick != draftPick.Pick || latest.Value.OnTheClockFranchiseId != draftPick.FranchiseId)
                    return Receipt(false, "The draft advanced before submission. Refresh and confirm the current pick.", latest.CorrelationId, false, false);
            }
            var submitted = mutation is DraftPickMutation or GenericMutation { UsesLiveDraftEndpoint: true }
                ? await drafts.SubmitAsync(league, mutation.ToParameters(), cancellationToken).ConfigureAwait(false)
                : await imports.ImportAsync(league, mutation.ToParameters(), cancellationToken).ConfigureAwait(false);
            if (!submitted.IsSuccess)
            {
                await WriteJournal(false, submitted.UserMessage ?? "Rejected", false).ConfigureAwait(false);
                return Receipt(false, submitted.UserMessage ?? "MFL rejected the request.", submitted.CorrelationId, false, false);
            }
            RefreshReport? report = null;
            try { report = await refresh.RefreshAllAsync(league, RefreshReason.Mutation, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            var refreshed = report is not null;
            var verified = report?.IsSuccess == true;
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
        _ => $"Submit {mutation.Kind}."
    };
}
