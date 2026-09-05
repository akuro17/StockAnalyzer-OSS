using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Models.Indicators;

public interface ICoreIndicator
{
    string Name { get; }
    
    /// <summary>
    /// Primary series values (for backward compatibility).
    /// After Calculate() is called, this should return the main series.
    /// </summary>
    IReadOnlyList<decimal?> Values { get; }
    
    /// <summary>
    /// Configures the indicator with the given parameters.
    /// </summary>
    void Configure(CoreIndicatorParameterBase parameters);
    
    /// <summary>
    /// Calculates the indicator values for the given candles.
    /// Returns an IIndicatorResult containing the result(s).
    /// </summary>
    IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles);

    /// <summary>
    /// Calculates the indicator values directly from a price series (IReadOnlyList<decimal?>).
    /// Used for indicator chaining (Indicator on Indicator) or direct series inputs with optional dynamic periods.
    /// </summary>
    IIndicatorResult CalculateSeries(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null);

    /// <summary>
    /// Calculates the indicator values asynchronously using the provided execution context.
    /// Used for indicators that require external processes or heavy async work.
    /// </summary>
    Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context);

    /// <summary>
    /// Gets the default settings for this indicator, including the correct parameter object.
    /// Used by the UI to auto-generate settings for newly added indicators.
    /// </summary>
    CoreIndicatorSettings GetDefaultSettings();
}
