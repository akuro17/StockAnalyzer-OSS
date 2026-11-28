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

    // The parameter class is CoreKeltnerParameter, not the CoreKeltnerChannelParameter the base-class naming rule looks for.
    public override CoreIndicatorSettings GetDefaultSettings()
    {
        var settings = base.GetDefaultSettings();
        settings.ParameterObject = new CoreKeltnerParameter();
        return settings;
    }

    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        UpperBand.Clear();
        LowerBand.Clear();

        if (candles.Count == 0)
        {
            return CreateAutomaticResult();
        }

        if (EmaPeriod <= 0 || AtrPeriod <= 0)
        {
            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add(null);
                UpperBand.Add(null);
                LowerBand.Add(null);
            }
            return CreateAutomaticResult();
        }

        // Center line follows the configurable Price Source (defaults to Close, preserving prior behavior).
        // The ATR band width intentionally keeps using the real High/Low/Close — inherent to ATR's definition.
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        var emas = IndicatorCalculationHelper.CalculateEmaWithNulls(priceSeries, EmaPeriod);

        var atr = new CoreAtrIndicator { Period = AtrPeriod };
        atr.Calculate(candles);

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? ema = emas[i];
            decimal? atrV = atr.Values[i];
            if (!ema.HasValue || !atrV.HasValue)
            {
                _values.Add(null);
                UpperBand.Add(null);
                LowerBand.Add(null);
            }
            else
            {
                _values.Add(ema.Value);
                UpperBand.Add(ema.Value + Multiplier * atrV.Value);
                LowerBand.Add(ema.Value - Multiplier * atrV.Value);
            }
        }

        return CreateAutomaticResult();
    }
}
