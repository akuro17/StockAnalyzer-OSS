using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The Relative Performance snapshot the chart draws (comparison series, last-value labels, value range, series order) is pinned against
/// <see cref="ComparisonSeriesCalculator"/>, the single definition of the mode formulas that sa_calculate_relative_performance also uses.
/// </summary>
public class ChartDataCoordinatorComparisonSnapshotTests
{
    private const string Primary = "AAA";
    private const string Second = "CCC";
    private const string Gappy = "BBB";
    private const int Bars = 60;
    private const int ZScorePeriod = 5;
    private static readonly Rect ControlBounds = new(0, 0, 1200, 700);

    private static ComparisonAlignedData AlignedData()
    {
        CoreCandleData[] candles = SwingingCandles.Create(Bars);
        DateTime[] timestamps = candles.Select(c => c.Timestamp).ToArray();

        CandleData?[] Build(Func<int, decimal?> close) => Enumerable.Range(0, Bars)
            .Select(i => close(i) is { } value ? (CandleData?)new CandleData(timestamps[i], value, value, value, value, 0) : null)
            .ToArray();

        var series = new Dictionary<string, CandleData?[]>
        {
            [Primary] = Build(i => candles[i].Close),
            [Second] = Build(i => 50m + (i % 9) * 3m + i * 0.4m),
            [Gappy] = Build(i => i < 7 || i is 20 or 21 or 33 ? null : 80m - (i % 5) * 2m + i * 0.1m),
        };
        return new ComparisonAlignedData(Primary, timestamps, series, new List<string>());
    }

    private sealed class FixedAligner : IComparisonDataAligner
    {
        private readonly ComparisonAlignedData _data;

        public FixedAligner(ComparisonAlignedData data) => _data = data;

        public int MaxComparisonSymbols { get; set; } = 5;

        public Task<ComparisonAlignedData> AlignAsync(string primarySymbol, IReadOnlyList<string> comparisonSymbols, TimeFrame timeFrame, int candleCount, CancellationToken ct = default)
            => Task.FromResult(_data);
    }

    private static async Task<(ChartDataSnapshot Snapshot, ComparisonAlignedData Aligned)> SnapshotAsync(ComparisonMode mode)
    {
        ComparisonAlignedData aligned = AlignedData();
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });
        var vm = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!,
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            new FixedAligner(aligned),
            messenger: new StrongReferenceMessenger());

        vm.Symbol = Primary;
        vm.Candles = SwingingCandles.Create(Bars);
        vm.ComparisonMode = mode;
        vm.ComparisonZScorePeriod = ZScorePeriod;
        vm.ChartType = ChartType.RelativePerformance;

        // The chart loads its comparison data in the background; the fixed aligner hands back exactly the data above.
        for (int waited = 0; vm.ComparisonData is null && waited < 5000; waited += 20)
        {
            await Task.Delay(20);
        }

        Assert.Same(aligned, vm.ComparisonData);
        new ChartDataCoordinator().UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        return (vm.CurrentSnapshot!, aligned);
    }

    public static IEnumerable<object[]> Modes() => Enum.GetValues<ComparisonMode>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ComparisonSeries_EqualTheSharedCalculatorOverTheVisibleWindow(ComparisonMode mode)
    {
        var (snapshot, aligned) = await SnapshotAsync(mode);

        Assert.NotNull(snapshot.ComparisonSeries);
        Assert.True(snapshot.VisibleCandleCount > 10);
        var expected = ComparisonSeriesCalculator.Calculate(aligned, snapshot.StartIndex, snapshot.VisibleCandleCount, mode, ZScorePeriod);

        Assert.Equal(aligned.Series.Keys.OrderBy(k => k), snapshot.ComparisonSeries!.Keys);
        foreach (string symbol in aligned.Series.Keys)
        {
            Assert.Equal(expected.Series[symbol].Values, snapshot.ComparisonSeries[symbol]);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task LastValueLabels_AreTheFormattedLastDefinedValueOfEachSeries(ComparisonMode mode)
    {
        var (snapshot, _) = await SnapshotAsync(mode);

        foreach ((string symbol, decimal?[] values) in snapshot.ComparisonSeries!)
        {
            decimal? last = values.LastOrDefault(v => v.HasValue);
            if (last.HasValue)
            {
                Assert.Equal(DisplayFormat.ComparisonValue(mode, last.Value), snapshot.RenderingMetadata[symbol]);
            }
            else
            {
                Assert.False(snapshot.RenderingMetadata.ContainsKey(symbol));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ValueRange_CoversTheDefinedValuesAndLeavesOutThePrimaryRowOfRatioAndSpread(ComparisonMode mode)
    {
        var (snapshot, _) = await SnapshotAsync(mode);
        bool primaryIsConstant = mode is ComparisonMode.Ratio or ComparisonMode.Spread;

        var counted = snapshot.ComparisonSeries!
            .Where(kv => !(primaryIsConstant && kv.Key == Primary))
            .SelectMany(kv => kv.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        Assert.NotEmpty(counted);
        Assert.True(snapshot.MinPrice <= counted.Min());
        Assert.True(snapshot.MaxPrice >= counted.Max());
        if (primaryIsConstant)
        {
            // A constant primary row (1 or 0) must not stretch the range: the range hugs the other series.
            Assert.True(snapshot.MaxPrice - snapshot.MinPrice < (counted.Max() - counted.Min()) * 2m + 1m);
        }
    }

    [Fact]
    public async Task Performance_IsRebasedAtTheFirstVisibleBarOfEachSymbol()
    {
        var (snapshot, aligned) = await SnapshotAsync(ComparisonMode.Performance);
        CandleData?[] primary = aligned.Series[Primary];
        decimal baseClose = primary[snapshot.StartIndex]!.Value.Close;
        decimal?[] values = snapshot.ComparisonSeries![Primary];

        Assert.Equal(0m, values[0]);
        Assert.Equal((primary[snapshot.StartIndex + snapshot.VisibleCandleCount - 1]!.Value.Close - baseClose) / baseClose * 100m, values[^1]);
    }
}
