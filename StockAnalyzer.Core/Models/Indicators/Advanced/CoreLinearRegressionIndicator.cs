using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.LinearRegression)]
public class CoreLinearRegressionIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Linear Regression ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

        if (candles.Count < Period || Period <= 1)
        {
            for (int i = 0; i < candles.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        decimal sumX = 0m;
        decimal sumX2 = 0m;
        for (int j = 0; j < Period; j++)
        {
            sumX += j;
            sumX2 += j * j;
        }
        decimal denom = Period * sumX2 - sumX * sumX;

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            bool hasNull = false;
            decimal sumY = 0m;
            decimal sumXY = 0m;
            int start = i - Period + 1;

            for (int j = 0; j < Period; j++)
            {
                var y = priceSeries[start + j];
                if (!y.HasValue)
                {
                    hasNull = true;
                    break;
                }
                sumY += y.Value;
                sumXY += j * y.Value;
            }

            if (hasNull || denom == 0m)
            {
                _values.Add(null);
                continue;
            }

            try
            {
                checked
                {
                    decimal slope = (Period * sumXY - sumX * sumY) / denom;
                    decimal intercept = (sumY - slope * sumX) / Period;
                    decimal result = slope * (Period - 1) + intercept;
                    _values.Add(result);
                }
            }
            catch (System.OverflowException)
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
