using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Additive capability implemented by analysis pipelines and gateways that accept an explicit chart
/// <see cref="TimeFrame"/> (used by universe-breadth indicators to align breadth series accurately on
/// daily, weekly, or monthly charts). Callers probe <c>x is ITimeFrameAwareIndicatorCalculation</c>
/// and fall back to <see cref="ICancellableIndicatorCalculation"/> / <see cref="IAnalysisPipelineService"/>.
/// </summary>
public interface ITimeFrameAwareIndicatorCalculation
{
    /// <summary>
    /// Calculates all enabled indicators asynchronously with explicit <paramref name="timeFrame"/> and <paramref name="cancellationToken"/>.
    /// </summary>
    Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Timeframe-aware overload that also supplies an inclusive as-of/end date cutoff so
    /// universe-breadth indicators trim constituent histories before aggregating W1/MN1 periods.
    /// </summary>
    Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        CancellationToken cancellationToken = default)
        => CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol, timeFrame, cancellationToken);
}
