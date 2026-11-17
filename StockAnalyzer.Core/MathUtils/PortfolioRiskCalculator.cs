using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>One holding as the risk calculation sees it: the symbol and its signed market value in the base currency (short positions are negative).</summary>
public readonly record struct PortfolioExposure(string Symbol, decimal SignedValue);

/// <summary>The weight and risk contributions of one symbol. Units: <see cref="Weight"/> ratio of gross exposure; the others are ratios of one bar's portfolio return.</summary>
public sealed record PortfolioRiskContribution(
    string Symbol,
    decimal SignedValue,
    decimal Weight,
    decimal? MarginalVolatility,
    decimal? ComponentVolatility,
    decimal? TailComponentCVaR);

/// <summary>The result of <see cref="PortfolioRiskCalculator.Calculate"/>. All risk figures are per bar, not annualized; VaR and CVaR are losses as positive ratios.</summary>
public sealed class PortfolioRiskResult
{
    public decimal GrossExposure { get; init; }

    public decimal NetExposure { get; init; }

    public IReadOnlyList<PortfolioRiskContribution> Contributions { get; init; } = Array.Empty<PortfolioRiskContribution>();

    public IReadOnlyList<ExcludedSymbolInfo> ExcludedSymbols { get; init; } = Array.Empty<ExcludedSymbolInfo>();

