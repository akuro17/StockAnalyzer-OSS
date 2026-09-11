using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    /// Performs structural DTW analysis: finds the top-K most similar historical patterns
    /// using MESA cycle period for window sizing and EGARCH volatility for distance penalty.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="topK">Number of top similar patterns to return.</param>
    /// <param name="threshold">Minimum similarity probability threshold.</param>
    /// <param name="futureSteps">Number of future candles to include in the projection path.</param>
    /// <returns>A <see cref="StructuralDtwResult"/> containing the analysis output.</returns>
    public async Task<StructuralDtwResult> CalculateStructuralDtwAsync(
        IReadOnlyList<CandleData> candles,
        int topK = 5,
        double threshold = 0.3,
        int futureSteps = 20,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
    {
        if (candles == null || candles.Count < 60)
        {
            return StructuralDtwResult.Failure("Insufficient data: need at least 60 candles.");
        }

        try
        {
            await _pythonService.InitializeExternalProcessAsync();
            var responseJson = await _pythonService.ExecuteTransactionAsync(async () =>
            {
                await _pythonService.SendCandlesAsync(candles.ToList());
                return await _pythonService.CalculateStructuralDtwAsync(topK, threshold, futureSteps, warpingRadius);
            });

            return ParseResponse(responseJson);
        }
        catch (Exception ex)
        {
            return StructuralDtwResult.Failure(ex.Message);
        }
    }

    internal static StructuralDtwResult ParseResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var error = root.TryGetProperty("error", out var err)
                ? (err.GetString() ?? "Unknown python error")
                : "Unknown python error";
            return StructuralDtwResult.Failure(error);
        }

        if (!root.TryGetProperty("result", out var resultElement))
        {
            return StructuralDtwResult.Failure("No result in response");
        }

        int dominantPeriod = resultElement.TryGetProperty("dominantPeriod", out var dp) ? dp.GetInt32() : 0;
        int dtwWindow = resultElement.TryGetProperty("dtwWindow", out var dw) ? dw.GetInt32() : 0;
        double queryVol = resultElement.TryGetProperty("queryVolatility", out var qv) ? qv.GetDouble() : 0;

        var matches = new List<SimilarPatternResult>();

        if (resultElement.TryGetProperty("matches", out var matchesArray))
        {
            foreach (var item in matchesArray.EnumerateArray())
            {
                var futurePath = new List<double>();
                if (item.TryGetProperty("futurePath", out var fpArray))
                {
                    foreach (var val in fpArray.EnumerateArray())
                    {
                        futurePath.Add(val.GetDouble());
                    }
                }

                matches.Add(new SimilarPatternResult
                {
                    Distance = item.TryGetProperty("distance", out var d) ? d.GetDouble() : 0,
                    Probability = item.TryGetProperty("probability", out var p) ? p.GetDouble() : 0,
                    StartIndex = item.TryGetProperty("startIndex", out var s) ? s.GetInt32() : 0,
                    EndIndex = item.TryGetProperty("endIndex", out var e) ? e.GetInt32() : 0,
                    FuturePath = futurePath
                });
            }
        }

        return StructuralDtwResult.Success(dominantPeriod, dtwWindow, queryVol, matches);
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
    /// <param name="useStructural">Whether to apply EGARCH volatility filtering.</param>
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
            await _pythonService.InitializeExternalProcessAsync();

            var responseJson = await _pythonService.ExecuteTransactionAsync(async () =>
            {
                await _pythonService.SendCandlesAsync(candles.ToList());
                return await _pythonService.SearchSimilarPatternsAsync(
                    lookback, topK, futureSteps, threshold, queryLength, queryStartIndex, useStructural, warpingRadius);
            });

            return ParseOverlayResponse(responseJson);
        }
        catch (Exception ex)
        {
            return PatternOverlayResult.Failure(ex.Message);
        }
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
