using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.ChaikinVolatility)]
public class CoreChaikinVolatilityIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 10;
    public int RocPeriod { get; set; } = 10;
    public override string Name => $"Chaikin Volatility ({Period}, {RocPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreChaikinVolatilityParameter p)
        {
            Period = p.Period;
            RocPeriod = p.RocPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count < Period + RocPeriod)
        {
             if (candles != null) _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var highLowSpread = candles.Select(c => c.High - c.Low).ToList();
        var emaOfSpread = Ema(highLowSpread, Period);

        for (int i = 0; i < emaOfSpread.Count; i++)
        {
            if (i < RocPeriod || emaOfSpread[i - RocPeriod] == null || emaOfSpread[i - RocPeriod] == 0)
            {
                _values.Add(null);
            }
            else
            {
                var roc = (emaOfSpread[i] - emaOfSpread[i - RocPeriod]) / emaOfSpread[i - RocPeriod] * 100;
                _values.Add(roc);
            }
        }

        return IndicatorResult.Success(_values);
    }

    private static List<decimal?> Ema(IReadOnlyList<decimal> data, int period)
    {
        var emaValues = new List<decimal?>();
        if (data.Count < period)
        {
            for (int i = 0; i < data.Count; i++) emaValues.Add(null);
            return emaValues;
        }

        decimal multiplier = 2.0m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1)
            {
                emaValues.Add(null);
            }
            else if (i == period - 1)
            {
                prevEma = data.Take(period).Average();
                emaValues.Add(prevEma);
            }
            else
            {
                prevEma = (data[i] - prevEma) * multiplier + prevEma;
                emaValues.Add(prevEma);
            }
        }
        return emaValues;
    }
}
