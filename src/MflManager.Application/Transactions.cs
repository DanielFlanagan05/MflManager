using System.Globalization;
using MflManager.Domain;

namespace MflManager.Application;

public sealed record TransactionRow(string TeamName, string Description, decimal? BidAmount, DateTimeOffset Timestamp)
{
    public string BidText => BidAmount?.ToString("C", CultureInfo.CurrentCulture) ?? "—";
    public string DateText => Timestamp == DateTimeOffset.MinValue ? "—" : Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public static class TransactionTable
{
    public static IReadOnlyList<TransactionRow> Build(LeagueSnapshot snapshot)
    {
        var teams = snapshot.Franchises.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Name, StringComparer.OrdinalIgnoreCase);
        var players = snapshot.Players.Concat(snapshot.FreeAgents).Concat(snapshot.Roster.Select(x => x.Player))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Name, StringComparer.OrdinalIgnoreCase);
        return snapshot.Transactions
            .Where(x => x.AcquiredPlayerIds.Count > 0 || x.DroppedPlayerIds.Count > 0)
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new TransactionRow(
                teams.GetValueOrDefault(x.FranchiseId ?? "", $"Team {x.FranchiseId ?? "unknown"}"),
                string.Join(", ", x.AcquiredPlayerIds.Select(id => $"Acquired {PlayerName(id)}")
                    .Concat(x.DroppedPlayerIds.Select(id => $"Dropped {PlayerName(id)}"))),
                x.AcquiredPlayerIds.Count > 0 ? x.BidAmount : null, x.Timestamp)).ToArray();

        string PlayerName(string id) => players.TryGetValue(id, out var name)
            ? AvailablePlayerSelector.DisplayName(name) : $"Player {id}";
    }
}
