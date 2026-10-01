using System;
using System.Collections.Immutable;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

public enum EquityMarkerKind
{
    Entry,
    Exit,
}

/// <summary>
/// One trade event on the equity chart: which trade (index into the trade list it was built from), entry or exit, the trade's side, whether
/// the exit was a forced liquidation (never true for an entry) and the index of the equity point it sits on.
/// </summary>
public readonly record struct EquityTradeMarker(
    int TradeIndex, EquityMarkerKind Kind, TradeSide Side, bool IsForcedLiquidation, int PointIndex);

/// <summary>A marker's screen position (DIP) and its index in the marker list; the unit of hover hit testing.</summary>
public readonly record struct EquityMarkerPlacement(int MarkerIndex, double X, double Y);

/// <summary>
/// Pure placement of trade events on the equity series (Avalonia-UI-free). A marker belongs on the Equity value at the event's time, never
/// on the trade price: the chart's Y axis is account equity (currency), and a price on it would mix two quantities. An event whose time is
/// not a time of the equity series (e.g. a trade before the evaluation start, which the series was sliced past) has no marker.
/// </summary>
public static class EquityTradeMarkers
{
    /// <summary>
    /// Entry and exit markers of every trade whose time is exactly a time of <paramref name="points"/>, in trade order (entry before exit).
    /// Empty for a series that breaks <see cref="EquitySeriesRule"/>. A trade whose side is not a defined <see cref="TradeSide"/> has no markers.
    /// </summary>
    public static ImmutableArray<EquityTradeMarker> Build(ImmutableArray<EquityPoint> points, ImmutableArray<BacktestTradeRow> trades)
    {
        if (trades.IsDefaultOrEmpty || !EquitySeriesRule.IsValid(points))
        {
            return ImmutableArray<EquityTradeMarker>.Empty;
        }

        ImmutableArray<EquityTradeMarker>.Builder builder = ImmutableArray.CreateBuilder<EquityTradeMarker>();
        for (int i = 0; i < trades.Length; i++)
        {
            BacktestTradeRow trade = trades[i];
            if (!Enum.IsDefined(trade.SideKind))
            {
                continue;
            }

            int entryIndex = EquityNearestPoint.FindExactIndex(points, trade.EntryTime.Ticks);
            if (entryIndex >= 0)
            {
                builder.Add(new EquityTradeMarker(i, EquityMarkerKind.Entry, trade.SideKind, IsForcedLiquidation: false, entryIndex));
            }

            int exitIndex = EquityNearestPoint.FindExactIndex(points, trade.ExitTime.Ticks);
            if (exitIndex >= 0)
            {
                builder.Add(new EquityTradeMarker(i, EquityMarkerKind.Exit, trade.SideKind, trade.IsForcedLiquidation, exitIndex));
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The placement nearest to (<paramref name="x"/>, <paramref name="y"/>) within <paramref name="tolerance"/> DIP, or null. Of two equally
    /// near placements the later one (drawn on top) wins.
    /// </summary>
    public static EquityMarkerPlacement? FindNearest(ImmutableArray<EquityMarkerPlacement> placements, double x, double y, double tolerance)
    {
        if (placements.IsDefaultOrEmpty)
        {
            return null;
        }

        EquityMarkerPlacement? best = null;
        double bestSquared = tolerance * tolerance;
        for (int i = 0; i < placements.Length; i++)
        {
            EquityMarkerPlacement placement = placements[i];
            double dx = placement.X - x;
            double dy = placement.Y - y;
            double squared = (dx * dx) + (dy * dy);
            if (squared <= bestSquared)
            {
                best = placement;
                bestSquared = squared;
            }
        }
        return best;
    }
}
