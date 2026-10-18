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
        _values.Clear();
        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

        if (candles.Count < Period || Period <= 0 || Sigma <= 0)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        decimal m = (decimal)Offset * (Period - 1);
        decimal s = (decimal)Period / (decimal)Sigma;

        decimal[] weights = new decimal[Period];
        decimal weightSum = 0m;
        decimal twoSS = 2m * s * s;

        for (int j = 0; j < Period; j++)
        {
            decimal diff = j - m;
            double exponent = (double)(-(diff * diff) / twoSS);
            weights[j] = (decimal)Math.Exp(exponent);
            weightSum += weights[j];
        }

        for (int i = Period - 1; i < candles.Count; i++)
        {
            bool hasNull = false;
            decimal weightedSum = 0m;
            int start = i - Period + 1;

            for (int j = 0; j < Period; j++)
            {
                var price = priceSeries[start + j];
                if (!price.HasValue)
                {
                    hasNull = true;
                    break;
                }
                weightedSum += price.Value * weights[j];
            }

            if (hasNull || weightSum == 0m)
            {
                _values.Add(null);
            }
            else
            {
                _values.Add(weightedSum / weightSum);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
