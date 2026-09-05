using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

public interface IPatternRecognitionService
{
    Task<PatternRecognitionResult> DetectAsync(
        IReadOnlyList<CandleData> candles,
        int minWindow = 20,
        int maxWindow = 60,
        int windowStep = 5,
        double threshold = 0.5,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius,
        double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha);
}
