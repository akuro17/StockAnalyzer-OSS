using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.StoredPeriod;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// What the Training Wizard tells before Start about the stored bars of the selected symbols: which symbols have too few bars for the settings, and whether the
/// selection can exceed the training sample cap (<c>TrainingResourceLimits.MaxSamples</c>). Pure arithmetic over bar counts; no I/O, no settings access.
/// </summary>
/// <remarks>
/// The sample figure is an UPPER BOUND of what the trainer counts, not the exact count (the exact count depends on the dates of all symbols together because of
/// the purge between folds). The trainer refuses a run when the counted samples are strictly above the cap
/// (<c>model_analysis.validate_population</c>, and for composed features or a fixed z-score also <c>dataset._checked_cumulative_samples</c>).
/// </remarks>
public static class TrainingDataSufficiency
{
    /// <summary>
    /// The estimate E(b) for a symbol with <paramref name="bars"/> bars. S = windows the symbol yields = max(0, bars - window - horizon + 1 - maxLag).
    /// <paramref name="retained"/> (composed features or fixed z-score): every window counts, E = S. Otherwise the cap counts the fit and validation windows of
    /// the last outer fold, which cannot exceed the outer train side of <c>splits * floor(S / (splits + 1))</c> windows: E = that value.
    /// </summary>
    public static long EstimateSamples(int bars, int window, int horizon, int maxLag, int splits, bool retained)
    {
        if (splits < 1) throw new ArgumentOutOfRangeException(nameof(splits), splits, "splits must be positive.");
        long windows = Math.Max(0L, (long)Math.Max(0, bars) - window - horizon + 1 - maxLag);
        return retained ? windows : checked(splits * (windows / (splits + 1)));
    }

    public static TrainingSufficiencyResult Evaluate(IReadOnlyList<SymbolBarInfo> infos, int requiredBars, int window, int horizon, int maxLag, int splits,
        bool retained, int sampleCap)
    {
        ArgumentNullException.ThrowIfNull(infos);
        long Estimate(int bars) => EstimateSamples(bars, window, horizon, maxLag, splits, retained);

        var shortSymbols = infos.Where(i => i.BarsInRange < requiredBars)
            .OrderBy(i => i.BarsInRange).ThenBy(i => i.Symbol, StringComparer.Ordinal).ToArray();
        var unreachable = infos.Where(i => i.BarsUpToEnd < requiredBars)
            .OrderBy(i => i.BarsUpToEnd).ThenBy(i => i.Symbol, StringComparer.Ordinal).ToArray();
        DateOnly? minimumStart = null;
        foreach (var info in infos)
            if (info.RequiredStart is { } date && (minimumStart is not { } current || date < current)) minimumStart = date;

        long total = 0;
        foreach (var info in infos) total = checked(total + Estimate(info.BarsInRange));
        long? atMinimumStart = null;
        if (minimumStart is not null && unreachable.Length == 0)
        {
            atMinimumStart = 0;
            foreach (var info in infos) atMinimumStart = checked(atMinimumStart + Estimate(info.BarsFromMinimumStart ?? 0));
        }

        long atRequired = Estimate(requiredBars);
        int? maxTickers = atRequired <= 0 ? null : (int)Math.Min(int.MaxValue, sampleCap / atRequired);
        return new TrainingSufficiencyResult(shortSymbols, unreachable, minimumStart, total > sampleCap, total, atMinimumStart, atRequired, maxTickers, infos.Count);
    }
}

/// <summary>The outcome of <see cref="TrainingDataSufficiency.Evaluate"/>.</summary>
/// <param name="ShortSymbols">Symbols with fewer bars in the period than required: fewest first, then ordinal name.</param>
/// <param name="Unreachable">Symbols whose whole stored history up to the end is below the requirement (no start can fix them).</param>
/// <param name="MinimumStart">The earliest required start over the symbols that can reach the requirement; a start on or before it gives each of them enough bars.</param>
/// <param name="OverLimit">The total estimate is strictly above the cap.</param>
/// <param name="TotalEstimate">Upper bound of the samples of all symbols for the current period.</param>
/// <param name="TotalAtMinimumStart">The same for the period from <paramref name="MinimumStart"/>; null when a symbol is unreachable or there is no minimum start.</param>
/// <param name="EstimateAtRequiredBars">The estimate of one symbol that has exactly the required number of bars.</param>
/// <param name="MaxTickers">How many symbols fit the cap at the shortest period: cap / <paramref name="EstimateAtRequiredBars"/> (null when that estimate is 0).</param>
/// <param name="TickerCount">The number of symbols given.</param>
public sealed record TrainingSufficiencyResult(IReadOnlyList<SymbolBarInfo> ShortSymbols, IReadOnlyList<SymbolBarInfo> Unreachable, DateOnly? MinimumStart,
    bool OverLimit, long TotalEstimate, long? TotalAtMinimumStart, long EstimateAtRequiredBars, int? MaxTickers, int TickerCount);
