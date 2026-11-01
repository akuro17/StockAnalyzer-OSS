using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Output artifact of market breadth calculation across a universe of tickers.
/// </summary>
public sealed class MarketBreadthResult
{
    public int DateCount { get; init; }
    public int SymbolCount { get; init; }
    public IReadOnlyList<ExcludedSymbolInfo> ExcludedSymbols { get; init; } = Array.Empty<ExcludedSymbolInfo>();
    public MarketBreadthSeries Series { get; init; } = new();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Aggregated market breadth series aligned across all evaluated trading dates.
/// </summary>
public sealed class MarketBreadthSeries
{
    public IReadOnlyList<string> Dates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> Advancers { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Decliners { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> Unchanged { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int?> AdLine { get; init; } = Array.Empty<int?>();
    public IReadOnlyList<double?> AdRatio { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<double?> McClellan { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<double?> SummationIndex { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<double?> PctAboveMa { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<int?> NewHighs { get; init; } = Array.Empty<int?>();
    public IReadOnlyList<int?> NewLows { get; init; } = Array.Empty<int?>();
    public IReadOnlyList<int?> NetNewHighs { get; init; } = Array.Empty<int?>();
    public IReadOnlyList<double?> Trin { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<decimal?> Cvi { get; init; } = Array.Empty<decimal?>();
}

/// <summary>
/// Parameters governing market breadth indicators and series filtering.
/// </summary>
public sealed class MarketBreadthParameters
{
    public int AdRatioBars { get; init; } = MarketBreadthCalculator.DefaultAdRatioBars;
    public int McClellanFast { get; init; } = MarketBreadthCalculator.DefaultMcClellanFast;
    public int McClellanSlow { get; init; } = MarketBreadthCalculator.DefaultMcClellanSlow;
    public int MaBars { get; init; } = MarketBreadthCalculator.DefaultMaBars;
    public int HighLowBars { get; init; } = MarketBreadthCalculator.DefaultHighLowBars;
    public int? OutputTailCount { get; init; }
    public IReadOnlySet<string>? IncludeSeries { get; init; }
}

/// <summary>
/// Pure static calculation engine for market breadth analytics across a universe of symbols.
/// Adheres to StockAnalyzer domain invariants (decimal arithmetic for volume/prices, causal time series).
/// </summary>
public static class MarketBreadthCalculator
{
    public const int DefaultAdRatioBars = 20;
    public const int DefaultMcClellanFast = 19;
    public const int DefaultMcClellanSlow = 39;
    public const int DefaultMaBars = 50;
    public const int DefaultHighLowBars = 20;

    public const string AdvancersKey = "advancers";
    public const string DeclinersKey = "decliners";
    public const string UnchangedKey = "unchanged";
    public const string AdLineKey = "adLine";
    public const string AdRatioKey = "adRatio";
    public const string McClellanKey = "mcClellan";
    public const string SummationIndexKey = "summationIndex";
    public const string PctAboveMaKey = "pctAboveMa";
    public const string NewHighsKey = "newHighs";
    public const string NewLowsKey = "newLows";
    public const string NetNewHighsKey = "netNewHighs";
    public const string TrinKey = "trin";
    public const string CviKey = "cvi";

    public static readonly IReadOnlySet<string> AllSeriesKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdvancersKey, DeclinersKey, UnchangedKey, AdLineKey, AdRatioKey,
        McClellanKey, SummationIndexKey, PctAboveMaKey,
        NewHighsKey, NewLowsKey, NetNewHighsKey, TrinKey, CviKey
    };

    /// <summary>
    /// Computes market breadth series from the provided universe candles.
    /// </summary>
    /// <param name="universeCandles">Mapping of symbol to its historical candles.</param>
    /// <param name="parameters">Parameters governing breadth indicators and filtering.</param>
    /// <returns>Calculated breadth result with series and metadata.</returns>
    public static MarketBreadthResult Calculate(
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> universeCandles,
        MarketBreadthParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(universeCandles);
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.AdRatioBars <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.AdRatioBars), "AdRatioBars must be positive.");
        if (parameters.McClellanFast <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.McClellanFast), "McClellanFast must be positive.");
        if (parameters.McClellanSlow <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.McClellanSlow), "McClellanSlow must be positive.");
        if (parameters.MaBars <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.MaBars), "MaBars must be positive.");
        if (parameters.HighLowBars <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters.HighLowBars), "HighLowBars must be positive.");

        var excludedSymbols = new List<ExcludedSymbolInfo>();
        var validCandlesBySymbol = new SortedDictionary<string, IReadOnlyList<CandleData>>(StringComparer.Ordinal);

        foreach (var (symbol, candles) in universeCandles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (candles == null || candles.Count < 2)
            {
                excludedSymbols.Add(new ExcludedSymbolInfo(symbol, "Fewer than 2 valid candles"));
                continue;
            }

            var sortedValid = candles
                .Where(c => c.Close > 0m && c.Volume >= 0m)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (sortedValid.Count < 2)
            {
                excludedSymbols.Add(new ExcludedSymbolInfo(symbol, "Fewer than 2 valid candles"));
                continue;
            }

            validCandlesBySymbol[symbol] = sortedValid;
        }

        if (validCandlesBySymbol.Count == 0)
        {
            return new MarketBreadthResult
            {
                DateCount = 0,
                SymbolCount = 0,
                ExcludedSymbols = excludedSymbols,
                Series = new MarketBreadthSeries(),
                Notes = CreateNotes()
            };
        }

        // Union of all dates across all tickers with valid candles (sorted ascending)
        var allDates = validCandlesBySymbol.Values
            .SelectMany(cList => cList)
            .Select(c => c.Timestamp.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        int totalDates = allDates.Count;
        if (totalDates == 0)
        {
            return new MarketBreadthResult
            {
                DateCount = 0,
                SymbolCount = validCandlesBySymbol.Count,
                ExcludedSymbols = excludedSymbols,
                Series = new MarketBreadthSeries(),
                Notes = CreateNotes()
            };
        }

        var dateToIndex = new Dictionary<DateTime, int>(totalDates);
        for (int i = 0; i < totalDates; i++)
        {
            dateToIndex[allDates[i]] = i;
        }

        // Aggregation arrays over union of dates
        var advancers = new int[totalDates];
        var decliners = new int[totalDates];
        var unchanged = new int[totalDates];
        var upVol = new decimal[totalDates];
        var downVol = new decimal[totalDates];
        var validSmaCount = new int[totalDates];
        var aboveSmaCount = new int[totalDates];
        var eligibleHighLowCount = new int[totalDates];
        var newHighsCount = new int[totalDates];
        var newLowsCount = new int[totalDates];

        // Process each symbol
        foreach (var (_, candles) in validCandlesBySymbol)
        {
            int candleCount = candles.Count;
            var closeList = new List<decimal>(candleCount);
            var nullableCloseList = new List<decimal?>(candleCount);

            for (int k = 0; k < candleCount; k++)
            {
                decimal c = candles[k].Close;
                closeList.Add(c);
                nullableCloseList.Add(c);
            }

            // SSoT Indicators: SMA and Rolling Extremes
            var smaList = IndicatorCalculationHelper.CalculateSma(closeList, parameters.MaBars);
            var rollingMax = RollingExtremeHelper.CalculateRollingMax(nullableCloseList, parameters.HighLowBars);
            var rollingMin = RollingExtremeHelper.CalculateRollingMin(nullableCloseList, parameters.HighLowBars);

            for (int k = 0; k < candleCount; k++)
            {
                DateTime d = candles[k].Timestamp.Date;
                if (!dateToIndex.TryGetValue(d, out int t))
                    continue;

                // 1. Advance / Decline / Unchanged and Volume (relative to its own previous bar)
                if (k > 0)
                {
                    decimal close = candles[k].Close;
                    decimal prevClose = candles[k - 1].Close;
                    decimal volume = candles[k].Volume;

                    if (close > prevClose)
                    {
                        advancers[t]++;
                        upVol[t] += volume;
                    }
                    else if (close < prevClose)
                    {
                        decliners[t]++;
                        downVol[t] += volume;
                    }
                    else
                    {
                        unchanged[t]++;
                    }
                }

                // 2. SMA comparison
                if (smaList.Count > k && smaList[k].HasValue)
                {
                    validSmaCount[t]++;
                    if (candles[k].Close > smaList[k]!.Value)
                    {
                        aboveSmaCount[t]++;
                    }
                }

                // 3. New Highs / New Lows
                if (rollingMax.Count > k && rollingMax[k].HasValue &&
                    rollingMin.Count > k && rollingMin[k].HasValue)
                {
                    eligibleHighLowCount[t]++;
                    if (candles[k].Close >= rollingMax[k]!.Value)
                    {
                        newHighsCount[t]++;
                    }
                    if (candles[k].Close <= rollingMin[k]!.Value)
                    {
                        newLowsCount[t]++;
                    }
                }
            }
        }

        // Series calculation over all dates
        var adLine = new int?[totalDates];
        int runningAd = 0;

        var adRatio = new double?[totalDates];
        var pctAboveMa = new double?[totalDates];
        var newHighs = new int?[totalDates];
        var newLows = new int?[totalDates];
        var netNewHighs = new int?[totalDates];
        var trin = new double?[totalDates];
        var cvi = new decimal?[totalDates];
        decimal runningCvi = 0m;

        for (int t = 0; t < totalDates; t++)
        {
            // A/D Line
            runningAd += (advancers[t] - decliners[t]);
            adLine[t] = runningAd;

            // A/D Ratio over rolling window
            if (t < parameters.AdRatioBars - 1)
            {
                adRatio[t] = null;
            }
            else
            {
                int sumA = 0;
                int sumD = 0;
                for (int i = t - parameters.AdRatioBars + 1; i <= t; i++)
                {
                    sumA += advancers[i];
                    sumD += decliners[i];
                }
                adRatio[t] = sumD == 0 ? null : (double)sumA / sumD;
            }

            // % Above SMA
            pctAboveMa[t] = validSmaCount[t] == 0 ? null : (double)aboveSmaCount[t] / validSmaCount[t];

            // New Highs / New Lows / Net
            if (eligibleHighLowCount[t] == 0)
            {
                newHighs[t] = null;
                newLows[t] = null;
                netNewHighs[t] = null;
            }
            else
            {
                newHighs[t] = newHighsCount[t];
                newLows[t] = newLowsCount[t];
                netNewHighs[t] = newHighsCount[t] - newLowsCount[t];
            }

            // TRIN
            if (decliners[t] == 0 || downVol[t] == 0m || upVol[t] == 0m)
            {
                trin[t] = null;
            }
            else
            {
                double adRatioVal = (double)advancers[t] / decliners[t];
                double volRatioVal = (double)upVol[t] / (double)downVol[t];
                trin[t] = volRatioVal <= 0.0 ? null : adRatioVal / volRatioVal;
            }

            // CVI
            runningCvi += (upVol[t] - downVol[t]);
            cvi[t] = runningCvi;
        }

        // McClellan Oscillator & Summation Index
        var netAdvancersDecimal = new decimal[totalDates];
        for (int t = 0; t < totalDates; t++)
        {
            netAdvancersDecimal[t] = (decimal)(advancers[t] - decliners[t]);
        }

        var fastEma = IndicatorCalculationHelper.CalculateEma(netAdvancersDecimal, parameters.McClellanFast);
        var slowEma = IndicatorCalculationHelper.CalculateEma(netAdvancersDecimal, parameters.McClellanSlow);

        var mcClellan = new double?[totalDates];
        var summationIndex = new double?[totalDates];
        double? runningSummation = null;

        for (int t = 0; t < totalDates; t++)
        {
            if (fastEma.Count > t && fastEma[t].HasValue &&
                slowEma.Count > t && slowEma[t].HasValue)
            {
                double osc = (double)(fastEma[t]!.Value - slowEma[t]!.Value);
                mcClellan[t] = osc;
                runningSummation = (runningSummation ?? 0.0) + osc;
                summationIndex[t] = runningSummation;
            }
            else
            {
                mcClellan[t] = null;
                summationIndex[t] = null;
            }
        }

        // Slicing to tail count if requested
        int skipCount = 0;
        if (parameters.OutputTailCount.HasValue &&
            parameters.OutputTailCount.Value > 0 &&
            parameters.OutputTailCount.Value < totalDates)
        {
            skipCount = totalDates - parameters.OutputTailCount.Value;
        }

        var finalDates = allDates.Skip(skipCount).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

        bool ShouldInclude(string key) =>
            parameters.IncludeSeries == null ||
            parameters.IncludeSeries.Contains(key);

        var series = new MarketBreadthSeries
        {
            Dates = finalDates,
            Advancers = ShouldInclude(AdvancersKey) ? advancers.Skip(skipCount).ToList() : Array.Empty<int>(),
            Decliners = ShouldInclude(DeclinersKey) ? decliners.Skip(skipCount).ToList() : Array.Empty<int>(),
            Unchanged = ShouldInclude(UnchangedKey) ? unchanged.Skip(skipCount).ToList() : Array.Empty<int>(),
            AdLine = ShouldInclude(AdLineKey) ? adLine.Skip(skipCount).ToList() : Array.Empty<int?>(),
            AdRatio = ShouldInclude(AdRatioKey) ? adRatio.Skip(skipCount).ToList() : Array.Empty<double?>(),
            McClellan = ShouldInclude(McClellanKey) ? mcClellan.Skip(skipCount).ToList() : Array.Empty<double?>(),
            SummationIndex = ShouldInclude(SummationIndexKey) ? summationIndex.Skip(skipCount).ToList() : Array.Empty<double?>(),
            PctAboveMa = ShouldInclude(PctAboveMaKey) ? pctAboveMa.Skip(skipCount).ToList() : Array.Empty<double?>(),
            NewHighs = ShouldInclude(NewHighsKey) ? newHighs.Skip(skipCount).ToList() : Array.Empty<int?>(),
            NewLows = ShouldInclude(NewLowsKey) ? newLows.Skip(skipCount).ToList() : Array.Empty<int?>(),
            NetNewHighs = ShouldInclude(NetNewHighsKey) ? netNewHighs.Skip(skipCount).ToList() : Array.Empty<int?>(),
            Trin = ShouldInclude(TrinKey) ? trin.Skip(skipCount).ToList() : Array.Empty<double?>(),
            Cvi = ShouldInclude(CviKey) ? cvi.Skip(skipCount).ToList() : Array.Empty<decimal?>()
        };

        return new MarketBreadthResult
        {
            DateCount = finalDates.Count,
            SymbolCount = validCandlesBySymbol.Count,
            ExcludedSymbols = excludedSymbols,
            Series = series,
            Notes = CreateNotes()
        };
    }

    private static IReadOnlyList<string> CreateNotes() => new[]
    {
        "A/D Line, CVI, and McClellan Summation Index are accumulated from the beginning of the evaluated window.",
        "Lookback calculations assume survivorship within the provided universe."
    };
}
