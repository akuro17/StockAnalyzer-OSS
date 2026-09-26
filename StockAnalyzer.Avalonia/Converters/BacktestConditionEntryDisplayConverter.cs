using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Task 6 (Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.4): formats a
/// <see cref="BacktestConditionEntry"/> as "{Left} {OperatorSymbol} {Right or RightNumericValue}", the
/// same display shape <c>ScreenerIndicatorEntry.DisplayName</c> uses for the Screener's own condition
/// list. Kept as an Avalonia-layer converter rather than a property on the Core model itself, since
/// display-string formatting is a UI concern, not a calculation concern.
/// </summary>
public class BacktestConditionEntryDisplayConverter : IValueConverter
{
    public static readonly BacktestConditionEntryDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is BacktestConditionEntry entry
            ? BacktestConditionFormatter.Format(entry)
            : value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Pure formatter shared by the legacy converter adapter and immutable result presentation.</summary>
public static class BacktestConditionFormatter
{
    public static string Format(BacktestConditionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string left = DescribeSide(entry.Left);
        string op = entry.Operator.ToSymbolString();
        string right = entry.TargetMode == RightHandTargetMode.Indicator && entry.Right is not null
            ? DescribeSide(entry.Right)
            : entry.RightNumericValue.ToString(CultureInfo.InvariantCulture);
        return $"{left} {op} {right}";
    }

    public static string FormatExecutionExpression(IReadOnlyList<BacktestConditionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return string.Empty;

        BacktestConditionExecutionPaths paths = BacktestConditionExecutionPaths.Create(entries);
        var expressions = new List<string>(5);
        AddPath("LongEntry", paths.LongEntry);
        AddPath("ShortEntry", paths.ShortEntry);
        AddPath("Exit", paths.Exit);
        AddPath("ReverseLong", paths.ReverseLong);
        AddPath("ReverseShort", paths.ReverseShort);
        return string.Join("; ", expressions);

        void AddPath(string name, IReadOnlyList<BacktestConditionEntry> path)
        {
            if (path.Count == 0) return;
            string expression = $"({Format(path[0])})";
            for (int i = 1; i < path.Count; i++)
            {
                expression = $"({expression} {path[i - 1].LogicalOperator.ToString().ToUpperInvariant()} ({Format(path[i])}))";
            }
            expressions.Add($"{name}={expression}");
        }
    }

    public static string FormatAudit(
        BacktestConditionEntry entry,
        int savedIndex,
        string executionExpression)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"Index={savedIndex}; Role={entry.Role}; Position={entry.Position}; ConnectorToNext={entry.LogicalOperator}; " +
               $"Left=[{DescribeSideAudit(entry.Left)}]; Right=[{DescribeRightAudit(entry)}]; " +
               $"ExecutionPaths={executionExpression}";
    }

    private static string DescribeSide(BacktestConditionSide side)
    {
        // SAで改善 (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): a "Price" side's
        // meaningful name IS its PriceSource ("High"/"Low"/...), not the generic "Price" indicator-type
        // description - showing both ("Price(High)") would be redundant, and the OutputName (which for a
        // Price side is just the same PriceType name again, plumbed through for dedupe purposes) would
        // otherwise duplicate it in the parenthetical below.
        if (side.IndicatorType == IndicatorType.Price && side.PriceSource is { } priceSource)
        {
            string priceName = PriceDataHelper.FormatPriceTypeLabel(priceSource);
            var priceParts = new List<string>();
            if (side.Frame is TimeFrame priceFrame) priceParts.Add(priceFrame.ToString());
            if (side.Offset != 0) priceParts.Add($"-{side.Offset}");
            return priceParts.Count > 0 ? $"{priceName}({string.Join(", ", priceParts)})" : priceName;
        }

        // SAで改善 (this round): short/formal name (e.g. "SMA") instead of the long localized
        // Description (e.g. "Simple Moving Average") per explicit user request, combined with
        // CoreIndicatorParameterBase.GetDisplayName - the SSoT every parameter class already
        // implements for "{Name} ({params})" (e.g. "SMA (20)", "MACD (12, 26, 9)") - instead of
        // hand-rolling period extraction here.
        string shortName = side.IndicatorType.GetFormalName();
        string baseName = side.Parameters?.GetDisplayName(shortName) ?? shortName;

        var parts = new List<string>();
        // ScreenerCatalogProvider.GetOutputSeriesNames falls back to the indicator's own formal
        // name (e.g. "SMA") as the sole "output" of a single-series indicator - that is not a real
        // sub-output selection, so showing it here would just repeat the name (the "SMA(SMA)" bug
        // this round fixes). Only a genuinely distinct OutputName (e.g. MACD's "MACDLine"/"Signal"/
        // "Histogram") is shown.
        bool isGenuineOutputName = !string.IsNullOrWhiteSpace(side.OutputName)
            && !string.Equals(side.OutputName, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(side.OutputName, shortName, StringComparison.OrdinalIgnoreCase);
        if (isGenuineOutputName) parts.Add(side.OutputName);
        if (side.Frame is TimeFrame frame) parts.Add(frame.ToString());
        if (side.Offset != 0) parts.Add($"-{side.Offset}");

        if (parts.Count == 0) return baseName;

        // Merge into GetDisplayName's own trailing "(...)" group when it has one (the near-universal
        // case, e.g. "MACD (12, 26, 9, MACDLine)") rather than appending a second parenthetical -
        // matches the requested "Name(period, selected output)" shape exactly.
        return baseName.EndsWith(')')
            ? $"{baseName[..^1]}, {string.Join(", ", parts)})"
            : $"{baseName}({string.Join(", ", parts)})";
    }

    private static string DescribeRightAudit(BacktestConditionEntry entry)
    {
        return entry.TargetMode == RightHandTargetMode.Indicator && entry.Right is not null
            ? DescribeSideAudit(entry.Right)
            : $"TargetMode={entry.TargetMode}, Value={entry.RightNumericValue.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string DescribeSideAudit(BacktestConditionSide side) =>
        $"IndicatorType={side.IndicatorType}, OutputName={side.OutputName}, Frame={(side.Frame?.ToString() ?? "RunFrame")}, " +
        $"PriceSource={(side.PriceSource?.ToString() ?? "None")}, Offset={side.Offset}";
}
