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

        // Only the first Period true ranges are ever averaged (bars 1..Period), so a running sum replaces the list of every true range.
        decimal firstWindowSum = 0m;
        for (int i = 1; i < candles.Count; i++)
        {
            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                                  Math.Abs(candles[i].Low - candles[i - 1].Close)));
            if (i <= Period) firstWindowSum += tr;

            if (i < Period) { _values.Add(null); continue; }

            if (i == Period)
                _values.Add(firstWindowSum / Period);
            else
                _values.Add(((_values[^1]!.Value * (Period - 1)) + tr) / Period);
        }

        return IndicatorResult.Success(_values);
    }
}
