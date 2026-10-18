using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.DMI)]
public class CoreDmiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"DMI ({Period})";
    
    public List<decimal?> PlusDI { get; } = new();
    public List<decimal?> MinusDI { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreDmiParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        PlusDI.Clear();
        MinusDI.Clear();

        if (candles == null || candles.Count == 0)
        {
            return CreateAutomaticResult();
        }

        var (plusDi, minusDi, _) = IndicatorCalculationHelper.CalculateWilderDmiSeries(candles, Period);
        PlusDI.AddRange(plusDi);
        MinusDI.AddRange(minusDi);
        _values.AddRange(plusDi);

        return CreateAutomaticResult();
    }
}
