using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service that bridges the Python-based pattern recognition engine with C#.
/// Handles sending candle data, invoking detection, and parsing JSON results into typed models.
/// </summary>
public class PatternRecognitionService : IPatternRecognitionService
{
    private readonly IPythonService _pythonService;

    public PatternRecognitionService(IPythonService pythonService)
    {
        _pythonService = pythonService ?? throw new ArgumentNullException(nameof(pythonService));
    }

    /// <summary>
    /// Detects chart patterns in the given candle data using ML-based waveform matching.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="minWindow">Minimum window size for pattern detection.</param>
    /// <param name="maxWindow">Maximum window size for pattern detection.</param>
    /// <param name="windowStep">Step size for sliding window.</param>
    /// <param name="threshold">Minimum probability threshold (0.0 to 1.0).</param>
    /// <returns>A <see cref="PatternRecognitionResult"/> containing detected patterns.</returns>
    public async Task<PatternRecognitionResult> DetectAsync(
        IReadOnlyList<CandleData> candles,
        int minWindow = 20,
        int maxWindow = 60,
        int windowStep = 5,
        double threshold = 0.5,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius,
        double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha)
    {
        if (candles == null || candles.Count < minWindow)
        {
            return PatternRecognitionResult.Success(Array.Empty<DetectedPattern>());
        }

        try
        {
            await _pythonService.InitializeExternalProcessAsync();
            var responseJson = await _pythonService.ExecuteTransactionAsync(async () =>
            {
                await _pythonService.SendCandlesAsync(candles.ToList());
                return await _pythonService.DetectPatternsAsync(
                    minWindow, maxWindow, windowStep, threshold, warpingRadius, shortSpanPenaltyAlpha);
            });

            return ParseResponse(responseJson);
        }
        catch (Exception ex)
        {
            return PatternRecognitionResult.Failure(ex.Message);
        }
    }

    private static PatternRecognitionResult ParseResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var error = root.TryGetProperty("error", out var err)
                ? (err.GetString() ?? "Unknown python error")
                : "Unknown python error";
            return PatternRecognitionResult.Failure(error);
        }

        var patterns = new List<DetectedPattern>();

        if (root.TryGetProperty("result", out var resultElement) &&
            resultElement.TryGetProperty("patterns", out var patternsArray))
        {
            foreach (var item in patternsArray.EnumerateArray())
            {
                patterns.Add(new DetectedPattern
                {
                    Name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty,
                    Probability = item.TryGetProperty("probability", out var p) ? p.GetDouble() : 0.0,
                    StartIndex = item.TryGetProperty("startIndex", out var s) ? s.GetInt32() : 0,
                    EndIndex = item.TryGetProperty("endIndex", out var e) ? e.GetInt32() : 0
                });
            }
        }

        return PatternRecognitionResult.Success(patterns);
    }
}
