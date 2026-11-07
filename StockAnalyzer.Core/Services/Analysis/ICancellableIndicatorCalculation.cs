using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// An indicator calculation that stops between indicators when its token is cancelled. Implemented by the analysis pipeline and by the read-only gateway that
/// forwards to it. Kept apart from <see cref="IAnalysisPipelineService"/> and from the gateway interface on purpose, so that every other implementation of those
/// (the chart's fakes and mocks, proxies) stays as it is: a consumer asks <c>x is ICancellableIndicatorCalculation</c> and falls back to the plain call when it is not.
/// A wrapper that does not implement this interface does not inherit the capability.
/// </summary>
public interface ICancellableIndicatorCalculation
{
    /// <summary>
    /// Calculates the indicators like <see cref="IAnalysisPipelineService.CalculateIndicatorsAsync"/>, and throws <see cref="System.OperationCanceledException"/>
    /// before it starts an indicator when <paramref name="cancellationToken"/> is cancelled. An indicator that is already running is not interrupted, and a
    /// cancellation is never turned into a failed result of an indicator. All parameters are required so that a call with fewer arguments can only mean the plain method.
    /// </summary>
    Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        CancellationToken cancellationToken);
}
