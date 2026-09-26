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
        
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            // Calculate Linear Regression for the window ending at i
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int j = 0; j < Period; j++)
            {
                // x = 0 to Period-1
                double x = j;
                double y = (double)(priceSeries[i - Period + 1 + j] ?? 0m);

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double slope = (Period * sumXY - sumX * sumY) / (Period * sumX2 - sumX * sumX);
            double intercept = (sumY - slope * sumX) / Period;

            // Value at the end of the window (x = Period - 1)
            decimal result = (decimal)(slope * (Period - 1) + intercept);
            _values.Add(result);
        }

        return IndicatorResult.Success(_values);
    }
}
