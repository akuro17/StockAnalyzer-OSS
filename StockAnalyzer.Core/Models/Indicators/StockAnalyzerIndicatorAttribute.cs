
namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Maps an ICoreIndicator implementation to its corresponding IndicatorType.
/// Used by IndicatorFactory for automatic registration via reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StockAnalyzerIndicatorAttribute : Attribute
{
    /// <summary>
    /// The IndicatorType this class implements.
    /// </summary>
    public IndicatorType IndicatorType { get; }

    public StockAnalyzerIndicatorAttribute(IndicatorType indicatorType)
    {
        IndicatorType = indicatorType;
    }
}
