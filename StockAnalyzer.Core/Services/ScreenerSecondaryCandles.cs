using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The comparison-symbol bars of one scan, loaded once and shared by the evaluation of every symbol (read only).
/// A symbol is looked up the way the chart looks up the secondary series of an indicator (<c>AnalysisPipelineService.ResolveSecondaryCandles</c>),
/// so the same comparison symbol text resolves to the same series on both paths.
/// </summary>
public sealed class ScreenerSecondaryCandles : IScreenerSecondaryCandles
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>> _bySymbol;

    public ScreenerSecondaryCandles(IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>> bySymbol)
    {
        _bySymbol = bySymbol ?? throw new ArgumentNullException(nameof(bySymbol));
    }

    public IReadOnlyList<CoreCandleData?>? Get(string comparisonSymbol)
        => AnalysisPipelineService.ResolveSecondaryCandles(_bySymbol, comparisonSymbol);

    /// <summary>
    /// Loads the bars of every symbol in <paramref name="symbols"/> (each once) with <paramref name="requiredCount"/> bars (0 = the whole history),
    /// the way the Screener loads the bars of the symbols it evaluates. A symbol with no bars is left out, so its sides have no value.
    /// </summary>
    public static async Task<ScreenerSecondaryCandles> LoadAsync(
        IDataService dataService,
        IEnumerable<string> symbols,
        TimeFrame timeFrame,
        int requiredCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataService);
        ArgumentNullException.ThrowIfNull(symbols);

        var loaded = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> bars = await dataService.LoadCandlesAsync(symbol, timeFrame, requiredCount).ConfigureAwait(false);
            if (bars is { Count: > 0 })
            {
                loaded[symbol] = bars.Select(bar => (CoreCandleData?)new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList();
            }
        }

        return new ScreenerSecondaryCandles(loaded);
    }

    /// <summary>
    /// <see cref="LoadAsync"/> for the spellings a condition names (<see cref="ScreenerComparisonRequirement.Spellings"/>): the spellings are grouped by
    /// <see cref="SymbolHelper.IsSameSymbol"/> (<c>7203.T</c> and <c>7203-T</c>, <c>^N225</c> and <c>N225</c> are one instrument), the FIRST spelling of each group is read
    /// ONCE, and the same bar list is registered under EVERY spelling of the group. Every later <see cref="Get"/> is then an exact-key hit: a spelling that differs from the
    /// loaded one would otherwise make <see cref="AnalysisPipelineService.ResolveSecondaryCandles"/> build strings and scan every key on each call. A group without bars
    /// registers no spelling (its sides have no value). Blank spellings are ignored.
    /// </summary>
    public static async Task<ScreenerSecondaryCandles> LoadForSpellingsAsync(
        IDataService dataService,
        IReadOnlyList<string> spellings,
        TimeFrame timeFrame,
        int requiredCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataService);
        ArgumentNullException.ThrowIfNull(spellings);

        var groups = new List<List<string>>();
        foreach (string spelling in spellings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
        {
            List<string>? group = groups.FirstOrDefault(g => SymbolHelper.IsSameSymbol(g[0], spelling));
            if (group == null)
            {
                groups.Add(new List<string> { spelling });
            }
            else if (!group.Contains(spelling, StringComparer.OrdinalIgnoreCase))
            {
                group.Add(spelling);
            }
        }

        ScreenerSecondaryCandles loaded = await LoadAsync(dataService, groups.Select(g => g[0]), timeFrame, requiredCount, ct).ConfigureAwait(false);

        var bySymbol = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
        foreach (List<string> group in groups)
        {
            IReadOnlyList<CoreCandleData?>? bars = loaded.Get(group[0]);
            if (bars == null)
            {
                continue;
            }

            foreach (string spelling in group)
            {
                bySymbol[spelling] = bars;
            }
        }

        return new ScreenerSecondaryCandles(bySymbol);
    }
}

