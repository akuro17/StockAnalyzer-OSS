using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public sealed class SpiralAnalysisDataSourceTests
{
    [Fact]
    public void DataRevision_ChangesOnlyWhenCandlesAreReplaced()
    {
        var source = new SpiralAnalysisDataSource();
        long initialRevision = source.DataRevision;
        source.SetCandles(CreateCandles(10m, 12m));

        long candleRevision = source.DataRevision;
        source.Analyze(new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Archimedean, 10m,
            RadialGrowthPerRadian: 1d));

        Assert.Equal(initialRevision + 1, candleRevision);
        Assert.Equal(candleRevision, source.DataRevision);
    }

    [Fact]
    public void Analyze_UsesCandlesSuppliedByTheActiveChartBoundary()
    {
        ISpiralAnalysisDataSource source = new SpiralAnalysisDataSource();
        source.SetCandles(new[]
        {
            new CoreCandleData(new DateTime(2024, 1, 1), 10m, 10m, 10m, 10m, 1L),
            new CoreCandleData(new DateTime(2024, 1, 2), 12m, 12m, 12m, 12m, 1L)
        });

        source.Analyze(new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Archimedean, 10m, RadialGrowthPerRadian: 1d));

        Assert.NotNull(source.Current);
        Assert.Equal(2, source.Current!.Samples.Count);
        Assert.Equal(12m, source.Current.Samples[1].Price);
    }

    [Fact]
    public void TryGetFirstPositivePrice_UsesSelectedSharedPriceType()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(new[]
        {
            new CoreCandleData(new DateTime(2024, 1, 1), -2m, 3m, -3m, -1m, 1L),
            new CoreCandleData(new DateTime(2024, 1, 2), 11m, 20m, 8m, 14m, 1L)
        });

        bool found = source.TryGetFirstPositivePrice(0, 1, PriceType.Open, out decimal price);

        Assert.True(found);
        Assert.Equal(11m, price);
    }

    private static CoreCandleData[] CreateCandles(params decimal[] closes)
    {
        var candles = new CoreCandleData[closes.Length];
        for (int index = 0; index < closes.Length; index++)
        {
            decimal close = closes[index];
            candles[index] = new CoreCandleData(new DateTime(2024, 1, 1).AddDays(index), close, close, close, close, 1L);
        }

        return candles;
    }
}
