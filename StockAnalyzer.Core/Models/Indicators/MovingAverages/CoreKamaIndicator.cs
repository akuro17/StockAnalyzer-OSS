using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.KAMA)]
public class CoreKamaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 10;
    public int Fast { get; set; } = 2;
    public int Slow { get; set; } = 30;

    public override string Name => $"KAMA ({Period}, {Fast}, {Slow})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKamaParameter p)
        {
            Period = p.Period;
            Fast = p.Fast;
            Slow = p.Slow;
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

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        decimal? kama = null;
        decimal fastAlpha = 2.0m / (Fast + 1);
        decimal slowAlpha = 2.0m / (Slow + 1);

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period)
            {
                _values.Add(null);
                if (i == Period - 1)
                {
                    kama = priceSeries.Take(Period).Average(v => v ?? 0m);
                }
                continue;
            }

            decimal change = Math.Abs((priceSeries[i] ?? 0m) - (priceSeries[i - Period] ?? 0m));

            decimal volatility = 0;
            for (int j = 0; j < Period; j++)
            {
                volatility += Math.Abs((priceSeries[i - j] ?? 0m) - (priceSeries[i - j - 1] ?? 0m));
            }

            if (volatility == 0)
            {
                _values.Add(kama); // If no volatility, KAMA remains the same.
                continue;
            }

            decimal er = change / volatility;
            decimal sc = er * (fastAlpha - slowAlpha) + slowAlpha;
            decimal scSquared = sc * sc;

            kama = kama + scSquared * ((priceSeries[i] ?? 0m) - kama);
            _values.Add(kama);
        }

        return IndicatorResult.Success(_values);
    }
}