/// <summary>
/// What a Screener condition needs from the comparison symbols of its two-ticker sides (<see cref="ScreenerComparisonSymbols.Inspect(IScreeningCondition?)"/>).
/// </summary>
/// <param name="Symbols">One representative spelling per instrument (<see cref="SymbolHelper.IsSameSymbol"/>: <c>7203.T</c> and <c>7203-T</c>, <c>^N225</c> and <c>N225</c> are one), first-seen order.</param>
/// <param name="Spellings">Every distinct spelling (ignoring case) the condition names, first-seen order; the representatives are among them.</param>
/// <param name="OffDailySide">The first two-ticker side that reads a time frame other than D1, whether or not it names a comparison symbol; null when none does.</param>
/// <param name="SymbolRequiredSide">The first evaluable two-ticker side whose type needs a comparison symbol (<see cref="ScreenerComparisonSymbols.IsComparisonSymbolOptional"/> is false) and that names none; null when there is none. A trailing optional member: every earlier construction keeps compiling and has none.</param>
public sealed record ScreenerComparisonRequirement(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Spellings,
    ScreenerIndicatorSideConfig? OffDailySide,
    ScreenerIndicatorSideConfig? SymbolRequiredSide = null);

/// <summary>The comparison symbols a screening condition names: the <c>ComparisonSymbol</c> parameter of its indicator sides.</summary>
public static class ScreenerComparisonSymbols
{
    private static readonly string ParameterName = nameof(ICrossTickerParameter.ComparisonSymbol);

