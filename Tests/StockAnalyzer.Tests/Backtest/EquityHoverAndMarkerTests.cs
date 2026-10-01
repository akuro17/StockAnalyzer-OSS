using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Nearest-point lookup, trade marker placement and hover texts of the equity chart (all UI-free).</summary>
public class EquityHoverAndMarkerTests
{
    private static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Series(int count, int firstDayOffset = 0) =>
        Enumerable.Range(0, count)
            .Select(i => new EquityPoint(i, Day0.AddDays(firstDayOffset + i), 1_000m + i, 400m + i, 600m, 25m))
            .ToImmutableArray();

    private static BacktestTradeRow Trade(
        long id, DateTime entry, DateTime exit, TradeSide side = TradeSide.Long, bool forced = false, decimal net = 12.5m) => new()
        {
            TradeId = id,
            SideKind = side,
            EntryTime = entry,
            EntryPrice = 101.25m,
            ExitTime = exit,
            ExitPrice = 113.75m,
            Quantity = 2m,
            ClosedNet = net,
            PnLSemantic = BacktestMetricSemantic.Plus,
            IsForcedLiquidation = forced,
        };

    // ---- nearest point ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(600, 0)]    // 10 h after day 0: closer to point 0
    [InlineData(840, 1)]    // 14 h: closer to point 1
    [InlineData(1440, 1)]   // exactly point 1
    [InlineData(5760, 4)]   // exactly the last point
    public void FindIndex_ReturnsTheClosestPointInTime(int minutesAfterFirst, int expected)
    {
        ImmutableArray<EquityPoint> points = Series(5);

        Assert.Equal(expected, EquityNearestPoint.FindIndex(points, Day0.AddMinutes(minutesAfterFirst).Ticks));
    }

    [Fact]
    public void FindIndex_TiesGoToTheEarlierPoint()
    {
        ImmutableArray<EquityPoint> points = Series(3);

        Assert.Equal(0, EquityNearestPoint.FindIndex(points, Day0.AddHours(12).Ticks));
    }

    [Fact]
    public void FindIndex_IsMinusOneOutsideTheSpanBeyondTheTolerance()
    {
        ImmutableArray<EquityPoint> points = Series(5);
        long before = Day0.Ticks - 1;
        long after = Day0.AddDays(4).Ticks + 1;

        Assert.Equal(-1, EquityNearestPoint.FindIndex(points, before));
        Assert.Equal(-1, EquityNearestPoint.FindIndex(points, after));
        Assert.Equal(0, EquityNearestPoint.FindIndex(points, before, toleranceTicks: 1));
        Assert.Equal(4, EquityNearestPoint.FindIndex(points, after, toleranceTicks: 1));
        Assert.Equal(-1, EquityNearestPoint.FindIndex(points, after + 1, toleranceTicks: 1));
    }

