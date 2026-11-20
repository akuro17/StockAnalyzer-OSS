using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// TC-10 (Data tab): the rows of a block chart overlay in the Data tab are the values of the same IIndicatorResult series the chart draws (Close, Reversal, Update; Kagi and Three Line Break have no Update),
/// formatted like every price (<see cref="DisplayFormat.Price"/>), and equal to the state the EXISTING chart-type calculator ends in for the bars up to the hovered bar. The direction flag is never a row.
/// The Count / Date / Days rows of the non-time-series chart types are not part of the overlays and are not tested here.
/// </summary>
public class DataWindowBlockChartOverlayTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 1);
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly ChartViewModel _chartViewModel;
    private readonly DataWindowViewModel _sut;
    private readonly SynchronousDispatcherService _dispatcher = new();

    public DataWindowBlockChartOverlayTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var messenger = new StrongReferenceMessenger();
        _chartViewModel = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!,
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Core.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
            _dispatcher,
            null, null!, null, null, null,
            messenger: messenger);
        _sut = new DataWindowViewModel(_chartViewModel, messenger, _dispatcher);
    }

    public void Dispose() => CultureInfo.CurrentCulture = _previousCulture;

    private const decimal Size = 3m;
    private const int Reversal = 2;
    private const int Lines = 3;

    private static List<CoreCandleData> ZigZag(int count)
    {
        var candles = new List<CoreCandleData>(count);
        decimal level = 100m;
        for (int i = 0; i < count; i++)
        {
            bool up = i % 26 < 12;
            decimal open = level;
            level += up ? 1.5m : -1.5m;
            candles.Add(new CoreCandleData(Start.AddDays(i), open, Math.Max(open, level) + 0.5m, Math.Min(open, level) - 0.5m, level, 1000));
        }

        return candles;
    }

    private static CoreIndicatorSettings Enabled(IndicatorType type)
    {
        var s = DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type);
        s.IsEnabled = true;
        switch (s.ParameterObject)
        {
            case CoreRenkoChartParameter r: r.SizingMode = ChartSizingMode.Fixed; r.ManualSize = Size; r.ReversalBricks = Reversal; break;
            case CorePointAndFigureChartParameter p: p.SizingMode = ChartSizingMode.Fixed; p.ManualSize = Size; p.ReversalBoxes = Reversal; break;
            case CoreKagiChartParameter k: k.SizingMode = ChartSizingMode.Fixed; k.ManualSize = Size; break;
            case CoreThreeLineBreakChartParameter t: t.NumberOfLines = Lines; break;
        }

        return s;
    }

    private static ZeroAllocCandleData[] Source(IReadOnlyList<CoreCandleData> bars)
        => bars.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();

    /// <summary>Close / Reversal / Update of the chart type itself after the bars 0..index (the existing calculators and the shared level formulas of the chart-type Data tab rows).</summary>
    private static (decimal Close, decimal Reversal, decimal? Update) Oracle(IndicatorType type, IReadOnlyList<CoreCandleData> bars)
    {
        switch (type)
        {
            case IndicatorType.Renko:
            {
                var a = RenkoCalculator.Calculate(Source(bars), new RenkoParameters(Size, Reversal, ChartRoundingMode.None));
                int last = a.Count - 1;
                decimal open = a.Opens.Span[last], close = a.Closes.Span[last];
                Assert.True(RenkoLevels.TryDescribe(close >= open, Math.Max(open, close), Math.Min(open, close), Size, Reversal, out decimal rev, out decimal upd));
                return (close, rev, upd);
            }
            case IndicatorType.PointAndFigure:
            {
                var a = PointAndFigureCalculator.Calculate(Source(bars), new PointAndFigureParameters(Size, Reversal, ChartRoundingMode.None));
                int last = a.Count - 1;
                Assert.True(PointAndFigureLevels.TryDescribe(a.Opens.Span, a.Highs.Span, a.Lows.Span, a.Closes.Span, last, Size, Reversal, out decimal rev, out decimal upd));
                return (a.Closes.Span[last], rev, upd);
            }
            case IndicatorType.Kagi:
            {
                var a = KagiCalculator.Calculate(Source(bars), new KagiParameters(Size, ChartRoundingMode.None));
                var segments = new List<CoreCandleData>(a.Count);
                for (int i = 0; i < a.Count; i++)
                    segments.Add(new CoreCandleData(a.Timestamps.Span[i], a.Opens.Span[i], a.Highs.Span[i], a.Lows.Span[i], a.Closes.Span[i], a.Volumes.Span[i]));
                Assert.True(KagiLevels.TryGetReversalPrice(segments, segments.Count - 1, Size, out decimal rev));
                return (segments[^1].Close, rev, null);
            }
            default:
            {
                // Three Line Break: the level the converter's own rule has to see broken for the NEXT reversal (decision D-4): extreme of the last min(count, lines) blocks, the last one included.
                var blocks = ThreeLineBreakConverter.Convert(bars, Lines, StockAnalyzer.Core.ChartConstants.ThreeLineBreakTrendThreshold, 0m);
                int index = blocks.Length - 1;
                int lookBack = Math.Min(blocks.Length, Lines);
                bool up = blocks[index].IsUp;
                decimal level = up ? decimal.MaxValue : decimal.MinValue;
                for (int i = 0; i < lookBack; i++)
                    level = up ? Math.Min(level, blocks[index - i].Low) : Math.Max(level, blocks[index - i].High);
                return (blocks[index].ClosePrice, level, null);
            }
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var type in new[] { IndicatorType.Renko, IndicatorType.PointAndFigure, IndicatorType.Kagi, IndicatorType.ThreeLineBreak })
            foreach (int index in new[] { 30, 45, 59, 79 })
                yield return new object[] { type, index };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheDataTabRows_AreTheOverlaySeriesAtTheHoveredBar_FormattedLikeAPrice_AndEqualTheChartTypeState(IndicatorType type, int hoveredIndex)
    {
        var candles = ZigZag(80);
        var setting = Enabled(type);
        var result = IndicatorFactory.CreateStatic(type, setting.ParameterObject!)!.Calculate(candles);
        bool hasUpdate = type is IndicatorType.Renko or IndicatorType.PointAndFigure;

        _chartViewModel.Candles = candles;
        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(setting);
        _chartViewModel.IndicatorResults = new Dictionary<string, IIndicatorResult> { { setting.Id, result } };

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = hoveredIndex, HoveredCandle = candles[hoveredIndex], ChartSymbol = "TEST" }));

        Assert.Single(_sut.IndicatorItems);
        var parent = _sut.IndicatorItems[0];
        Assert.True(parent.HasChildren);

        // Rows: Close, Reversal, (Update). The IsUpTrend helper series is never a row.
        string[] names = parent.Children.Select(c => c.Name).ToArray();
        Assert.Equal(hasUpdate ? new[] { "Close", "Reversal", "Update" } : new[] { "Close", "Reversal" }, names);

        // The text is the price format of the series value at the hovered bar ...
        Assert.Equal(DisplayFormat.Price(result.GetSeries("Main")[hoveredIndex]!.Value), parent.Children[0].Value);
        Assert.Equal(DisplayFormat.Price(result.GetSeries("Reversal")[hoveredIndex]!.Value), parent.Children[1].Value);
        if (hasUpdate) Assert.Equal(DisplayFormat.Price(result.GetSeries("Update")[hoveredIndex]!.Value), parent.Children[2].Value);

        // ... and that value is what the existing chart type shows for the bars up to the hovered bar.
        var oracle = Oracle(type, candles.Take(hoveredIndex + 1).ToList());
        Assert.Equal(DisplayFormat.Price(oracle.Close), parent.Children[0].Value);
        Assert.Equal(DisplayFormat.Price(oracle.Reversal), parent.Children[1].Value);
        if (hasUpdate) Assert.Equal(DisplayFormat.Price(oracle.Update!.Value), parent.Children[2].Value);
    }

    [Fact]
    public void BeforeTheFirstBlock_TheRowsShowNotAvailable_NotZero()
    {
        var candles = ZigZag(80);
        var setting = Enabled(IndicatorType.Renko);   // Fixed size 3: no brick before the price has moved by 3
        var result = IndicatorFactory.CreateStatic(IndicatorType.Renko, setting.ParameterObject!)!.Calculate(candles);
        Assert.Null(result.GetSeries("Main")[0]);

        _chartViewModel.Candles = candles;
        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(setting);
        _chartViewModel.IndicatorResults = new Dictionary<string, IIndicatorResult> { { setting.Id, result } };

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candles[0], ChartSymbol = "TEST" }));

        var parent = _sut.IndicatorItems.Single();
        Assert.All(parent.Children, c => Assert.Equal("N/A", c.Value));
    }
}
