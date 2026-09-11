using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Core.Strategies
{
    public record ChartStrategyResult(IZeroAllocChartDataAdapter Adapter, decimal EffectiveSize);

    public interface IChartStrategy
    {
        ChartType TargetType { get; }
        ChartStrategyResult Calculate(IEnumerable<CoreCandleData> candles, ChartStrategyParameters parameters);
    }

    public record ChartStrategyParameters(
        ChartSizingMode Mode,
        decimal ManualSize, // ReversalAmount / BrickSize / BoxSize
        int AtrPeriod,
        decimal AtrMultiplier,
        decimal Percentage, // For Kagi/Renko Percentage
        int PnfReversal = 3, // For P&F
        int RenkoReversal = 2, // For Renko
        int RoundingMode = 0,
        int FallbackMode = 0
    );
}
