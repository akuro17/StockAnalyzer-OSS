using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.TRIX)]
public class CoreTrixIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 15;
    public override string Name => $"TRIX ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTrixParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        decimal mult = 2m / (Period + 1);

        var ema1 = new List<decimal?>();
        var ema2 = new List<decimal?>();
        var ema3 = new List<decimal?>();

        // Triple EMA
        for (int pass = 0; pass < 3; pass++)
        {
            var source = pass == 0 ? PriceDataHelper.ExtractPriceSeries(candles, PriceSource) :
                        (pass == 1 ? ema1 : ema2);
            var target = pass == 0 ? ema1 : (pass == 1 ? ema2 : ema3);

            decimal? ema = null;
            for (int i = 0; i < source.Count; i++)
            {
                if (i < Period - 1) {
                    target.Add(null);
                    continue;
                }

                // Check if we can calculate the first SMA
                if (ema == null) {
                    var window = source.Skip(i - Period + 1).Take(Period);
                    if (window.Any(v => !v.HasValue)) {
                        target.Add(null);
                        continue;
                    }
                    ema = window.Average();
                } else {
                    if (!source[i].HasValue) { // Should not happen after first ema is calculated
                         target.Add(null);
                         continue;
                    }
                    ema = (source[i]!.Value - ema!.Value) * mult + ema!.Value;
                }
                target.Add(ema);
            }
        }

        // TRIX = ROC of triple EMA
        for (int i = 0; i < ema3.Count; i++)
        {
            if (i == 0 || !ema3[i].HasValue || !ema3[i - 1].HasValue)
            { _values.Add(null); continue; }

            decimal prev = ema3[i - 1]!.Value;
            _values.Add(prev == 0 ? 0 : ((ema3[i]!.Value - prev) / prev) * 100);
        }

        return IndicatorResult.Success(_values);
    }
}
