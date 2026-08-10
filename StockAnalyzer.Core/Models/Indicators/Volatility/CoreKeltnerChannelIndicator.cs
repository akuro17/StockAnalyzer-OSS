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
        var candleDataList = candles.ToList();

        decimal mult = 2m / (EmaPeriod + 1);
        decimal? ema = null;

        var atr = new CoreAtrIndicator { Period = AtrPeriod };
        atr.Calculate(candles);

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < System.Math.Max(EmaPeriod, AtrPeriod) - 1 || !atr.Values[i].HasValue)
            { _values.Add(null); UpperBand.Add(null); LowerBand.Add(null); continue; }

            if (ema == null)
            {
                decimal sum = 0;
                for (int j = 0; j < EmaPeriod; j++) sum += candles[i - j].Close;
                ema = sum / EmaPeriod;
            }
            else ema = (candles[i].Close - ema.Value) * mult + ema.Value;

            _values.Add(ema);
            UpperBand.Add(ema + Multiplier * atr.Values[i]!.Value);
            LowerBand.Add(ema - Multiplier * atr.Values[i]!.Value);
        }

        return IndicatorResult.Success(_values);
    }
}
