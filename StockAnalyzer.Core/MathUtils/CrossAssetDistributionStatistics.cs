using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Distribution statistics of one symbol's aligned returns (ratio on the chosen return basis). Every undefined value is null (never 0, never NaN).
/// </summary>
/// <param name="Symbol">Ticker symbol.</param>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="StdDev">Sample standard deviation (divisor N - 1), the definition of the covariance matrix of the same result.</param>
/// <param name="DownsideDeviation">sigma- against the target 0 (<see cref="AsymmetricRiskMath"/>); a defined 0 when no return is below 0.</param>
/// <param name="Skewness">Sample skewness G1; null below 3 returns or for a flat series.</param>
/// <param name="ExcessKurtosis">Sample excess kurtosis G2; null below 4 returns or for a flat series.</param>
/// <param name="JarqueBeraStatistic">Jarque-Bera statistic; null below 8 returns or for a flat series.</param>
/// <param name="JarqueBeraPValue">exp(-JB / 2); null where the statistic is null.</param>
/// <param name="CornishFisherVar95">One-bar 95 % Cornish-Fisher Value at Risk as the lower-tail RETURN quantile (a loss is negative); null below 4 returns, for a flat series and where the expansion is not monotone.</param>
public readonly record struct SymbolDistributionStats(
    string Symbol,
    double? Mean,
    double? StdDev,
    double? DownsideDeviation,
    double? Skewness,
    double? ExcessKurtosis,
    double? JarqueBeraStatistic,
    double? JarqueBeraPValue,
    double? CornishFisherVar95);

/// <summary>
/// The optional distribution figures of a cross-asset result (<c>includeDistributionStats</c> of the MCP tool): per-symbol <see cref="SymbolDistributionStats"/> from
/// <see cref="CrossAssetStatsResult.AlignedReturns"/>, and the conditional (downside / upside) betas of <see cref="BetaAlphaEntry"/> from
/// <see cref="CrossAssetStatsResult.BenchmarkAlignedReturns"/>. Pure functions of an existing result; the existing statistics are not touched.
/// Specification: docs/indicators_formulas.md, record indicators_formulas_0605 (distribution diagnostics of the MCP tools).
/// </summary>
public static class CrossAssetDistributionStatistics
{
    /// <summary>The confidence level of <see cref="SymbolDistributionStats.CornishFisherVar95"/> (fixed by the field name).</summary>
    public const double CornishFisherConfidence = 0.95;

    /// <summary>The distribution statistics of every symbol of <paramref name="result"/>, in <see cref="CrossAssetStatsResult.Symbols"/> order (empty when there are no aligned returns).</summary>
    public static IReadOnlyList<SymbolDistributionStats> SymbolStats(CrossAssetStatsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var list = new List<SymbolDistributionStats>(result.AlignedReturns.Count);
        for (int i = 0; i < result.AlignedReturns.Count && i < result.Symbols.Count; i++)
        {
            list.Add(Compute(result.Symbols[i], result.AlignedReturns[i]));
        }

        return list;
    }

    /// <summary>
    /// The beta entries of <paramref name="result"/> with <see cref="BetaAlphaEntry.DownsideBeta"/> and <see cref="BetaAlphaEntry.UpsideBeta"/> filled (the other fields unchanged).
    /// The entries are returned unchanged when the result has no benchmark returns.
    /// </summary>
    public static IReadOnlyList<BetaAlphaEntry> WithConditionalBetas(CrossAssetStatsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        decimal[]? benchmark = result.BenchmarkAlignedReturns;
        if (benchmark is null)
        {
            return result.BetaAlpha;
        }

        var list = new List<BetaAlphaEntry>(result.BetaAlpha.Count);
        for (int i = 0; i < result.BetaAlpha.Count; i++)
        {
            BetaAlphaEntry entry = result.BetaAlpha[i];
            int column = IndexOf(result.Symbols, entry.Symbol);
            if (column < 0 || column >= result.AlignedReturns.Count)
            {
                list.Add(entry);
                continue;
            }

            decimal[] asset = result.AlignedReturns[column];
            list.Add(entry with
            {
                DownsideBeta = ToDouble(AsymmetricRiskMath.ConditionalBeta(asset, benchmark, BenchmarkRegime.Down)),
                UpsideBeta = ToDouble(AsymmetricRiskMath.ConditionalBeta(asset, benchmark, BenchmarkRegime.Up)),
            });
        }

        return list;
    }

    private static SymbolDistributionStats Compute(string symbol, decimal[] returns)
    {
        double[] values = new double[returns.Length];
        for (int i = 0; i < returns.Length; i++)
        {
            values[i] = (double)returns[i];
        }

        double? downside = AsymmetricRiskMath.TryComputeDownsideStatistics(returns, 0m, out DownsideStatistics statistics) ? (double)statistics.DownsideDeviation : null;
        if (!WindowMoments.TryCompute(values, FlatWindowRule.Returns, out WindowMoments moments))
        {
            return new SymbolDistributionStats(symbol, null, null, downside, null, null, null, null, null);
        }

        double? jbStatistic = null;
        double? jbPValue = null;
        if (NormalityAndTailMath.TryJarqueBera(moments, out double jb, out double jbP))
        {
            jbStatistic = Finite(jb);
            jbPValue = Finite(jbP);
        }

        double? cornishFisher = NormalityAndTailMath.TryCornishFisherValueAtRisk(moments, CornishFisherConfidence, out _, out double cfVar) ? Finite(cfVar) : null;
        return new SymbolDistributionStats(
            symbol,
            Finite(moments.Mean),
            Finite(moments.SampleStandardDeviation),
            downside,
            Finite(moments.SampleSkewness),
            Finite(moments.SampleExcessKurtosis),
            jbStatistic,
            jbPValue,
            cornishFisher);
    }

    private static int IndexOf(IReadOnlyList<string> symbols, string symbol)
    {
        for (int i = 0; i < symbols.Count; i++)
        {
            if (string.Equals(symbols[i], symbol, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static double? ToDouble(decimal? value) => value is { } number ? (double)number : null;
}