    [Fact]
    public void FindIndex_HandlesEmptyAndSinglePointSeries()
    {
        Assert.Equal(-1, EquityNearestPoint.FindIndex(ImmutableArray<EquityPoint>.Empty, Day0.Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindIndex(default, Day0.Ticks));

        ImmutableArray<EquityPoint> single = Series(1);
        Assert.Equal(0, EquityNearestPoint.FindIndex(single, Day0.Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindIndex(single, Day0.AddDays(1).Ticks));
    }

    [Fact]
    public void FindIndex_AgreesWithABruteForceSearch()
    {
        ImmutableArray<EquityPoint> points = Series(40);
        for (int minutes = 0; minutes <= 39 * 24 * 60; minutes += 137)
        {
            long ticks = Day0.AddMinutes(minutes).Ticks;
            int expected = Enumerable.Range(0, points.Length)
                .OrderBy(i => Math.Abs(points[i].Timestamp.Ticks - ticks)).ThenBy(i => i).First();

            Assert.Equal(expected, EquityNearestPoint.FindIndex(points, ticks));
        }
    }

    [Fact]
    public void FindExactIndex_MatchesOnlyAnExactTimestamp()
    {
        ImmutableArray<EquityPoint> points = Series(5);

        Assert.Equal(3, EquityNearestPoint.FindExactIndex(points, Day0.AddDays(3).Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindExactIndex(points, Day0.AddDays(3).AddMinutes(1).Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindExactIndex(points, Day0.AddDays(-1).Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindExactIndex(points, Day0.AddDays(9).Ticks));
        Assert.Equal(-1, EquityNearestPoint.FindExactIndex(ImmutableArray<EquityPoint>.Empty, Day0.Ticks));
    }

    [Fact]
    public void Normalize_IsTheProjectionTheLayoutUsesForItsPoints()
    {
        ImmutableArray<EquityPoint> points = Series(8);
        long start = Day0.AddDays(2).Ticks;
        long end = Day0.AddDays(5).Ticks;

        EquityCurveLayout layout = EquityCurveLayout.Build(points, start, end);

        for (int i = 0; i < layout.Points.Length; i++)
        {
            Assert.Equal(
                layout.Points[i],
                EquityCurveLayout.Normalize(points[layout.FirstIndex + i], start, end, layout.YMin, layout.YMax));
        }
    }

    // ---- trade markers ----

    [Fact]
    public void Markers_SitOnTheEquityPointsAtTheTradeTimes_EntryBeforeExit()
    {
        ImmutableArray<EquityPoint> points = Series(30);
        ImmutableArray<BacktestTradeRow> trades = ImmutableArray.Create(
            Trade(1, Day0.AddDays(3), Day0.AddDays(9)),
            Trade(2, Day0.AddDays(12), Day0.AddDays(20), side: TradeSide.Short));

        ImmutableArray<EquityTradeMarker> markers = EquityTradeMarkers.Build(points, trades);

        Assert.Equal(4, markers.Length);
        Assert.Equal(new EquityTradeMarker(0, EquityMarkerKind.Entry, TradeSide.Long, false, 3), markers[0]);
        Assert.Equal(new EquityTradeMarker(0, EquityMarkerKind.Exit, TradeSide.Long, false, 9), markers[1]);
        Assert.Equal(new EquityTradeMarker(1, EquityMarkerKind.Entry, TradeSide.Short, false, 12), markers[2]);
        Assert.Equal(new EquityTradeMarker(1, EquityMarkerKind.Exit, TradeSide.Short, false, 20), markers[3]);
    }

    [Fact]
    public void Markers_AreOmittedForTimesBeforeTheEvaluatedSeries()
    {
        // The series was sliced past the evaluation start (days 10..): a trade entered on day 4 has no point to sit on.
        ImmutableArray<EquityPoint> points = Series(20, firstDayOffset: 10);
        ImmutableArray<BacktestTradeRow> trades = ImmutableArray.Create(Trade(1, Day0.AddDays(4), Day0.AddDays(15)));

        ImmutableArray<EquityTradeMarker> markers = EquityTradeMarkers.Build(points, trades);

        EquityTradeMarker only = Assert.Single(markers);
        Assert.Equal(EquityMarkerKind.Exit, only.Kind);
        Assert.Equal(5, only.PointIndex);
    }

    [Fact]
    public void Markers_OnlyAnExitCanBeAForcedLiquidation()
    {
        ImmutableArray<EquityPoint> points = Series(10);
        ImmutableArray<BacktestTradeRow> trades = ImmutableArray.Create(Trade(1, Day0.AddDays(1), Day0.AddDays(5), forced: true));

        ImmutableArray<EquityTradeMarker> markers = EquityTradeMarkers.Build(points, trades);

        Assert.False(markers[0].IsForcedLiquidation);
        Assert.True(markers[1].IsForcedLiquidation);
    }

    [Fact]
    public void Markers_AreEmptyWithoutTradesOrForAnInvalidSeries_AndSkipAnUnknownSide()
    {
        ImmutableArray<EquityPoint> points = Series(10);
        BacktestTradeRow trade = Trade(1, Day0.AddDays(1), Day0.AddDays(5));

        Assert.Empty(EquityTradeMarkers.Build(points, ImmutableArray<BacktestTradeRow>.Empty));
        Assert.Empty(EquityTradeMarkers.Build(ImmutableArray<EquityPoint>.Empty, ImmutableArray.Create(trade)));
        Assert.Empty(EquityTradeMarkers.Build(
            ImmutableArray.Create(points[1], points[0]), ImmutableArray.Create(trade)));
        Assert.Empty(EquityTradeMarkers.Build(points, ImmutableArray.Create(Trade(2, Day0.AddDays(1), Day0.AddDays(5), side: (TradeSide)5))));
    }

    [Fact]
    public void FindNearest_PicksTheClosestPlacementWithinTolerance_LaterWinsATie()
    {
        ImmutableArray<EquityMarkerPlacement> placements = ImmutableArray.Create(
            new EquityMarkerPlacement(0, 100d, 50d),
            new EquityMarkerPlacement(1, 106d, 50d),
            new EquityMarkerPlacement(2, 112d, 50d));

        Assert.Equal(1, EquityTradeMarkers.FindNearest(placements, 107d, 50d, 8d)?.MarkerIndex);
        Assert.Equal(1, EquityTradeMarkers.FindNearest(placements, 103d, 50d, 8d)?.MarkerIndex);
        Assert.Null(EquityTradeMarkers.FindNearest(placements, 100d, 70d, 8d));
        Assert.Equal(0, EquityTradeMarkers.FindNearest(placements, 100d, 58d, 8d)?.MarkerIndex);
        Assert.Null(EquityTradeMarkers.FindNearest(ImmutableArray<EquityMarkerPlacement>.Empty, 0d, 0d, 8d));
        Assert.Null(EquityTradeMarkers.FindNearest(default, 0d, 0d, 8d));
    }

    // ---- hover texts ----

    private static string Key(string key) => $"<{key}>";

    [Fact]
    public void PointLines_ListDateEquityCashMarketValueAndHeldMargin()
    {
        var point = new EquityPoint(3, new DateTime(2024, 3, 5, 14, 30, 0, DateTimeKind.Utc), 1234.5m, 400m, 834.5m, 25m);

        ImmutableArray<string> lines = EquityHoverText.PointLines(point, Key);

        Assert.Equal(
            new[]
            {
                "<Backtest_EquityHover_Date>: 2024-03-05 14:30",
                "<Backtest_EquityHover_Equity>: 1234.5000",
                "<Backtest_EquityHover_Cash>: 400.0000",
                "<Backtest_EquityHover_MarketValue>: 834.5000",
                "<Backtest_EquityHover_HeldMargin>: 25.0000",
            },
            lines);
    }

    [Fact]
    public void MarkerLines_DescribeTheEventsSideTimePriceQuantityAndNet()
    {
        BacktestTradeRow trade = Trade(7, Day0.AddDays(2), Day0.AddDays(6), side: TradeSide.Short, net: -3.25m);

        ImmutableArray<string> entry = EquityHoverText.MarkerLines(trade, EquityMarkerKind.Entry, false, Key);
        ImmutableArray<string> exit = EquityHoverText.MarkerLines(trade, EquityMarkerKind.Exit, false, Key);

        Assert.Equal(
            new[]
            {
                "<Backtest_TradeList_Header_TradeId>7  <Btn_PositionShort>  <Backtest_EquityMarker_Entry>",
                "<Backtest_TradeList_Header_EntryTime>: 2024-01-03 00:00",
                "<Backtest_TradeList_Header_EntryPrice>: 101.2500",
                "<Backtest_TradeList_Header_Quantity>: 2.0000",
                "<Backtest_TradeList_Header_ClosedNet>: -3.2500",
            },
            entry);
        Assert.Equal("<Backtest_TradeList_Header_TradeId>7  <Btn_PositionShort>  <Backtest_EquityMarker_Exit>", exit[0]);
        Assert.Equal("<Backtest_TradeList_Header_ExitTime>: 2024-01-07 00:00", exit[1]);
        Assert.Equal("<Backtest_TradeList_Header_ExitPrice>: 113.7500", exit[2]);
    }

    [Fact]
    public void MarkerLines_AForcedLiquidationExitAddsItsBadge()
    {
        BacktestTradeRow trade = Trade(1, Day0, Day0.AddDays(1), forced: true);

        ImmutableArray<string> lines = EquityHoverText.MarkerLines(trade, EquityMarkerKind.Exit, true, Key);

        Assert.Equal("<Backtest_TradeList_ForcedLiquidation_Badge>", lines[^1]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public void HoverTexts_AreWrittenInvariantWhateverTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var point = new EquityPoint(0, Day0, 1234.5m, 0m, 0m, 0m);

            Assert.Equal("1234.5000", EquityHoverText.AxisEquity(point.Equity));
            Assert.Equal("2024-01-01 00:00", EquityHoverText.AxisTime(point.Timestamp));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void HoverCaptionKeys_AreExactlyTheOnesTheTextsRequest()
    {
        // The localizer records every key the texts ask for, so the set cannot drift from the code (the locale tests resolve each one).
        var requested = new System.Collections.Generic.HashSet<string>();
        string Record(string key)
        {
            requested.Add(key);
            return key;
        }

        EquityHoverText.PointLines(new EquityPoint(0, Day0, 1m, 1m, 1m, 1m), Record);
        foreach (TradeSide side in new[] { TradeSide.Long, TradeSide.Short })
        {
            foreach (EquityMarkerKind kind in new[] { EquityMarkerKind.Entry, EquityMarkerKind.Exit })
            {
                EquityHoverText.MarkerLines(Trade(1, Day0, Day0.AddDays(1), side), kind, true, Record);
            }
        }

        Assert.Equal(
            new[]
            {
                "Backtest_EquityHover_Cash", "Backtest_EquityHover_Date", "Backtest_EquityHover_Equity", "Backtest_EquityHover_HeldMargin",
                "Backtest_EquityHover_MarketValue", "Backtest_EquityMarker_Entry", "Backtest_EquityMarker_Exit", "Backtest_TradeList_ForcedLiquidation_Badge",
                "Backtest_TradeList_Header_ClosedNet", "Backtest_TradeList_Header_EntryPrice", "Backtest_TradeList_Header_EntryTime",
                "Backtest_TradeList_Header_ExitPrice", "Backtest_TradeList_Header_ExitTime", "Backtest_TradeList_Header_Quantity",
                "Backtest_TradeList_Header_TradeId", "Btn_PositionLong", "Btn_PositionShort",
            },
            requested.OrderBy(k => k, StringComparer.Ordinal));
    }
}
