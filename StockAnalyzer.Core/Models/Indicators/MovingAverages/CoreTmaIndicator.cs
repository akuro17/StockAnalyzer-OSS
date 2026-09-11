using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.TMA)]
public class CoreTmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"TMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);
        if (candles.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var sma1Period = Period / 2;
        var sma2Period = Period / 2 + 1;

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        var sma1Values = Sma(priceSeries, sma1Period);
        var sma2Values = Sma(sma1Values, sma2Period);

        _values.AddRange(sma2Values);

        return IndicatorResult.Success(_values);
    }

    private List<decimal?> Sma(IReadOnlyList<decimal?> data, int period)
    {
        var results = new List<decimal?>();
        if (period <= 0) return Enumerable.Repeat<decimal?>(null, data.Count).ToList();

        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
            }
            else
            {
                decimal? sum = 0;
                bool hasNull = false;
                for (int j = 0; j < period; j++)
                {
                    var val = data[i - j];
                    if (val.HasValue)
                    {
                        sum += val.Value;
                    }
                    else
                    {
                        hasNull = true;
                        break;
                    }
                }
                if (hasNull)
                {
                    results.Add(null);
                }
                else
                {
                    results.Add(sum.Value / period);
                }
            }
        }
        return results;
    }
}
