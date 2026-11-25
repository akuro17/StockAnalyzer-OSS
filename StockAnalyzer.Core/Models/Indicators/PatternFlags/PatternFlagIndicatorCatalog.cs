using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags;

/// <summary>
/// One row of the table of chart-pattern flag indicators: the facts every consumer needs about one type. The row is the single definition (SA_ARCHITECTURE_RULES section 7, "one definition
/// of a type set"): the indicator base, the static default settings, the series suppression, the default render primitive and the contract tests all read it.
/// </summary>
/// <param name="Type">The indicator type.</param>
/// <param name="Category">The Indicator Manager category.</param>
/// <param name="MinBars">Bars needed before the first non-null value (unit: bars; Main is null while t + 1 is below it).</param>
/// <param name="Model">How the value at bar t is defined (S-3).</param>
/// <param name="HasDirection">True for the bidirectional types: they also output the Direction series.</param>
/// <param name="Color">Default colour of the flag histogram.</param>
/// <param name="ParameterClass">The parameter class of the type (a <see cref="CoreIndicatorParameterBase"/> with a public parameterless constructor).</param>
/// <param name="BullishSeries">Name of the visible bullish flag series (+1m) of a bidirectional type (integration spec 4.2); null for a type without direction.</param>
/// <param name="BearishSeries">Name of the visible bearish flag series (-1m) of a bidirectional type; null for a type without direction.</param>
public sealed record PatternFlagSpec(
    IndicatorType Type,
    CoreIndicatorCategory Category,
    int MinBars,
    PatternFlagEvaluationModel Model,
    bool HasDirection,
    IndicatorColor Color,
    Type ParameterClass,
    string? BullishSeries = null,
    string? BearishSeries = null,
    Func<CoreIndicatorParameterBase>? ParameterFactory = null,
    string[]? RuleSeries = null,
    IndicatorColor[]? RuleColors = null)
{
    /// <summary>True for Group B multi-rule flag indicators (Granville Buy / Sell rules 1..4).</summary>
    public bool HasRules => RuleSeries is { Length: > 0 };

    /// <summary>Lower end of the fixed Y range of the sub-window: -1.2m for a bidirectional type (its bearish series is at -1m), 0m otherwise.</summary>
    public decimal YMin => HasDirection ? IndicatorDefaultConstants.PatternFlagBidirectionalMinValue : IndicatorDefaultConstants.PatternFlagMinValue;

    /// <summary>Upper end of the fixed Y range of the sub-window.</summary>
    public decimal YMax => IndicatorDefaultConstants.PatternFlagMaxValue;

    /// <summary>Creates a fresh parameter instance with the specification defaults for this indicator type.</summary>
    public CoreIndicatorParameterBase CreateDefaultParameter()
        => ParameterFactory != null ? ParameterFactory() : (CoreIndicatorParameterBase)Activator.CreateInstance(ParameterClass)!;
}

/// <summary>
/// The table of the 57 chart-pattern flag indicators (plan ChartPatterns56 + U-W10). The rows are added by the run that implements the family: candlesticks (43), formations and market structure (4),
/// harmonic and waves, trading rules. Nothing else keeps a list of these types.
/// </summary>
public static class PatternFlagIndicatorCatalog
{
    // One line per family (each family's table is the definition of its rows): candlesticks (43, run 2), formations (3) and market structure (4, run 3 + U-W10), harmonics (3, run V2-6), waves (2) and rules (2, run V2-7).
    private static readonly PatternFlagSpec[] RowList = Candles.CandlePatternFlagTable.CreateSpecs()
        .Concat(Formations.FormationPatternFlagSpecs.CreateSpecs())
        .Concat(Structure.StructurePatternFlagSpecs.CreateSpecs())
        .Concat(Harmonics.HarmonicPatternFlagSpecs.CreateSpecs())
        .Concat(Waves.WavePatternFlagSpecs.CreateSpecs())
        .Concat(Rules.RulePatternFlagSpecs.CreateSpecs())
        .ToArray();

    private static readonly FrozenDictionary<IndicatorType, PatternFlagSpec> ByType = BuildIndex(RowList);

    /// <summary>All rows, in definition order.</summary>
    public static IReadOnlyList<PatternFlagSpec> Rows => RowList;

    /// <summary>True when <paramref name="type"/> is one of the flag indicators.</summary>
    public static bool Contains(IndicatorType type) => ByType.ContainsKey(type);

    /// <summary>The row of <paramref name="type"/>, or false when it is not a flag indicator.</summary>
    public static bool TryGet(IndicatorType type, out PatternFlagSpec spec)
    {
        if (ByType.TryGetValue(type, out PatternFlagSpec? found))
        {
            spec = found;
            return true;
        }

        spec = null!;
        return false;
    }