    /// <summary>The comparison symbol the indicator <paramref name="side"/> names, or null when it names none.</summary>
    public static string? Of(ScreenerIndicatorSideConfig? side)
    {
        if (side == null || side.CategoryType != ScreenerItemCategoryType.Indicator || side.Parameters == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, object> parameter in side.Parameters)
        {
            if (!string.Equals(parameter.Key, ParameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? text = parameter.Value switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    /// <summary>
    /// True when the indicator <paramref name="type"/> compares two symbols and accepts a blank comparison symbol
    /// (<see cref="ICrossTickerParameter.ComparisonSymbolOptional"/>: Correlation and CrossCorrelation, which then compare Price against Volume). False for an indicator that
    /// does not compare two symbols, for a type the factory cannot create, and for a two-ticker type that needs a symbol. The one definition of "optional".
    /// </summary>
    public static bool IsComparisonSymbolOptional(IndicatorType type)
        => OptionalSymbolTypes.GetOrAdd(
            type,
            static t => ScreenerIndicatorEligibility.IsTwoTickerType(t)
                        && IndicatorFactory.CreateStatic(t) is ICoreIndicator indicator
                        && indicator.GetDefaultSettings().ParameterObject is ICrossTickerParameter { ComparisonSymbolOptional: true });

    // "Optional" is a property of the indicator TYPE (its parameter class), so it is worked out once per type: the answer is read by property getters of the Scatter registration window.
    private static readonly ConcurrentDictionary<IndicatorType, bool> OptionalSymbolTypes = new();

    /// <summary>
    /// True when the metric or condition <paramref name="side"/> reads the DAILY bars of a comparison symbol, so it can only be evaluated on Day: an Indicator side whose
    /// type implements <see cref="ICrossTickerIndicator"/> and that either needs a comparison symbol (<see cref="IsComparisonSymbolOptional"/> is false) or names one
    /// (<see cref="Of"/>). A two-ticker type whose symbol is optional and blank (Price vs Volume) reads only its own bars and is not covered. A property of the side's
    /// type and parameters only: neither the time frame nor <see cref="ScreenerIndicatorEligibility"/> is judged here.
    /// </summary>
    public static bool NeedsDailyComparisonBars(ScreenerIndicatorSideConfig? side)
    {
        if (side == null || side.CategoryType != ScreenerItemCategoryType.Indicator || !ScreenerIndicatorEligibility.IsTwoTickerType(side.IndicatorType))
        {
            return false;
        }

        return !IsComparisonSymbolOptional(side.IndicatorType) || Of(side) != null;
    }

    /// <summary>
    /// The comparison symbols the EVALUABLE two-ticker sides of <paramref name="condition"/> name, and the first such side that reads a time frame other than D1.
    /// An evaluable two-ticker side is an Indicator side whose indicator type implements <see cref="ICrossTickerIndicator"/> and that
    /// <see cref="ScreenerIndicatorEligibility"/> allows (a side that has no value anyway reads no bars). The traversal is the one of
    /// <see cref="ScreenerSideFrames"/>: only enabled leaves of a tree, the left side always, the right side only when the entry compares against an indicator;
    /// a condition of any other type needs nothing. A side without a comparison symbol (Price vs Volume) names nothing. Called once per scan, never per symbol.
    /// </summary>
    public static ScreenerComparisonRequirement Inspect(IScreeningCondition? condition)
    {
        var sides = new List<ScreenerIndicatorSideConfig?>();
        switch (condition)
        {
            case ScreenerIndicatorEntry entry:
                GatherEntry(entry, sides);
                break;
            case ScreenerConditionTreeCondition tree:
                GatherNode(tree.Root, sides);
                break;
        }

        return Inspect(sides);
    }

    /// <summary>
    /// The same requirement for a plain list of sides (the Scatter evaluator hands over the sources of its metrics): each side is judged exactly as a side of a condition is
    /// (<see cref="Inspect(IScreeningCondition?)"/> gathers the sides of its condition and delegates here). A null side and a side that is not an evaluable two-ticker
    /// Indicator contribute nothing. Called once per evaluation, never per symbol.
    /// </summary>
    public static ScreenerComparisonRequirement Inspect(IEnumerable<ScreenerIndicatorSideConfig?> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        var symbols = new List<string>();
        var spellings = new List<string>();
        ScreenerIndicatorSideConfig? offDaily = null;
        ScreenerIndicatorSideConfig? symbolRequired = null;

        foreach (ScreenerIndicatorSideConfig? side in sides)
        {
            CollectSide(side, symbols, spellings, ref offDaily, ref symbolRequired);
        }

        return new ScreenerComparisonRequirement(symbols, spellings, offDaily, symbolRequired);
    }

    private static void GatherNode(IScreenerConditionNode node, List<ScreenerIndicatorSideConfig?> sides)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                if (leaf.Entry.IsEnabled)
                {
                    GatherEntry(leaf.Entry, sides);
                }
                break;
            case ScreenerConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    GatherNode(group.Children[i], sides);
                }
                break;
        }
    }

    private static void GatherEntry(ScreenerIndicatorEntry entry, List<ScreenerIndicatorSideConfig?> sides)
    {
        sides.Add(entry.LeftHand);
        if (entry.TargetMode == RightHandTargetMode.Indicator)
        {
            sides.Add(entry.RightHand);
        }
    }

    private static void CollectSide(ScreenerIndicatorSideConfig? side, List<string> symbols, List<string> spellings, ref ScreenerIndicatorSideConfig? offDaily, ref ScreenerIndicatorSideConfig? symbolRequired)
    {
        if (side == null || side.CategoryType != ScreenerItemCategoryType.Indicator)
        {
            return;
        }

        if (!ScreenerIndicatorEligibility.IsTwoTickerType(side.IndicatorType) || !ScreenerIndicatorEligibility.IsAllowed(side.IndicatorType, side.OutputName))
        {
            return;
        }

        if (offDaily == null && side.TimeFrame != TimeFrame.D1)
        {
            offDaily = side;
        }

        string? named = Of(side);
        if (named == null)
        {
            // A type that cannot give a value without a comparison symbol (Correlation and CrossCorrelation can: Price vs Volume) has no value here.
            if (symbolRequired == null && !IsComparisonSymbolOptional(side.IndicatorType))
            {
                symbolRequired = side;
            }

            return;
        }

        string spelling = named.ToUpperInvariant();
        if (spellings.Contains(spelling, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        spellings.Add(spelling);
        if (!symbols.Any(existing => SymbolHelper.IsSameSymbol(existing, spelling)))
        {
            symbols.Add(spelling);
        }
    }
}
