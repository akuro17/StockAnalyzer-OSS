using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.ADX)]
public class CoreAdxIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"ADX ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdxParameter p)
        {
            Period = p.Period;
        }
    }

    public List<decimal?> PlusDI { get; } = new();
    public List<decimal?> MinusDI { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        PlusDI.Clear();
        MinusDI.Clear();

        if (candles == null || candles.Count == 0)
        {
            return CreateAutomaticResult();
        }

        int n = candles.Count;
        if (Period <= 0 || n < Period)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                PlusDI.Add(null);
                MinusDI.Add(null);
            }
            return CreateAutomaticResult();
        }

        var (plusDi, minusDi, dxList) = IndicatorCalculationHelper.CalculateWilderDmiSeries(candles, Period);
        PlusDI.AddRange(plusDi);
        MinusDI.AddRange(minusDi);

        _values.AddRange(IndicatorCalculationHelper.CalculateWilderAdxSeries(dxList, Period));

        return CreateAutomaticResult();
    }
}
