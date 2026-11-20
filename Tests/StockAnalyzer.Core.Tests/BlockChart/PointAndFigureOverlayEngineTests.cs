using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// Point and Figure overlay engine (T2). Oracle = the EXISTING <see cref="PointAndFigureCalculator"/> on every prefix with the same effective size, then the existing
/// <see cref="PointAndFigureLevels.TryDescribe"/> on its last column.
/// </summary>
public class PointAndFigureOverlayEngineTests
{
    private static OracleState? Oracle(IReadOnlyList<CoreCandleData> bars, decimal size, int reversal, ChartRoundingMode rounding)
    {
        var source = bars.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();
        var adapter = PointAndFigureCalculator.Calculate(source, new PointAndFigureParameters(size, reversal, rounding));
        if (adapter.Count == 0) return null;

        int last = adapter.Count - 1;
        Assert.True(PointAndFigureLevels.TryDescribe(
            adapter.Opens.Span, adapter.Highs.Span, adapter.Lows.Span, adapter.Closes.Span, last, size, Math.Max(reversal, 1), out decimal rev, out decimal upd));

        // Direction of the last column from the calculator's own geometry (X column: Open = bottom, Close = top); a one-box column has Open == Close and is resolved by alternation in TryDescribe,
        // so derive the direction from the reversal price instead: an X column reverses BELOW its top, an O column ABOVE its bottom.
        bool isUp = rev < adapter.Closes.Span[last];
        return new OracleState(adapter.Closes.Span[last], rev, upd, isUp);
    }

    private static BlockChartOverlaySeries Run(IReadOnlyList<CoreCandleData> candles, BlockChartSizingSpec sizing, int reversal)
        => BlockChartOverlayMapper.Map(candles, sizing, size => new PointAndFigureBarEngine(size, reversal, sizing.Rounding), hasUpdate: true);

    private static BlockChartSizingSpec Fixed(decimal size, ChartRoundingMode rounding = ChartRoundingMode.None)
        => new(ChartSizingMode.Fixed, size, 1m, 14, 1m, rounding, AutoFallbackMode.Percentage);

    // --- prefix parity (TC-10) --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1.0, 3, 0)]
    [InlineData(2, 0.5, 1, 0)]
    [InlineData(33, 2.0, 4, 0)]
    [InlineData(4, 1.0, 3, 3)] // Floor
    [InlineData(5, 0.7, 2, 5)] // Round
    [InlineData(6, 1.5, 3, 1)] // NiceNumbers (grid via ChartMath.Quantize)
    public void FixedSize_EveryPrefixEqualsThePointAndFigureCalculator(int seed, double sizeValue, int reversal, int roundingValue)
    {
        var rounding = (ChartRoundingMode)roundingValue;
        var candles = RandomWalk(seed, 150, 100m, 2m);
        var sizing = Fixed((decimal)sizeValue, rounding);
        Assert.True(sizing.TryResolve(candles.Take(1).ToArray(), out decimal resolved));

        BlockChartParity.AssertMatches(candles, Run(candles, sizing, reversal), 0, p => Oracle(p, resolved, reversal, rounding), $"seed {seed}", requireRichComparison: true);
    }

    [Theory]
    [InlineData(11, 14, 1.0, 3)]
    [InlineData(12, 5, 0.5, 2)]
    [InlineData(13, 20, 2.0, 3)]
    public void AutoAtr_EveryPrefixFromTheAnchorEqualsTheCalculatorWithTheAnchoredSize(int seed, int period, double multiplier, int reversal)
    {
        var candles = RandomWalk(seed, 150, 100m, 2m);
        var sizing = new BlockChartSizingSpec(ChartSizingMode.AutoAtr, 1m, 1m, period, (decimal)multiplier, ChartRoundingMode.None, AutoFallbackMode.Percentage);
        Assert.True(sizing.TryResolve(candles.Take(period + 1).ToArray(), out decimal anchored));

        BlockChartParity.AssertMatches(candles, Run(candles, sizing, reversal), period, p => Oracle(p, anchored, reversal, ChartRoundingMode.None), $"seed {seed}", requireRichComparison: true);
    }

    // --- TC-07: one bar reaches both the extension and the reversal condition --------------------------------------------------------------------

    [Fact]
    public void Tc07_ABarThatReachesBothConditions_ExtendsFirst_AsTheCalculatorDecides()
    {
        // S = 1, R = 3. Seed 100; the second bar makes the X column [100,105].
        var up = new List<CoreCandleData> { Flat(0, 100m), Bar(1, 100m, 105m, 100m, 105m) };

        // Both true on the third bar: quantized high 106 > 105 (extension) AND low 102 <= 105 - 3 (reversal). The extension wins.
        var both = up.Append(Bar(2, 105m, 106.5m, 102m, 104m)).ToList();
        var series = Run(both, Fixed(1m), 3);
        Assert.Equal(106m, series.Close[2]);
        Assert.Equal(1m, series.IsUpTrend[2]);
        BlockChartParity.AssertMatches(both, series, 0, p => Oracle(p, 1m, 3, ChartRoundingMode.None), "both conditions");

        // Reversal only: the quantized high 105 does not exceed the top, the low reaches 102.
        var reversalOnly = up.Append(Bar(2, 105m, 105.5m, 102m, 103m)).ToList();
        var reversed = Run(reversalOnly, Fixed(1m), 3);
        Assert.Equal(102m, reversed.Close[2]);
        Assert.Equal(0m, reversed.IsUpTrend[2]);
        BlockChartParity.AssertMatches(reversalOnly, reversed, 0, p => Oracle(p, 1m, 3, ChartRoundingMode.None), "reversal only");
    }

