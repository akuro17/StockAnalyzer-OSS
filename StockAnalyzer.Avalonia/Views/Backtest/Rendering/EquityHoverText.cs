using System;
using System.Collections.Immutable;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// The texts of the equity chart's hover readouts (crosshair value panel, axis labels, trade marker panel), built with an injected
/// localizer so they are testable without the UI. Numbers and dates are written by <see cref="EquityLabelFormats"/> (invariant culture);
/// only the captions are localized. Trade detail reuses the trade list's own header keys (one wording per concept); only the
/// equity-point and entry/exit words are new. The control builds these texts when the hover changes, never per paint.
/// </summary>
public static class EquityHoverText
{
    public const string DateKey = "Backtest_EquityHover_Date";
    public const string EquityKey = "Backtest_EquityHover_Equity";
    public const string CashKey = "Backtest_EquityHover_Cash";
    public const string MarketValueKey = "Backtest_EquityHover_MarketValue";
    public const string HeldMarginKey = "Backtest_EquityHover_HeldMargin";
    public const string EntryKey = "Backtest_EquityMarker_Entry";
    public const string ExitKey = "Backtest_EquityMarker_Exit";

    /// <summary>The time label of the crosshair on the X axis.</summary>
    public static string AxisTime(DateTime timestamp) => timestamp.ToString(EquityLabelFormats.Instant, EquityLabelFormats.Culture);

    /// <summary>The Equity label of the crosshair on the Y axis.</summary>
    public static string AxisEquity(decimal equity) => Decimal(equity);

    /// <summary>The value panel of an equity point: date, equity, cash, market value, held margin.</summary>
    public static ImmutableArray<string> PointLines(EquityPoint point, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        return ImmutableArray.Create(
            Line(localize(DateKey), AxisTime(point.Timestamp)),
            Line(localize(EquityKey), AxisEquity(point.Equity)),
            Line(localize(CashKey), Decimal(point.Cash)),
            Line(localize(MarketValueKey), Decimal(point.MarketValue)),
            Line(localize(HeldMarginKey), Decimal(point.HeldMargin)));
    }

    /// <summary>
    /// The detail panel of one trade event: id, side and entry/exit on the first line, then the event's time and price, the quantity and the
    /// net result; a forced liquidation exit adds its badge as a last line.
    /// </summary>
    public static ImmutableArray<string> MarkerLines(
        BacktestTradeRow trade, EquityMarkerKind kind, bool isForcedLiquidation, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(localize);
        string side = localize(trade.SideKind == TradeSide.Long ? "Btn_PositionLong" : "Btn_PositionShort");
        bool isEntry = kind == EquityMarkerKind.Entry;
        ImmutableArray<string>.Builder lines = ImmutableArray.CreateBuilder<string>(6);
        lines.Add($"{localize("Backtest_TradeList_Header_TradeId")}{trade.TradeId.ToString(EquityLabelFormats.Culture)}  {side}  {localize(isEntry ? EntryKey : ExitKey)}");
        lines.Add(Line(
            localize(isEntry ? "Backtest_TradeList_Header_EntryTime" : "Backtest_TradeList_Header_ExitTime"),
            AxisTime(isEntry ? trade.EntryTime : trade.ExitTime)));
        lines.Add(Line(
            localize(isEntry ? "Backtest_TradeList_Header_EntryPrice" : "Backtest_TradeList_Header_ExitPrice"),
            Decimal(isEntry ? trade.EntryPrice : trade.ExitPrice)));
        lines.Add(Line(localize("Backtest_TradeList_Header_Quantity"), Decimal(trade.Quantity)));
        lines.Add(Line(localize("Backtest_TradeList_Header_ClosedNet"), Decimal(trade.ClosedNet)));
        if (isForcedLiquidation)
        {
            lines.Add(localize("Backtest_TradeList_ForcedLiquidation_Badge"));
        }
        return lines.ToImmutable();
    }

    private static string Decimal(decimal value) => value.ToString(EquityLabelFormats.Decimal, EquityLabelFormats.Culture);

    private static string Line(string label, string value) => $"{label}: {value}";
}
