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

        decimal prevAdx = 0m;
        for (int i = 0; i < n; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
            }
            else if (i == Period - 1)
            {
                prevAdx = dxList[i] ?? 0m;
                _values.Add(prevAdx);
            }
            else
            {
                decimal dx = dxList[i] ?? 0m;
                decimal adx = (prevAdx * (Period - 1) + dx) / Period;
                prevAdx = adx;
                _values.Add(adx);
            }
        }

        return CreateAutomaticResult();
    }
}
