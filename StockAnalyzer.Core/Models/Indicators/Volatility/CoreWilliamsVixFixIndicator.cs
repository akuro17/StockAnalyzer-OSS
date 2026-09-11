using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.WilliamsVixFix)]
public class CoreWilliamsVixFixIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 22;
    public override string Name => $"Williams Vix Fix ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period; // SMA parameter matches Period
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count < Period)
        {
             if (candles != null) _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
            }
            else
            {
                var window = candles.Skip(i - Period + 1).Take(Period).ToList();
                var highestClose = window.Max(c => c.Close);
                var currentLow = candles[i].Low;

                if (highestClose != 0)
                {
                    var wvf = (highestClose - currentLow) / highestClose * 100;
                    _values.Add(wvf);
                }
                else
                {
                    _values.Add(null);
                }
            }
        }

        return IndicatorResult.Success(_values);
    }
}
