namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Implemented by an indicator parameter object that already owns a time-frame choice of its own (the Pivot Points'
/// <c>SourcePeriod</c>). Such an indicator is never aggregated by the settings-level <see cref="CoreIndicatorSettings.Timeframe"/>;
/// the value is only read to refuse a frame finer than the chart. Implemented explicitly so it does not appear as a parameter property.
/// </summary>
public interface ISelfTimeframeParameter
{
    /// <summary>The frame the indicator reads its source bars from (0/1/2 map to <see cref="IndicatorTimeframe"/>).</summary>
    IndicatorTimeframe SelfTimeframe { get; }
}
