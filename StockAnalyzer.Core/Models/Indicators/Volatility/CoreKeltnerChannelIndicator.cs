using System;

using StockAnalyzer.Core.Models.Parameters;

using Math = System.Math;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.Keltner)]
public class CoreKeltnerChannelIndicator : CoreIndicatorBase
{
    public int EmaPeriod { get; set; } = 20;
    public int AtrPeriod { get; set; } = 10;
    public decimal Multiplier { get; set; } = 2.0m;

    public override string Name => $"Keltner ({EmaPeriod}, {Multiplier})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKeltnerParameter p)
        {
            EmaPeriod = p.EmaPeriod;
            AtrPeriod = p.AtrPeriod;
            Multiplier = p.Multiplier;
        }
    }

    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        UpperBand.Clear();
        LowerBand.Clear();

        // Center line follows the configurable Price Source (defaults to Close, preserving prior behavior).
        // The ATR band width intentionally keeps using the real High/Low/Close — inherent to ATR's definition.
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        decimal mult = 2m / (EmaPeriod + 1);
        decimal? ema = null;

        var atr = new CoreAtrIndicator { Period = AtrPeriod };
        atr.Calculate(candles);

        int emaSeedIndex = EmaPeriod - 1;

        for (int i = 0; i < candles.Count; i++)
        {
            if (i >= emaSeedIndex)
            {
                if (ema == null)
                {
                    decimal sum = 0;
                    for (int j = 0; j < EmaPeriod; j++) sum += priceSeries[i - j] ?? 0m;
                    ema = sum / EmaPeriod;
                }
                else
                {
                    ema = ((priceSeries[i] ?? 0m) - ema.Value) * mult + ema.Value;
                }
            }

            var atrV = atr.Values[i];
            if (ema == null || !atrV.HasValue)
            {
                _values.Add(null);
                UpperBand.Add(null);
                LowerBand.Add(null);
            }
            else
            {
                _values.Add(ema);
                UpperBand.Add(ema + Multiplier * atrV.Value);
                LowerBand.Add(ema - Multiplier * atrV.Value);
            }
        }

        return CreateAutomaticResult();
    }
}
