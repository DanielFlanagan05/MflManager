using MflManager.Application;
using MflManager.Domain;
using MflManager.MflApi;

namespace MflManager.UnitTests;

public sealed class TransactionTests
{
    [Theory]
    [InlineData("FREE_AGENT", "p1,|p2,", "p1", "p2", null)]
    [InlineData("FREE_AGENT", "p1,|", "p1", "", null)]
    [InlineData("WAIVER", "|p2,p3,", "", "p2,p3", null)]
    [InlineData("BBID_WAIVER", "p1,|12.5|p2,", "p1", "p2", "12.5")]
    [InlineData("BBID_WAIVER", "p1|0|", "p1", "", "0")]
    [InlineData("BBID_WAIVER_REQUEST", "p1|40|p2", "", "", null)]
    public void ParsesPlayerMovesAndBids(string type, string encoded, string adds, string drops, string? bid)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            transactions = new { transaction = new { timestamp = "1791417600", type, franchise = "0001", transaction = encoded } }
        });
        var transaction = Assert.Single(MflJsonParser.ParseTransactions(json));
        Assert.Equal(adds.Split(',', StringSplitOptions.RemoveEmptyEntries), transaction.AcquiredPlayerIds);
        Assert.Equal(drops.Split(',', StringSplitOptions.RemoveEmptyEntries), transaction.DroppedPlayerIds);
        Assert.Equal(bid is null ? (decimal?)null : decimal.Parse(bid, System.Globalization.CultureInfo.InvariantCulture), transaction.BidAmount);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791417600), transaction.Timestamp);
    }

    [Fact]
    public void TableResolvesNamesOmitsAbsentMovesAndOrdersNewestFirst()
    {
        var transactions = MflJsonParser.ParseTransactions("""
            {"transactions":{"transaction":[
                {"timestamp":"100","type":"FREE_AGENT","franchise":"0001","transaction":"p1,|"},
                {"timestamp":"300","type":"BBID_WAIVER","franchise":"0002","transaction":"p1|7|p2,"},
                {"timestamp":"200","type":"FREE_AGENT","franchise":"0001","transaction":"|p2,"},
                {"timestamp":"400","type":"BBID_WAIVER_REQUEST","franchise":"0001","transaction":"p1|20|p2"}
            ]}}
            """);
        var snapshot = new LeagueSnapshot
        {
            Context = new("test", SeasonContext.Current(2026), new Uri("https://example.com")),
            CapturedAt = DateTimeOffset.UtcNow,
            Franchises = [new("0001", "Team One"), new("0002", "Team Two")],
            Players = [new("p1", "Runner, Ray", "RB", "BUF"), new("p2", "Receiver, Rob", "WR", "DET")],
            Transactions = transactions
        };
        var rows = TransactionTable.Build(snapshot);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Team Two", rows[0].TeamName);
        Assert.Equal("Acquired Ray Runner, Dropped Rob Receiver", rows[0].Description);
        Assert.Equal(7m, rows[0].BidAmount);
        Assert.Equal("Dropped Rob Receiver", rows[1].Description);
        Assert.Null(rows[1].BidAmount);
        Assert.Equal("Acquired Ray Runner", rows[2].Description);
    }

    [Fact]
    public void InvalidTimestampDoesNotBecomeANewTransaction()
    {
        var transaction = Assert.Single(MflJsonParser.ParseTransactions("""{"transaction":{"timestamp":"invalid","type":"FREE_AGENT","transaction":"p1|"}}"""));
        Assert.Equal(DateTimeOffset.MinValue, transaction.Timestamp);
        Assert.Equal("—", new TransactionRow("Team", "Acquired player", null, transaction.Timestamp).DateText);
        Assert.Empty(MflJsonParser.ParseTransactions("""{"transactions":{}}"""));
    }
}