    /// <summary>The row of <paramref name="type"/>; throws when it is not a flag indicator (a flag indicator class names its own type, so a missing row is a programming error).</summary>
    public static PatternFlagSpec Get(IndicatorType type)
        => ByType.TryGetValue(type, out PatternFlagSpec? spec)
            ? spec
            : throw new InvalidOperationException($"{type} is missing in the pattern flag catalog.");

    /// <summary>
    /// True when <paramref name="seriesName"/> of a bidirectional <paramref name="type"/> is a helper series that must not be drawn on the chart and must not be a row of the Data tab: <c>Main</c> (0m / 1m, for
    /// the Screener and the Backtest) and <c>Direction</c> (-1m..+1m). Both stay in the result, the Screener, the Backtest and MCP. The visible series are the named bullish and bearish flags (integration spec 4.2),
    /// so the Data tab shows one row per side. A type without direction has one series, <c>Main</c>, and no helper.
    /// </summary>
    public static bool IsHelperSeries(IndicatorType type, string seriesName)
    {
        if (!ByType.TryGetValue(type, out PatternFlagSpec? spec))
        {
            return false;
        }

        if (spec.HasDirection)
        {
            return string.Equals(seriesName, IndicatorSeriesNames.PatternDirection, StringComparison.Ordinal)
                || string.Equals(seriesName, IndicatorResult.MainSeriesName, StringComparison.Ordinal);
        }

        if (spec.HasRules)
        {
            return string.Equals(seriesName, Rules.CoreGranvilleIndicatorBase.RuleCodeSeriesName, StringComparison.Ordinal)
                || string.Equals(seriesName, IndicatorResult.MainSeriesName, StringComparison.Ordinal);
        }

        return false;
    }

    private static FrozenDictionary<IndicatorType, PatternFlagSpec> BuildIndex(PatternFlagSpec[] rows)
    {
        var index = new Dictionary<IndicatorType, PatternFlagSpec>(rows.Length);
        foreach (PatternFlagSpec row in rows)
        {
            if (!index.TryAdd(row.Type, row))
            {
                throw new InvalidOperationException($"The pattern flag catalog lists '{row.Type}' twice.");
            }

            ValidateSeriesNames(row);
        }

        return index.ToFrozenDictionary();
    }

    /// <summary>A bidirectional row names two distinct visible series; a rule row names distinct rule series; a single row names none.</summary>
    private static void ValidateSeriesNames(PatternFlagSpec row)
    {
        if (row.HasDirection && row.HasRules)
        {
            throw new InvalidOperationException($"'{row.Type}' cannot be both bidirectional and multi-rule.");
        }

        if (row.HasRules)
        {
            if (row.BullishSeries != null || row.BearishSeries != null)
            {
                throw new InvalidOperationException($"'{row.Type}' is multi-rule and must not name bullish/bearish series.");
            }

            if (row.RuleColors == null || row.RuleColors.Length != row.RuleSeries!.Length)
            {
                throw new InvalidOperationException($"'{row.Type}' must define one colour per rule series.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in row.RuleSeries)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || !seen.Add(name)
                    || IndicatorSeriesNames.IsSignalSeries(name)
                    || name.EndsWith("Signals", StringComparison.OrdinalIgnoreCase)
                    || name == IndicatorResult.MainSeriesName
                    || name == IndicatorSeriesNames.PatternDirection
                    || name == Rules.CoreGranvilleIndicatorBase.RuleCodeSeriesName)
                {
                    throw new InvalidOperationException($"'{row.Type}' has an invalid or duplicate rule series name '{name}'.");
                }
            }

            return;
        }

        if (!row.HasDirection)
        {
            if (row.BullishSeries != null || row.BearishSeries != null)
            {
                throw new InvalidOperationException($"'{row.Type}' has no direction and must not name bullish/bearish series.");
            }

            return;
        }

        string?[] names = { row.BullishSeries, row.BearishSeries };
        if (names.Any(string.IsNullOrWhiteSpace)
            || string.Equals(row.BullishSeries, row.BearishSeries, StringComparison.Ordinal)
            || names.Any(n => IndicatorSeriesNames.IsSignalSeries(n!) || n!.EndsWith("Signals", StringComparison.OrdinalIgnoreCase)
                              || n == IndicatorResult.MainSeriesName || n == IndicatorSeriesNames.PatternDirection))
        {
            throw new InvalidOperationException($"'{row.Type}' needs two distinct, non-reserved bullish and bearish series names.");
        }
    }
}
