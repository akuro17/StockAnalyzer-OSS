using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Plan ChartPatterns56 V2, run V2-2, integration spec 4.1 (verified against <c>DataWindowViewModel</c>): a bidirectional chart-pattern flag shows ONE ROW PER SIDE in the Data tab (the bullish and the bearish series),
/// never the helper series <c>Main</c> and <c>Direction</c>; a flag without direction shows its single series. The rows are those of the settings of the catalog (two colour entries), so the Data tab needs no
/// code of its own for the flags.
/// </summary>
public class DataWindowPatternFlagRowsTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 1);
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly ChartViewModel _chartViewModel;
    private readonly DataWindowViewModel _sut;
    private readonly SynchronousDispatcherService _dispatcher = new();

    public DataWindowPatternFlagRowsTests()
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

    private const int Bars = 300;
    private const int Hovered = 250;

    /// <summary>A repeating swing: 12 bars up, 14 bars down, so the confirmed-pivot detectors have material.</summary>
    private static List<CoreCandleData> Swings()
    {
        var candles = new List<CoreCandleData>(Bars);
        decimal level = 100m;
        for (int i = 0; i < Bars; i++)
        {
            bool up = i % 26 < 12;
            decimal open = level;
            level += up ? 1.5m : -1.5m;
            candles.Add(new CoreCandleData(Start.AddDays(i), open, Math.Max(open, level) + 0.5m, Math.Min(open, level) - 0.5m, level, 1000));
        }

        return candles;
    }

    private string[] RowNames(IndicatorType type)
    {
        List<CoreCandleData> candles = Swings();
        CoreIndicatorSettings setting = DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type);
        setting.IsEnabled = true;
        IIndicatorResult result = IndicatorFactory.CreateStatic(type, setting.ParameterObject!)!.Calculate(candles);
        Assert.True(result.IsSuccessful, result.ErrorMessage);

        _chartViewModel.Candles = candles;
        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(setting);
        _chartViewModel.IndicatorResults = new Dictionary<string, IIndicatorResult> { { setting.Id, result } };

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = Hovered, HoveredCandle = candles[Hovered], ChartSymbol = "TEST" }));

        var item = Assert.Single(_sut.IndicatorItems);
        return item.HasChildren ? item.Children.Select(c => c.Name).ToArray() : new[] { item.Name };
    }

    public static IEnumerable<object[]> Bidirectional() => PatternFlagIndicatorCatalog.Rows.Where(r => r.HasDirection).Select(r => new object[] { r.Type });

    [Theory]
    [MemberData(nameof(Bidirectional))]
    public void ABidirectionalFlag_ShowsOneRowPerSide_AndNoHelperRows(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);

        string[] names = RowNames(type);

        Assert.Equal(2, names.Length);
        Assert.Contains(names, n => n.Contains(row.BullishSeries!, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains(row.BearishSeries!, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Direction", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Main", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFlagWithoutDirection_ShowsItsSingleSeries()
    {
        string[] names = RowNames(IndicatorType.MarketStructureHhHl);

        Assert.Single(names);
    }

    [Theory]
    [InlineData(IndicatorType.GranvilleBuySignal)]
    [InlineData(IndicatorType.GranvilleSellSignal)]
    public void ARulesFlag_ShowsFourRuleRows_AndNoHelperRows(IndicatorType type)
    {
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);
        string[] names = RowNames(type);

        Assert.Equal(row.RuleSeries!, names);
        Assert.DoesNotContain(IndicatorResult.MainSeriesName, names);
        Assert.DoesNotContain(StockAnalyzer.Core.Models.Indicators.PatternFlags.Rules.CoreGranvilleIndicatorBase.RuleCodeSeriesName, names);
    }
}
