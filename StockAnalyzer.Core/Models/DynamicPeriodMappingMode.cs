namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines how a dynamic driver series is mapped to an indicator's calculation period.
/// </summary>
public enum DynamicPeriodMappingMode
{
    /// <summary>
    /// Direct mapping: Period = Driver value (e.g. for FFT Cycle dominant periods).
    /// </summary>
    Direct,

    /// <summary>
    /// Inverse ratio mapping: Period = BasePeriod * (ReferenceValue / DriverValue).
    /// Higher driver values (e.g. High Volatility / High ATR) produce shorter periods (higher sensitivity).
    /// </summary>
    InverseRatio,

    /// <summary>
    /// Normalized ratio mapping: maps a [0.0 .. 1.0] driver ratio to [minPeriod .. maxPeriod].
    /// </summary>
    NormalizedRange
}
