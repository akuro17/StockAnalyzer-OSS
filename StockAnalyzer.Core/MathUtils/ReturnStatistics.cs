using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Beta and Alpha regression statistics for a single symbol relative to a benchmark.
/// </summary>
/// <param name="Symbol">Ticker symbol.</param>
/// <param name="Beta">OLS regression slope Cov(i, b) / Var(b); null when benchmark variance is zero.</param>
/// <param name="Alpha">OLS regression intercept mean_i - Beta * mean_b (per bar return, not annualized); null when Beta is null.</param>
/// <param name="RSquared">Coefficient of determination rho_ib^2; null when correlation is undefined.</param>
public readonly record struct BetaAlphaEntry(string Symbol, double? Beta, double? Alpha, double? RSquared);

/// <summary>
/// Record describing a symbol excluded from cross-asset calculations and the reason.
/// </summary>
/// <param name="Symbol">Ticker symbol.</param>
/// <param name="Reason">Human-readable description of why the symbol was excluded.</param>
public readonly record struct ExcludedSymbolInfo(string Symbol, string Reason);

/// <summary>
/// Output artifact of cross-asset return statistics calculation.
/// </summary>
public sealed class CrossAssetStatsResult
{
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
    public int CommonDateCount { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string ReturnBasis { get; init; } = ReturnStatistics.SimpleReturnBasis;
    public string CovarianceDegreesOfFreedom => "Sample(N-1)";
    public double?[][] CorrelationMatrix { get; init; } = Array.Empty<double?[]>();
    public double?[][]? CovarianceMatrix { get; init; }
    public IReadOnlyList<BetaAlphaEntry> BetaAlpha { get; init; } = Array.Empty<BetaAlphaEntry>();
    public IReadOnlyList<ExcludedSymbolInfo> ExcludedSymbols { get; init; } = Array.Empty<ExcludedSymbolInfo>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure mathematical functions for multi-asset return statistics, including
/// correlation matrices, sample covariance matrices (N-1 Bessel correction), and benchmark Beta/Alpha.
/// </summary>
public static class ReturnStatistics
{
    public const string SimpleReturnBasis = "SimpleReturn";
    public const string LogReturnBasis = "LogReturn";
    public const int MinCommonDates = 3;
    public const decimal DecimalVarianceEpsilon = 1e-14m;
    public const double VarianceEpsilon = 1e-14;

    /// <summary>
    /// Computes cross-asset return statistics across a dictionary of symbols and their chronological candles.
    /// Preserves the input symbol order. Returns are aligned by the inner join of dates.
    /// </summary>
    /// <param name="symbolCandles">Map of symbol to chronological bars (must be valid prices).</param>
    /// <param name="benchmarkSymbol">Optional benchmark symbol (can be one of symbolCandles or external).</param>
    /// <param name="includeCovariance">True to include the sample covariance matrix in the result.</param>
    /// <param name="returnBasis">"SimpleReturn" (default) or "LogReturn".</param>
    /// <param name="benchmarkCandles">Optional external benchmark candles when benchmarkSymbol is not in symbolCandles.</param>
    /// <returns>CrossAssetStatsResult containing matrices and benchmark regressions.</returns>
    public static CrossAssetStatsResult Calculate(
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> symbolCandles,
        string? benchmarkSymbol = null,
        bool includeCovariance = false,
        string returnBasis = SimpleReturnBasis,
        IReadOnlyList<CandleData>? benchmarkCandles = null)
    {
        ArgumentNullException.ThrowIfNull(symbolCandles);

        bool isLogReturn = string.Equals(returnBasis, LogReturnBasis, StringComparison.OrdinalIgnoreCase);
        string effectiveReturnBasis = isLogReturn ? LogReturnBasis : SimpleReturnBasis;

        var excludedSymbols = new List<ExcludedSymbolInfo>();
        var notes = new List<string>
        {
            "Sample covariance uses Bessel's correction (N-1 degrees of freedom).",
            "Survival bias notice: calculations are performed on currently stored tickers."
        };

        // 1. Calculate return series for each symbol
        var returnsBySymbol = new Dictionary<string, Dictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (symbol, candles) in symbolCandles)
        {
            if (candles == null || candles.Count < 2)
            {
                excludedSymbols.Add(new ExcludedSymbolInfo(symbol, "Fewer than 2 price bars"));
                continue;
            }

            var dailyReturns = ComputeReturns(candles, isLogReturn);
            if (dailyReturns.Count == 0)
            {
                excludedSymbols.Add(new ExcludedSymbolInfo(symbol, "No valid positive price returns"));
                continue;
            }

            returnsBySymbol[symbol] = dailyReturns;
        }

        // 2. Identify active universe symbols (preserving input order)
        var universeSymbols = new List<string>();
        foreach (string sym in symbolCandles.Keys)
        {
            if (returnsBySymbol.ContainsKey(sym))
            {
                universeSymbols.Add(sym);
            }
        }

        // Benchmark handling: resolve returns from universe symbols if present, or from benchmarkCandles if external
        Dictionary<DateTime, decimal>? benchmarkReturns = null;
        if (!string.IsNullOrWhiteSpace(benchmarkSymbol))
        {
            if (returnsBySymbol.TryGetValue(benchmarkSymbol, out var r))
            {
                benchmarkReturns = r;
            }
            else if (benchmarkCandles != null && benchmarkCandles.Count >= 2)
            {
                var rBench = ComputeReturns(benchmarkCandles, isLogReturn);
                if (rBench.Count > 0)
                {
                    benchmarkReturns = rBench;
                }
            }
        }

        bool hasBenchmark = benchmarkReturns != null;
        if (!string.IsNullOrWhiteSpace(benchmarkSymbol) && !hasBenchmark)
        {
            notes.Add($"Benchmark '{benchmarkSymbol}' data is unavailable or has insufficient bars; beta and alpha are omitted.");
        }

        if (universeSymbols.Count < 2)
        {
            return new CrossAssetStatsResult
            {
                Symbols = universeSymbols,
                CommonDateCount = 0,
                ReturnBasis = effectiveReturnBasis,
                ExcludedSymbols = excludedSymbols,
                Notes = notes
            };
        }

        // 3. Find common dates (inner join across universe symbols + benchmark)
        HashSet<DateTime>? commonDatesSet = null;

        foreach (string sym in universeSymbols)
        {
            var dates = returnsBySymbol[sym].Keys;
            if (commonDatesSet == null)
            {
                commonDatesSet = new HashSet<DateTime>(dates);
            }
            else
            {
                commonDatesSet.IntersectWith(dates);
            }
        }

        if (hasBenchmark && commonDatesSet != null)
        {
            commonDatesSet.IntersectWith(benchmarkReturns!.Keys);
        }

        var commonDates = (commonDatesSet?.OrderBy(d => d).ToList()) ?? new List<DateTime>();
        int T = commonDates.Count;

        if (T < MinCommonDates)
        {
            return new CrossAssetStatsResult
            {
                Symbols = universeSymbols,
                CommonDateCount = T,
                StartDate = T > 0 ? commonDates[0] : null,
                EndDate = T > 0 ? commonDates[^1] : null,
                ReturnBasis = effectiveReturnBasis,
                ExcludedSymbols = excludedSymbols,
                Notes = notes
            };
        }

        DateTime startDate = commonDates[0];
        DateTime endDate = commonDates[^1];

        // 4. Build aligned vectors and compute means in decimal
        int N = universeSymbols.Count;
        var alignedReturns = new decimal[N][];
        var means = new decimal[N];
        var variances = new decimal[N];
        var centered = new decimal[N][];

        for (int i = 0; i < N; i++)
        {
            string sym = universeSymbols[i];
            var dict = returnsBySymbol[sym];
            var r = new decimal[T];
            decimal sum = 0m;

            for (int t = 0; t < T; t++)
            {
                decimal val = dict[commonDates[t]];
                r[t] = val;
                sum += val;
            }

            alignedReturns[i] = r;
            decimal mean = sum / T;
            means[i] = mean;

            var u = new decimal[T];
            decimal sxx = 0m;
            for (int t = 0; t < T; t++)
            {
                decimal dev = r[t] - mean;
                u[t] = dev;
                sxx += dev * dev;
            }

            centered[i] = u;
            variances[i] = sxx / (T - 1m);

            if (variances[i] <= DecimalVarianceEpsilon)
            {
                notes.Add($"Symbol '{sym}' has zero return variance; correlations and betas are undefined.");
            }
        }

        // 5. Compute Covariance and Correlation Matrices
        var corrMatrix = new double?[N][];
        double?[][]? covMatrix = includeCovariance ? new double?[N][] : null;

        for (int i = 0; i < N; i++)
        {
            corrMatrix[i] = new double?[N];
            if (covMatrix != null)
            {
                covMatrix[i] = new double?[N];
            }

            for (int j = 0; j < N; j++)
            {
                if (i == j)
                {
                    corrMatrix[i][j] = variances[i] <= DecimalVarianceEpsilon ? null : 1.0;
                    if (covMatrix != null)
                    {
                        covMatrix[i][j] = (double)variances[i];
                    }
                }
                else if (j < i)
                {
                    // Symmetric reuse
                    corrMatrix[i][j] = corrMatrix[j][i];
                    if (covMatrix != null)
                    {
                        covMatrix[i][j] = covMatrix[j][i];
                    }
                }
                else
                {
                    // Compute cross product in decimal
                    decimal sxy = 0m;
                    decimal[] ui = centered[i];
                    decimal[] uj = centered[j];
                    for (int t = 0; t < T; t++)
                    {
                        sxy += ui[t] * uj[t];
                    }

                    decimal cov = sxy / (T - 1m);
                    if (covMatrix != null)
                    {
                        covMatrix[i][j] = (double)cov;
                    }

                    if (variances[i] <= DecimalVarianceEpsilon || variances[j] <= DecimalVarianceEpsilon)
                    {
                        corrMatrix[i][j] = null;
                    }
                    else
                    {
                        double vi = (double)variances[i];
                        double vj = (double)variances[j];
                        double denom = Math.Sqrt(vi * vj);
                        if (denom <= 0.0 || double.IsNaN(denom) || double.IsInfinity(denom))
                        {
                            corrMatrix[i][j] = null;
                        }
                        else
                        {
                            double rho = (double)cov / denom;
                            corrMatrix[i][j] = Math.Clamp(rho, -1.0, 1.0);
                        }
                    }
                }
            }
        }

        // 6. Compute Benchmark Beta & Alpha
        var betaAlphaList = new List<BetaAlphaEntry>();
        if (hasBenchmark)
        {
            var rBench = new decimal[T];
            decimal sumBench = 0m;

            for (int t = 0; t < T; t++)
            {
                decimal val = benchmarkReturns![commonDates[t]];
                rBench[t] = val;
                sumBench += val;
            }

            decimal meanBench = sumBench / T;
            var uBench = new decimal[T];
            decimal sbb = 0m;

            for (int t = 0; t < T; t++)
            {
                decimal dev = rBench[t] - meanBench;
                uBench[t] = dev;
                sbb += dev * dev;
            }

            decimal varBench = sbb / (T - 1m);

            if (varBench <= DecimalVarianceEpsilon)
            {
                notes.Add($"Benchmark '{benchmarkSymbol}' has zero return variance; beta and alpha are undefined.");
                foreach (string sym in universeSymbols)
                {
                    betaAlphaList.Add(new BetaAlphaEntry(sym, null, null, null));
                }
            }
            else
            {
                for (int i = 0; i < N; i++)
                {
                    string sym = universeSymbols[i];
                    decimal sxy = 0m;
                    decimal[] ui = centered[i];

                    for (int t = 0; t < T; t++)
                    {
                        sxy += ui[t] * uBench[t];
                    }

                    decimal beta = sxy / sbb;
                    decimal alpha = means[i] - beta * meanBench;

                    double? rSquared = null;
                    if (variances[i] > DecimalVarianceEpsilon)
                    {
                        double vi = (double)variances[i];
                        double vb = (double)varBench;
                        double denom = Math.Sqrt(vi * vb);
                        if (denom > 0.0 && !double.IsNaN(denom) && !double.IsInfinity(denom))
                        {
                            double rho = Math.Clamp((double)(sxy / (T - 1m)) / denom, -1.0, 1.0);
                            rSquared = rho * rho;
                        }
                    }

                    betaAlphaList.Add(new BetaAlphaEntry(sym, (double)beta, (double)alpha, rSquared));
                }
            }
        }

        return new CrossAssetStatsResult
        {
            Symbols = universeSymbols,
            CommonDateCount = T,
            StartDate = startDate,
            EndDate = endDate,
            ReturnBasis = effectiveReturnBasis,
            CorrelationMatrix = corrMatrix,
            CovarianceMatrix = covMatrix,
            BetaAlpha = betaAlphaList,
            ExcludedSymbols = excludedSymbols,
            Notes = notes
        };
    }

    private static Dictionary<DateTime, decimal> ComputeReturns(IReadOnlyList<CandleData> candles, bool isLogReturn)
    {
        var returns = new Dictionary<DateTime, decimal>();
        if (candles == null || candles.Count < 2) return returns;

        var sorted = candles.OrderBy(c => c.Timestamp).ToList();
        var prices = new List<decimal?>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            prices.Add(sorted[i].Close);
        }

        var returnList = isLogReturn
            ? IndicatorCalculationHelper.ConvertToLogReturns(prices)
            : IndicatorCalculationHelper.ConvertToSimpleReturns(prices);

        for (int i = 1; i < sorted.Count; i++)
        {
            if (returnList[i].HasValue)
            {
                returns[sorted[i].Timestamp.Date] = returnList[i]!.Value;
            }
        }

        return returns;
    }
}
