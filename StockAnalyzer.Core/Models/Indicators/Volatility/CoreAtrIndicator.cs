using StockAnalyzer.Core.Models.Parameters;
using Math = System.Math;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.ATR)]
public class CoreAtrIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"ATR ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAtrParameter p)
        {
            Period = p.Period; 
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count < 2) return IndicatorResult.Success(_values);
        _values.Add(null);

        var trList = new List<decimal>();
        for (int i = 1; i < candles.Count; i++)
        {
            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                                  Math.Abs(candles[i].Low - candles[i - 1].Close)));
            trList.Add(tr);

            if (i < Period) { _values.Add(null); continue; }

            if (i == Period)
                _values.Add(trList.Average());
            else
                _values.Add(((_values[^1]!.Value * (Period - 1)) + tr) / Period);
        }

        return IndicatorResult.Success(_values);
    }
}
