using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.RegressionSlope)]
public class CoreRegressionSlopeIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Regression Slope ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int j = 0; j < Period; j++)
            {
                double x = j;
                double y = (double)candles[i - Period + 1 + j].Close;

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double slope = (Period * sumXY - sumX * sumY) / (Period * sumX2 - sumX * sumX);
            _values.Add((decimal)slope);
        }

        return IndicatorResult.Success(_values);
    }
}
