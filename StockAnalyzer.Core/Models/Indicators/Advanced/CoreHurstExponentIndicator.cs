using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

// [StockAnalyzerIndicator(IndicatorType.HurstExponent)] // Unimplemented stub
public class CoreHurstExponentIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 30;
    public override string Name => $"Hurst Exponent ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
         if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // R/S Analysis Implementation Placeholder
        _values.Clear();
         foreach(var c in candles) _values.Add(null);

        return IndicatorResult.Success(_values);
    }
}
