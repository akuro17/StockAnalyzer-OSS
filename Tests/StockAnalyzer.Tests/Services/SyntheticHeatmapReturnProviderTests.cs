using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Services;

/// <summary>
/// Pins the exact values of the synthetic heatmap returns, so the way they are generated can change without the demo data changing.
/// The expected values come from an independent computation of the 32-bit FNV-1a hash (low byte, then high byte, of every UTF-16 unit).
/// </summary>
public sealed class SyntheticHeatmapReturnProviderTests
{
    // ticker, 32-bit hash, return ratio = (hash % 1000) / 1000 * 0.16 - 0.08
    public static TheoryData<string, uint, double> Cases => new()
    {
        { "AAPL", 140046577u, 0.012319999999999998 },
        { "MSFT", 858343345u, -0.024800000000000003 },
        { "GOOGL", 2865479473u, -0.004320000000000004 },
        { "BRK-B", 2013106689u, 0.03023999999999999 },
        { "7203.T", 525984513u, 0.0020799999999999985 },
        { "\u30c8\u30e8\u30bf", 2199718522u, 0.0035200000000000092 }, // non-ASCII: the high byte of each unit counts
        { "aapl", 1687171313u, -0.029920000000000002 },               // case-sensitive
        { "", 2166136261u, -0.038239999999999996 },                   // the FNV offset basis
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Offset0_ReturnsTheFixedValue_ForEveryTicker(string ticker, uint _, double expectedReturn)
    {
        var result = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(new[] { ticker }, 0);

        Assert.Single(result.Items);
        Assert.Equal(ticker, result.Items[0].Ticker);
        Assert.Equal(expectedReturn, result.Items[0].ReturnPercentage, precision: 12);
    }

    [Fact]
    public async Task Offset0_KeepsTheTickerOrder_AndTheMetadata()
    {
        var tickers = new[] { "MSFT", "AAPL", "GOOGL" };

        var result = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(tickers, 0);

        Assert.Equal(tickers, result.Items.Select(i => i.Ticker).ToArray());
        Assert.Equal(new[] { -0.024800000000000003, 0.012319999999999998, -0.004320000000000004 }, result.Items.Select(i => i.ReturnPercentage).ToArray());
        Assert.Equal("Synthetic", result.ProviderId);
        Assert.False(result.IsLiveMarketData);
        Assert.Null(result.TargetDate);
        Assert.Equal("Synthetic Demo Data", result.StatusMessage);
    }

    [Fact]
    public async Task AValueDoesNotDependOnTheOtherTickersInTheList()
    {
        var alone = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(new[] { "AAPL" }, 0);
        var inAList = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(new[] { "MSFT", "AAPL" }, 0);

        Assert.Equal(alone.Items[0].ReturnPercentage, inAList.Items[1].ReturnPercentage);
    }

    [Fact]
    public async Task EveryValueStaysInsideTheDocumentedBand()
    {
        var tickers = Enumerable.Range(0, 500).Select(i => "T" + i).ToArray();

        var result = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(tickers, 0);

        Assert.All(result.Items, i => Assert.InRange(i.ReturnPercentage, -0.08, 0.08));
    }

    [Fact]
    public async Task AnOffsetAboveZero_HasNoHistory_AndIsNaN()
    {
        var result = await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(new[] { "AAPL", "MSFT" }, 3);

        Assert.All(result.Items, i => Assert.True(double.IsNaN(i.ReturnPercentage)));
        Assert.Equal("Synthetic Demo Data (no history at offset)", result.StatusMessage);
    }

    [Fact]
    public async Task NoTickers_GiveTheEmptyResult()
    {
        Assert.Same(HeatmapReturnResult.Empty, await SyntheticHeatmapReturnProvider.Instance.ComputeAsync(System.Array.Empty<string>(), 0));
    }

    [Fact]
    public void GetStableHash32_ProducesDeterministicValuesWithoutOverflow()
    {
        uint hash1 = SyntheticHeatmapReturnProvider.GetStableHash32("AAPL");
        uint hash2 = SyntheticHeatmapReturnProvider.GetStableHash32("AAPL");
        Assert.Equal(hash1, hash2);

        uint hashMsft = SyntheticHeatmapReturnProvider.GetStableHash32("MSFT");
        Assert.NotEqual(hash1, hashMsft);

        uint hashEmpty = SyntheticHeatmapReturnProvider.GetStableHash32("");
        Assert.Equal(2166136261u, hashEmpty);
    }
}