    public int CommonDateCount { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public double ConfidenceLevel { get; init; }

    /// <summary>Index of the quantile in the ascending-sorted portfolio returns: Clamp(floor((1 - c) T), 0, T - 1); -1 when there is no result.</summary>
    public int TailIndex { get; init; } = -1;

    public decimal? ValueAtRisk { get; init; }

    public decimal? ConditionalValueAtRisk { get; init; }

    public decimal? PortfolioVolatility { get; init; }

    /// <summary>The weighted sum of the symbols' betas against the benchmark; null without a benchmark or when one beta is undefined.</summary>
    public decimal? Beta { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Historical risk of a set of holdings, built on <see cref="ReturnStatistics"/> (aligned simple returns, sample covariance with N-1 degrees of freedom).
/// Definitions (all per bar of the bars given, not annualized):
/// weight w_i = V_i / G with G = sum |V_j| (the holdings of one symbol are netted first); portfolio return R_t = sum w_i r_(i,t);
/// with the returns sorted ascending and k = Clamp(floor((1 - c) T), 0, T - 1): VaR = -R_(k), CVaR = -mean(R_(0..k)), tail component of symbol i = -(1/(k+1)) sum over the k+1 worst dates of w_i r_(i,t);
/// volatility sigma = sqrt(w' S w), marginal_i = (S w)_i / sigma, component_i = w_i marginal_i (the components sum to sigma); beta = sum w_i beta_i.
/// The tail components sum to CVaR. Pure and total: it never throws on the data; a figure that cannot be calculated is null and is explained in <see cref="PortfolioRiskResult.Notes"/>.
/// </summary>
public static class PortfolioRiskCalculator
{
    public const double MinConfidenceLevel = 0.5;
    public const double MaxConfidenceLevel = 0.999;

    public static PortfolioRiskResult Calculate(
        IReadOnlyList<PortfolioExposure> exposures,
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> symbolCandles,
        double confidenceLevel,
        string? benchmarkSymbol = null,
        IReadOnlyList<CandleData>? benchmarkCandles = null,
        TimeFrame timeFrame = TimeFrame.D1)
    {
        ArgumentNullException.ThrowIfNull(exposures);
        ArgumentNullException.ThrowIfNull(symbolCandles);
        if (!(confidenceLevel >= MinConfidenceLevel && confidenceLevel <= MaxConfidenceLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceLevel), $"The confidence level must be within {MinConfidenceLevel}..{MaxConfidenceLevel}.");
        }

        // Net the holdings of one symbol (a long and a short of the same ticker) and keep the first-seen order.
        var netValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (PortfolioExposure exposure in exposures)
        {
            if (netValues.TryGetValue(exposure.Symbol, out decimal existing))
            {
                netValues[exposure.Symbol] = existing + exposure.SignedValue;
            }
            else
            {
                netValues[exposure.Symbol] = exposure.SignedValue;
                order.Add(exposure.Symbol);
            }
        }

        decimal gross = exposures.Sum(e => Math.Abs(e.SignedValue));
        decimal net = exposures.Sum(e => e.SignedValue);
        var notes = new List<string>();
        var excluded = new List<ExcludedSymbolInfo>();

        var candlesOfHeld = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in order)
        {
            if (symbolCandles.TryGetValue(symbol, out IReadOnlyList<CandleData>? candles) && candles.Count >= 2)
            {
                candlesOfHeld[symbol] = candles;
            }
            else
            {
                excluded.Add(new ExcludedSymbolInfo(symbol, "Fewer than 2 price bars"));
            }
        }

        // ReturnStatistics describes at least two series; a portfolio of one symbol gets a zero-weight copy of it, which changes none of the figures.
        const string PadSuffix = "#pad";
        if (candlesOfHeld.Count == 1)
        {
            KeyValuePair<string, IReadOnlyList<CandleData>> only = candlesOfHeld.First();
            candlesOfHeld[only.Key + PadSuffix] = only.Value;
        }

        CrossAssetStatsResult stats = ReturnStatistics.Calculate(
            candlesOfHeld, benchmarkSymbol, includeCovariance: true, ReturnStatistics.SimpleReturnBasis, benchmarkCandles, timeFrame);
        excluded.AddRange(stats.ExcludedSymbols);
        notes.AddRange(stats.Notes);

        IReadOnlyList<string> used = stats.Symbols;
        if (gross == 0m || used.Count == 0)
        {
            notes.Add(gross == 0m ? "The gross exposure is zero." : "No holding has usable return data.");
            return Empty(gross, net, excluded, stats, confidenceLevel, notes);
        }

        // Weights are relative to the whole gross exposure: a holding without return data stays in the denominator (its risk is unknown, not zero).
        var weights = used.Select(symbol => netValues.TryGetValue(symbol, out decimal value) ? value / gross : 0m).ToArray();
        int count = stats.CommonDateCount;
        if (count < ReturnStatistics.MinCommonDates || stats.AlignedReturns.Count != used.Count)
        {
            notes.Add($"At least {ReturnStatistics.MinCommonDates} common return dates are required; {count} were found.");
            return Empty(gross, net, excluded, stats, confidenceLevel, notes);
        }

        var portfolioReturns = new decimal?[count];
        for (int t = 0; t < count; t++)
        {
            decimal sum = 0m;
            for (int i = 0; i < used.Count; i++)
            {
                sum += weights[i] * stats.AlignedReturns[i][t];
            }

            portfolioReturns[t] = sum;
        }

        var sorted = new decimal[count];
        RollingTailMath.TryLoadSortedWindow(portfolioReturns, 0, count, sorted);
        int k = Math.Clamp((int)Math.Floor((1.0 - confidenceLevel) * count), 0, count - 1);
        decimal valueAtRisk = -sorted[k];
        decimal conditionalValueAtRisk = -sorted.Take(k + 1).Sum() / (k + 1);

        // The k + 1 worst dates: the tail components are the symbols' shares of those returns.
        int[] worstDates = Enumerable.Range(0, count).OrderBy(t => portfolioReturns[t]).ThenBy(t => t).Take(k + 1).ToArray();

        double[][] covariance = stats.CovarianceMatrix!.Select(row => row.Select(v => v ?? 0.0).ToArray()).ToArray();
        double[] covarianceTimesWeights = new double[used.Count];
        for (int i = 0; i < used.Count; i++)
        {
            for (int j = 0; j < used.Count; j++)
            {
                covarianceTimesWeights[i] += covariance[i][j] * (double)weights[j];
            }
        }

        double variance = 0.0;
        for (int i = 0; i < used.Count; i++)
        {
            variance += (double)weights[i] * covarianceTimesWeights[i];
        }

        double sigma = variance > 0.0 ? Math.Sqrt(variance) : 0.0;
        decimal? volatility = IndicatorCalculationHelper.ToDecimalOrNull(sigma);
        if (sigma <= 0.0)
        {
            notes.Add("The portfolio has no return variance; volatility contributions are undefined.");
        }

        var contributions = new List<PortfolioRiskContribution>(used.Count);
        for (int i = 0; i < used.Count; i++)
        {
            if (!netValues.ContainsKey(used[i]))
            {
                continue; // the zero-weight copy
            }

            decimal tail = 0m;
            foreach (int t in worstDates)
            {
                tail += weights[i] * stats.AlignedReturns[i][t];
            }

            double? marginal = sigma > 0.0 ? covarianceTimesWeights[i] / sigma : null;
            contributions.Add(new PortfolioRiskContribution(
                used[i],
                netValues[used[i]],
                weights[i],
                marginal is { } m ? IndicatorCalculationHelper.ToDecimalOrNull(m) : null,
                marginal is { } m2 ? IndicatorCalculationHelper.ToDecimalOrNull((double)weights[i] * m2) : null,
                -tail / (k + 1)));
        }

        decimal? beta = null;
        if (!string.IsNullOrWhiteSpace(benchmarkSymbol) && stats.BetaAlpha.Count == used.Count && stats.BetaAlpha.All(entry => entry.Beta.HasValue))
        {
            beta = IndicatorCalculationHelper.ToDecimalOrNull(stats.BetaAlpha.Select((entry, i) => (double)weights[i] * entry.Beta!.Value).Sum());
        }

        return new PortfolioRiskResult
        {
            GrossExposure = gross,
            NetExposure = net,
            Contributions = contributions,
            ExcludedSymbols = excluded,
            CommonDateCount = count,
            StartDate = stats.StartDate,
            EndDate = stats.EndDate,
            ConfidenceLevel = confidenceLevel,
            TailIndex = k,
            ValueAtRisk = valueAtRisk,
            ConditionalValueAtRisk = conditionalValueAtRisk,
            PortfolioVolatility = volatility,
            Beta = beta,
            Notes = notes,
        };
    }

    private static PortfolioRiskResult Empty(decimal gross, decimal net, List<ExcludedSymbolInfo> excluded, CrossAssetStatsResult stats, double confidenceLevel, List<string> notes)
        => new()
        {
            GrossExposure = gross,
            NetExposure = net,
            ExcludedSymbols = excluded,
            CommonDateCount = stats.CommonDateCount,
            StartDate = stats.StartDate,
            EndDate = stats.EndDate,
            ConfidenceLevel = confidenceLevel,
            Notes = notes,
        };
}
