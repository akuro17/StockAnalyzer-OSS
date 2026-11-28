using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.KylesLambda)]
public class CoreKylesLambdaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Kyle's Lambda ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        for (int i = 0; i < candles.Count; i++)
        {
            if (Period <= 0 || i < Period)
            {
                _values.Add(null);
                continue;
            }

            // Lambda = Sum(|DeltaP|) / Sum(Volume) over Period
            decimal sumAbsOnyx = 0m;
            decimal sumVol = 0m;
            bool hasNull = false;

            for (int j = 0; j < Period; j++)
            {
                decimal? curr = priceSeries[i - j];
                decimal? prev = priceSeries[i - j - 1];
                if (!curr.HasValue || !prev.HasValue)
                {
                    hasNull = true;
                    break;
                }
                sumAbsOnyx += Math.Abs(curr.Value - prev.Value);
                sumVol += candles[i - j].Volume;
            }

            if (hasNull) _values.Add(null);
            else if (sumVol == 0m) _values.Add(0m);
            else _values.Add(sumAbsOnyx / sumVol * 1000000m); // Scale up
        }

        return IndicatorResult.Success(_values);
    }
}
