using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Supported moving average types for baseline calculations in derived indicators like MA Deviation Rate.
/// </summary>
public enum MovingAverageType
{
    [Description("Arnaud Legoux Moving Average")] ALMA,
    [Description("Double Exponential Moving Average")] DEMA,
    [Description("Exponential Moving Average")] EMA,
    [Description("Hull Moving Average")] HMA,
    [Description("Kaufman's Adaptive Moving Average")] KAMA,
    [Description("Linear Regression Moving Average")] LRMA,
    [Description("Least Squares Moving Average")] LSMA,
    [Description("Simple Moving Average")] SMA,
    [Description("Smoothed Moving Average")] SMMA,
    [Description("Triple Exponential Moving Average")] TEMA,
    [Description("Triangular Moving Average")] TMA,
    [Description("Weighted Moving Average")] WMA,
    [Description("Zero Lag Exponential Moving Average")] ZLEMA
}
