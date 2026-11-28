using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.ZScore)]
public class CoreZScoreIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Z-Score ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (Period <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            decimal sum = 0m;
            bool hasNull = false;
            for (int j = 0; j < Period; j++)
            {
                decimal? val = series[i - j];
                if (!val.HasValue)
                {
                    hasNull = true;
                    break;
                }
                sum += val.Value;
            }

            if (hasNull)
            {
                _values.Add(null);
                continue;
            }

            decimal mean = sum / Period;
            decimal sumSqDiff = 0m;
            for (int j = 0; j < Period; j++)
            {
                decimal diff = series[i - j]!.Value - mean;
                sumSqDiff += diff * diff;
            }

            decimal? stdDev = IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)(sumSqDiff / Period)));
            if (!stdDev.HasValue)
            {
                _values.Add(null);
            }
            else if (stdDev.Value == 0m)
            {
                _values.Add(0m);
            }
            else
            {
                _values.Add((series[i]!.Value - mean) / stdDev.Value);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
