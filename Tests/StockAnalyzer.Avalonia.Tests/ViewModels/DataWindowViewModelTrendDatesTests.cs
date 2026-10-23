using StockAnalyzer.Core.Services;
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
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Data tab rows "reversal date", "update date" and "days" of Renko, Point and Figure, Kagi and Three Line Break. They follow the hovered element of the
/// drawn series (the newest one when nothing is hovered), are empty for every other chart, and are formatted with the invariant culture (the class runs under de-DE).
/// Vectors are the ones of the implementation plan (2026-01-dd).
/// </summary>
public class DataWindowViewModelTrendDatesTests : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly ChartViewModel _chartViewModel;
    private readonly DataWindowViewModel _sut;
    private readonly IMessenger _messenger;

    public DataWindowViewModelTrendDatesTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var messenger = new StrongReferenceMessenger();
        _messenger = messenger;
        _chartViewModel = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!,
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Core.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
            new SynchronousDispatcherService(),
            null, null!, null, null, null,
            messenger: messenger);
        _sut = new DataWindowViewModel(_chartViewModel, messenger, new SynchronousDispatcherService());
    }

    public void Dispose() => CultureInfo.CurrentCulture = _previousCulture;

    private static DateTime Jan(int day) => new(2026, 1, day);

    private static CoreCandleData Up(DateTime date) => new(date, 10m, 12m, 10m, 12m, 0);

    private static CoreCandleData Down(DateTime date) => new(date, 12m, 12m, 10m, 10m, 0);

    private static CoreCandleData Flat(DateTime date) => new(date, 10m, 10m, 10m, 10m, 0);

    private static List<CoreCandleData> LoadedCandles() =>
        Enumerable.Range(0, 50).Select(i => new CoreCandleData(Jan(1).AddDays(i), 100m + i, 101m + i, 99m + i, 100.5m + i, 1000)).ToList();

    // ---- The drawn series of each chart type and what the three rows must show for every element.

    private sealed record Drawn(List<CoreCandleData> Series, ElementDates[]? Dates, (string? Reversal, string Update, string? Days)[] Expected);

    private static string D(int day) => "2026-01-" + day.ToString("00", CultureInfo.InvariantCulture);

    private static Drawn RenkoLike() => new(
        new List<CoreCandleData> { Up(Jan(5)), Up(Jan(6)), Down(Jan(9)), Down(Jan(10)), Down(Jan(10)), Up(Jan(15)) },
        null,
        new (string?, string, string?)[]
        {
            (null, D(5), null), (null, D(6), null), (D(9), D(9), "0"), (D(9), D(10), "1"), (D(9), D(10), "1"), (D(15), D(15), "0"),
        });

    private static Drawn Pnf() => new(
        new List<CoreCandleData> { Up(Jan(8)), Down(Jan(14)), Up(Jan(20)) },
        new[] { new ElementDates(Jan(5), Jan(8)), new ElementDates(Jan(12), Jan(14)), new ElementDates(Jan(20), Jan(20)) },
        new (string?, string, string?)[] { (null, D(8), null), (D(12), D(14), "2"), (D(20), D(20), "0") });

    private static Drawn Kagi() => new(
        new List<CoreCandleData> { Flat(Jan(2)), Up(Jan(6)), Up(Jan(7)), Down(Jan(11)), Down(Jan(12)), Up(Jan(22)) },
        new[]
        {
            new ElementDates(Jan(2), Jan(2)), new ElementDates(Jan(5), Jan(6)), new ElementDates(Jan(7), Jan(7)),
            new ElementDates(Jan(10), Jan(11)), new ElementDates(Jan(12), Jan(12)), new ElementDates(Jan(20), Jan(22)),
        },
        new (string?, string, string?)[]
        {
            (null, D(2), null), (null, D(7), null), (null, D(7), null), (D(10), D(12), "2"), (D(10), D(12), "2"), (D(20), D(22), "2"),
        });

    private static Drawn For(ChartType chartType) => chartType switch
    {
        ChartType.PointAndFigure => Pnf(),
        ChartType.Kagi => Kagi(),
        _ => RenkoLike(),
    };

    public static IEnumerable<object[]> ChartTypes() => new[]
    {
        new object[] { ChartType.Renko }, new object[] { ChartType.ThreeLineBreak },
        new object[] { ChartType.PointAndFigure }, new object[] { ChartType.Kagi },
    };

    private void Show(ChartType chartType, Drawn drawn, int windowStart = 0)
    {
        _chartViewModel.Candles = LoadedCandles();
        _chartViewModel.ChartType = chartType;
        _chartViewModel.VisibleStartIndex = windowStart;
        _chartViewModel.CurrentSnapshot = new ChartDataSnapshot(
            drawn.Series, chartType: chartType, startIndex: windowStart, allCandles: drawn.Series, elementDates: drawn.Dates);
    }

    /// <summary>An element that is neither the first nor the last of the drawn series.</summary>
    private static int MiddleElement(Drawn drawn) => drawn.Series.Count / 2;

    private static CrosshairPositionChangedMessage Hover(int windowPosition, CoreCandleData element) =>
        new(new CrosshairPositionData { CandleIndex = windowPosition, HoveredCandle = element, ChartSymbol = "TEST" });

    private static CrosshairPositionChangedMessage PointerLeft() =>
        new(new CrosshairPositionData { CandleIndex = -1, HoveredCandle = null, ChartSymbol = "TEST" });

    private void AssertRows((string? Reversal, string Update, string? Days) expected, string context)
    {
        Assert.True(expected.Reversal == (_sut.TrendReversalDateText.Length == 0 ? null : _sut.TrendReversalDateText),
            $"{context}: reversal '{_sut.TrendReversalDateText}' expected '{expected.Reversal}'");
        Assert.True(expected.Update == _sut.TrendUpdateDateText, $"{context}: update '{_sut.TrendUpdateDateText}' expected '{expected.Update}'");
        Assert.True(expected.Days == (_sut.TrendDaysText.Length == 0 ? null : _sut.TrendDaysText),
            $"{context}: days '{_sut.TrendDaysText}' expected '{expected.Days}'");
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Receive_OnEveryElement_ShowsTheDatesOfTheHoveredElement(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn);

        for (int i = 0; i < drawn.Series.Count; i++)
        {
            _sut.Receive(Hover(i, drawn.Series[i]));

            AssertRows(drawn.Expected[i], $"{chartType} element {i}");
        }
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Receive_OnAScrolledWindow_AddsTheStartOfTheWindowToTheHoveredPosition(ChartType chartType)
    {
        var drawn = For(chartType);
        int element = MiddleElement(drawn);
        Show(chartType, drawn, windowStart: element - 1);

        _sut.Receive(Hover(1, drawn.Series[element]));   // window position 1 + start (element - 1) = the element

        AssertRows(drawn.Expected[element], $"{chartType} scrolled");
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Receive_WhenThePointerLeaves_ShowsTheNewestElement(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn);
        _sut.Receive(Hover(1, drawn.Series[1]));

        _sut.Receive(PointerLeft());

        AssertRows(drawn.Expected[^1], $"{chartType} pointer left");
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Receive_WhenTheHoveredPositionIsBeyondTheSeries_ShowsTheNewestElement(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn);

        _sut.Receive(Hover(99, new CoreCandleData(new DateTime(2030, 1, 1), 99m, 99m, 99m, 99m, 0)));

        AssertRows(drawn.Expected[^1], $"{chartType} out of range");
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Refresh_WithoutAHover_ShowsTheNewestElement(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        AssertRows(drawn.Expected[^1], $"{chartType} refresh");
    }

    [Theory]
    [MemberData(nameof(ChartTypes))]
    public void Refresh_AfterAHover_KeepsTheHoveredElement(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn);
        int element = MiddleElement(drawn);
        _sut.Receive(Hover(element, drawn.Series[element]));

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        AssertRows(drawn.Expected[element], $"{chartType} refresh after hover");
    }

    [Theory]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.Kagi)]
    public void Receive_WhenTheSnapshotCarriesNoDates_ShowsNothingInsteadOfAWrongDate(ChartType chartType)
    {
        var drawn = For(chartType);
        Show(chartType, drawn with { Dates = null });

        _sut.Receive(Hover(1, drawn.Series[1]));

        Assert.Equal(string.Empty, _sut.TrendReversalDateText);
        Assert.Equal(string.Empty, _sut.TrendUpdateDateText);
        Assert.Equal(string.Empty, _sut.TrendDaysText);
    }

    [Fact]
    public void Receive_WhenTheSnapshotBelongsToAnotherChartType_ShowsNothing()
    {
        var drawn = Pnf();
        Show(ChartType.PointAndFigure, drawn);
        _chartViewModel.ChartType = ChartType.Kagi;   // the new snapshot has not arrived yet

        _sut.Receive(Hover(1, drawn.Series[1]));

        Assert.Equal(string.Empty, _sut.TrendUpdateDateText);
    }

    [Theory]
    [InlineData(ChartType.Candlestick)]
    [InlineData(ChartType.ReverseWatch)]
    [InlineData(ChartType.RelativePerformance)]
    public void Receive_OnAChartThatIsNotOneOfTheFour_ClearsTheRows(ChartType chartType)
    {
        var drawn = RenkoLike();
        Show(ChartType.Renko, drawn);
        _sut.Receive(Hover(3, drawn.Series[3]));
        Assert.NotEqual(string.Empty, _sut.TrendUpdateDateText);

        _chartViewModel.ChartType = chartType;
        _sut.Receive(Hover(3, drawn.Series[3]));

        Assert.Equal(string.Empty, _sut.TrendReversalDateText);
        Assert.Equal(string.Empty, _sut.TrendUpdateDateText);
        Assert.Equal(string.Empty, _sut.TrendDaysText);
    }

    [Fact]
    public void Receive_OnATimeSeriesChart_LeavesTheRowsEmptyAndFormatsTheHeaderAsIsoDate()
    {
        _chartViewModel.Candles = LoadedCandles();
        _chartViewModel.ChartType = ChartType.Candlestick;

        _sut.Receive(Hover(0, new CoreCandleData(new DateTime(2026, 1, 1, 10, 0, 0), 1m, 1m, 1m, 1m, 0)));

        Assert.Equal(string.Empty, _sut.TrendUpdateDateText);
        Assert.Equal("2026-01-01 10:00", _sut.DateText);
    }
}