    [Fact]
    public void WithNoColumnYet_TheUpConditionIsTestedBeforeTheDownCondition()
    {
        // Seed 100, S = 1: a wide bar satisfies high >= 101 AND low <= 99; the calculator starts an X column.
        var candles = new List<CoreCandleData> { Flat(0, 100m), Bar(1, 100m, 103.4m, 98.2m, 100m) };
        var series = Run(candles, Fixed(1m), 3);

        Assert.Equal(103m, series.Close[1]);
        Assert.Equal(1m, series.IsUpTrend[1]);
        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 1m, 3, ChartRoundingMode.None), "up first");
    }

    [Fact]
    public void TheFirstBarOnlySeedsTheBaseline_ItsOwnHighAndLowStartNothing()
    {
        // The calculator's loop starts at the second bar: a huge range on the first bar creates no column.
        var candles = new List<CoreCandleData> { Bar(0, 100m, 150m, 50m, 100m), Flat(1, 100m) };
        var series = Run(candles, Fixed(1m), 3);

        Assert.Null(series.Close[0]);
        Assert.Null(series.Close[1]);
    }

    // --- TC-08: boundary triple 100.09 / 100.10 / 100.11 (S = 0.1, baseline 100.0) -------------------------------------------------------------

    [Theory]
    [InlineData("100.09", false)]
    [InlineData("100.10", true)]
    [InlineData("100.11", true)]
    public void Tc08_TheFirstColumnStartsExactlyAtBaselinePlusOneBox(string highText, bool columnStarts)
    {
        decimal high = decimal.Parse(highText, System.Globalization.CultureInfo.InvariantCulture);
        var candles = new List<CoreCandleData> { Flat(0, 100.00m), Bar(1, 100.00m, high, 100.00m, 100.00m) };
        var series = Run(candles, Fixed(0.1m), 3);

        if (columnStarts)
        {
            Assert.Equal(100.1m, series.Close[1]); // QuantizeDown(100.11, 0.1) = 100.1
            Assert.Equal(100.2m, series.Update![1]);
            Assert.Equal(99.8m, series.Reversal[1]); // 100.1 - 3 x 0.1
        }
        else
        {
            Assert.Null(series.Close[1]);
        }

        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 0.1m, 3, ChartRoundingMode.None), highText);
    }

    // --- TC-12 ------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("107.1", false)] // 2.9 boxes below the top 110: no reversal
    [InlineData("107.0", true)]  // 3 boxes: reversal (inclusive)
    public void Tc12_ReversalNeedsRBoxes_AndTheClosedPositionIsTheReachedBottomNotThePassedBoxes(string lowText, bool reverses)
    {
        decimal low = decimal.Parse(lowText, System.Globalization.CultureInfo.InvariantCulture);
        var candles = new List<CoreCandleData>
        {
            Flat(0, 100m),
            Bar(1, 100m, 110m, 100m, 110m),            // X column [100,110]
            Bar(2, 110m, 110m, low, low),               // the test bar: high does not extend (quantized 110 is not above 110)
        };
        var series = Run(candles, Fixed(1m), 3);

        Assert.Equal(110m, series.Close[1]);
        if (reverses)
        {
            Assert.Equal(107m, series.Close[2]);        // the O column reaches 107 (QuantizeUp of the low), Close is that reached bottom
            Assert.Equal(0m, series.IsUpTrend[2]);
            Assert.Equal(110m, series.Reversal[2]);     // bottom 107 + 3
            Assert.Equal(106m, series.Update![2]);      // bottom 107 - 1
        }
        else
        {
            Assert.Equal(110m, series.Close[2]);
            Assert.Equal(1m, series.IsUpTrend[2]);
            Assert.Equal(107m, series.Reversal[2]);     // top 110 - 3
            Assert.Equal(111m, series.Update![2]);
        }

        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 1m, 3, ChartRoundingMode.None), lowText);
    }

    [Fact]
    public void AColumnOfOneBox_KeepsItsDirection_NotDerivedFromOpenAndClose()
    {
        // S = 1, R = 1: an X column [100,101], then a one-box O column [100,100] (Open == Close in the calculator's record). The engine reports the direction it has.
        var candles = new List<CoreCandleData> { Flat(0, 100m), Bar(1, 100m, 101m, 100m, 101m), Bar(2, 101m, 101m, 100m, 100m) };
        var series = Run(candles, Fixed(1m), 1);

        Assert.Equal(1m, series.IsUpTrend[1]);
        Assert.Equal(0m, series.IsUpTrend[2]);
        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 1m, 1, ChartRoundingMode.None), "one box");
    }

    [Fact]
    public void Engine_RejectsANonPositiveBoxSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointAndFigureBarEngine(0m, 3, ChartRoundingMode.None));
    }

    [Fact]
    public void ScalarLevels_AreTheSameFormulaAsTheArrayForm()
    {
        Assert.True(PointAndFigureLevels.TryDescribeColumn(true, 110m, 1m, 3, out decimal upReversal, out decimal upUpdate));
        Assert.Equal(107m, upReversal);
        Assert.Equal(111m, upUpdate);

        Assert.True(PointAndFigureLevels.TryDescribeColumn(false, 107m, 1m, 3, out decimal downReversal, out decimal downUpdate));
        Assert.Equal(110m, downReversal);
        Assert.Equal(106m, downUpdate);

        Assert.False(PointAndFigureLevels.TryDescribeColumn(true, 110m, 0m, 3, out _, out _));
        Assert.False(PointAndFigureLevels.TryDescribeColumn(true, 110m, 1m, 0, out _, out _));
    }
}
