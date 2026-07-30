using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.ALMA)]
public class CoreAlmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 9;
    public double Offset { get; set; } = 0.85;
    public double Sigma { get; set; } = 6.0;

    public override string Name => $"ALMA ({Period}, {Offset}, {Sigma})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAlmaParameter p)
        {
            Period = p.Period;
            Offset = p.Offset;
            Sigma = p.Sigma;
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

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        var m = (decimal)(Offset * (Period - 1));
        var s = (decimal)(Period / Sigma);

        for (int i = Period - 1; i < candles.Count; i++)
        {
            decimal weightedSum = 0;
            decimal weightSum = 0;

            for (int j = 0; j < Period; j++)
            {
                decimal weight = (decimal)Math.Exp((double)-(((j - m) * (j - m)) / (2 * s * s)));
                weightedSum += candles[i - Period + 1 + j].Close * weight;
                weightSum += weight;
            }

            if (weightSum != 0)
            {
                _values.Add(weightedSum / weightSum);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
