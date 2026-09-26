using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service that provides market structure analysis by combining
/// EGARCH volatility, MESA cycle estimation, and DTW pattern matching.
/// </summary>
public class MarketStructureService
{
    private readonly IPythonService _pythonService;

    public MarketStructureService(IPythonService pythonService)
    {
        _pythonService = pythonService ?? throw new ArgumentNullException(nameof(pythonService));
    }

    /// <summary>
    /// Performs structural DTW analysis natively (no Python process): finds the top-K most similar historical patterns
    /// using the Burg cycle period of the mid prices for window sizing and the EGARCH(1,1,1) volatility for the distance penalty
    /// (a rolling standard deviation when no EGARCH estimate is usable). See <see cref="StructuralDtwMath"/>.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="topK">Number of top similar patterns to return.</param>
    /// <param name="threshold">Minimum similarity probability threshold.</param>
    /// <param name="futureSteps">Number of future candles to include in the projection path.</param>
    /// <param name="warpingRadius">Sakoe-Chiba half-width; negative = unconstrained.</param>
    /// <returns>A <see cref="StructuralDtwResult"/> containing the analysis output.</returns>
    public async Task<StructuralDtwResult> CalculateStructuralDtwAsync(
        IReadOnlyList<CandleData> candles,
        int topK = 5,
        double threshold = 0.3,
        int futureSteps = 20,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
    {
        if (candles == null || candles.Count < IndicatorDefaultConstants.StructuralDtwMinCandles)
        {
            return StructuralDtwResult.Failure($"Insufficient data: need at least {IndicatorDefaultConstants.StructuralDtwMinCandles} candles.");
        }

        try
        {
            return await Task.Run(() =>
            {
                int n = candles.Count;
                var closes = new double[n];
                var midPrices = new double[n];
                for (int i = 0; i < n; i++)
                {
                    closes[i] = (double)candles[i].Close;
                    midPrices[i] = ((double)candles[i].High + (double)candles[i].Low) / 2.0;
                }

                double[] volatility = ComputeStructuralVolatility(candles) ?? StructuralDtwMath.RollingStdVolatility(closes);
                return StructuralDtwMath.Calculate(closes, midPrices, volatility, topK, threshold, futureSteps, warpingRadius);
            });
        }
        catch (Exception ex)
        {
            return StructuralDtwResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Searches for similar historical patterns and returns their future price trajectories
    /// for overlay visualization on the chart.
    /// </summary>
    /// <param name="candles">The candle data to search within.</param>
    /// <param name="lookback">Number of candles to search back (0 = all history).</param>
    /// <param name="topK">Number of top similar patterns to return.</param>
    /// <param name="futureSteps">Number of future candles to project.</param>
    /// <param name="threshold">Minimum similarity probability threshold.</param>
    /// <param name="queryLength">Length of the current pattern to match against.</param>
    /// <param name="queryStartIndex">The index in the candle data where the query segment begins.</param>
    /// <param name="useStructural">Whether to apply EGARCH volatility filtering. The volatility is estimated here (native C#) and sent to Python; when the estimate fails the search runs without the filter.</param>
    public async Task<PatternOverlayResult> SearchSimilarPatternsAsync(
        IReadOnlyList<CandleData> candles,
        int lookback = 0,
        int topK = 5,
        int futureSteps = 20,
        double threshold = 0.3,
        int queryLength = 30,
        int queryStartIndex = -1,
        bool useStructural = false,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
    {
        int minRequired = queryLength + futureSteps + 10;
        if (candles == null || candles.Count < minRequired)
        {
            return PatternOverlayResult.Failure($"Insufficient data: need at least {minRequired} candles.");
        }

        try
        {
            var queryCandles = candles.Skip(queryStartIndex).Take(queryLength).ToList();
            double[]? volatility = useStructural
                ? await Task.Run(() => ComputeStructuralVolatility(candles))
                : null;
            await _pythonService.InitializeExternalProcessAsync();

            var responseJson = await _pythonService.ExecuteTransactionAsync(async () =>
            {
                await _pythonService.SendCandlesAsync(candles.ToList());
                return await _pythonService.SearchSimilarPatternsAsync(
                    lookback, topK, futureSteps, threshold, queryLength, queryStartIndex, useStructural && volatility != null, warpingRadius, volatility);
            });

            return ParseOverlayResponse(responseJson);
        }
        catch (Exception ex)
        {
            return PatternOverlayResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Conditional volatility (percent per bar) aligned one-to-one with <paramref name="candles"/>, from an EGARCH(P, O=P, Q) fit of the whole
    /// history's log returns. <c>vol[0] = σ_0</c> and <c>vol[k] = σ_{k-1}</c>, so vol[k] is the volatility of the return that ends at bar k.
    /// This is an in-sample (non-causal) description of the history for pattern matching, not a forecast. Null when the history is too short,
    /// contains a non-positive price, or has no usable variance.
    /// </summary>
    internal static double[]? ComputeStructuralVolatility(IReadOnlyList<CandleData> candles)
    {
        int n = candles.Count;
        if (n < 3)
        {
            return null;
        }

        var returns = new double[n - 1];
        for (int i = 1; i < n; i++)
        {
            returns[i - 1] = EgarchMath.PercentLogReturn((double)candles[i - 1].Close, (double)candles[i].Close);
        }

        int order = IndicatorDefaultConstants.EgarchDefaultOrder;
        Span<double> theta = stackalloc double[EgarchMath.ParameterCount(order, order)];
        var fit = EgarchMath.Fit(returns, order, order, theta, new EgarchFitOptions(MaxIterations: IndicatorDefaultConstants.EgarchDefaultMaxIterations));
        if (!fit.Success)
        {
            return null;
        }

        var sigma = new double[returns.Length + 1];
        EgarchMath.FilterOneStepAhead(returns, theta, order, order, fit.Initialization, sigma);

        var volatility = new double[n];
        volatility[0] = sigma[0];
        for (int k = 1; k < n; k++)
        {
            volatility[k] = sigma[k - 1];
        }

        // JSON cannot carry NaN/Infinity; a non-finite estimate means "no usable volatility" (unfiltered search), not a failed request.
        return Array.TrueForAll(volatility, double.IsFinite) ? volatility : null;
    }

    internal static PatternOverlayResult ParseOverlayResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var error = root.TryGetProperty("error", out var err)
                ? (err.GetString() ?? "Unknown python error")
                : "Unknown python error";
            return PatternOverlayResult.Failure(error);
        }

        if (!root.TryGetProperty("result", out var resultElement))
        {
            return PatternOverlayResult.Failure("No result in response");
        }

        string debugInfo = resultElement.TryGetProperty("debug_info", out var di) ? di.GetString() ?? "" : "";

        int queryLen = resultElement.TryGetProperty("queryLength", out var ql) ? ql.GetInt32() : 0;
        var patterns = new List<OverlayPattern>();

        if (resultElement.TryGetProperty("patterns", out var patternsArray))
        {
            foreach (var item in patternsArray.EnumerateArray())
            {
                patterns.Add(new OverlayPattern
                {
                    Distance = item.TryGetProperty("distance", out var d) ? d.GetDouble() : 0,
                    Probability = item.TryGetProperty("probability", out var p) ? p.GetDouble() : 0,
                    StartIndex = item.TryGetProperty("startIndex", out var s) ? s.GetInt32() : 0,
                    EndIndex = item.TryGetProperty("endIndex", out var e) ? e.GetInt32() : 0,
                    MatchedPrices = ParseDoubleArray(item, "matchedPrices"),
                    FutureRawPrices = ParseDoubleArray(item, "futureRawPrices"),
                    FuturePercentChange = ParseDoubleArray(item, "futurePercentChange")
                });
            }
        }

        var resultObj = PatternOverlayResult.Success(queryLen, patterns);
        // Expose debugInfo through standard failure or inject? We can re-use ErrorMessage
        if (!string.IsNullOrEmpty(debugInfo)) 
        {
            // Just hijack ErrorMessage to carry debug info on success for now
            return new PatternOverlayResult(queryLen, patterns, true, debugInfo);
        }
        return PatternOverlayResult.Success(queryLen, patterns);
    }

    private static IReadOnlyList<double> ParseDoubleArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var arr))
            return Array.Empty<double>();

        var list = new List<double>();
        foreach (var val in arr.EnumerateArray())
        {
            list.Add(val.GetDouble());
        }
        return list;
    }
}
