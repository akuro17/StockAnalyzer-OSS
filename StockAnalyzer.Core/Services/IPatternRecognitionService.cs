using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

public interface IPatternRecognitionService
{
    Task<PatternRecognitionResult> DetectAsync(
        IReadOnlyList<CandleData> candles,
        int minWindow = ChartConstants.PatternRecognitionDefaultMinWindow,
        int maxWindow = ChartConstants.PatternRecognitionDefaultMaxWindow,
        int windowStep = ChartConstants.PatternRecognitionDefaultWindowStep,
        double threshold = ChartConstants.PatternRecognitionDefaultThreshold,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius,
        double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha);
}
