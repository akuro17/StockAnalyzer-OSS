using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

// [StockAnalyzerIndicator(IndicatorType.FFTCycle)] // Unimplemented stub
public class CoreFFTCycleIndicator : CoreIndicatorBase
{
    public override string Name => "FFT Cycle";
    public override void Configure(CoreIndicatorParameterBase parameters) {}
    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles) 
    {

        foreach(var c in candles) _values.Add(null);

        return IndicatorResult.Success(_values);
    }
}
