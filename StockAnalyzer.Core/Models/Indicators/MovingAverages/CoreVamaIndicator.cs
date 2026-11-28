using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.VAMA)]
public class CoreVamaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"VAMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);
        int requiredDataCount = 2 * Period - 1;
        if (Period <= 0 || series.Count < requiredDataCount)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, series.Count));
            return IndicatorResult.Success(_values);
        }

        var stdDevValues = StdDevWithNulls(series, Period);
        var smaOfStdDev = IndicatorCalculationHelper.CalculateSmaWithNulls(stdDevValues, Period);

        decimal? prevVama = null;

        for (int i = 0; i < series.Count; i++)
        {
            if (!series[i].HasValue || !stdDevValues[i].HasValue || !smaOfStdDev[i].HasValue || smaOfStdDev[i]!.Value == 0m)
            {
                _values.Add(null);
                continue;
            }

            if (prevVama == null)
            {
                // Seed the first VAMA value with the SMA of the non-null window ending at i
                decimal seedSum = 0m;
                bool seedValid = true;
                for (int j = i - Period + 1; j <= i; j++)
                {
                    if (!series[j].HasValue)
                    {
                        seedValid = false;
                        break;
                    }
                    seedSum += series[j]!.Value;
                }

                if (!seedValid)
                {
                    _values.Add(null);
                    continue;
                }

                prevVama = seedSum / Period;
            }

            var volatilityRatio = stdDevValues[i]!.Value / smaOfStdDev[i]!.Value;
            var smoothingFactor = 2m / (Period * volatilityRatio + 1m);
            var currentVama = prevVama.Value + smoothingFactor * (series[i]!.Value - prevVama.Value);
            _values.Add(currentVama);
            prevVama = currentVama;
        }

        return IndicatorResult.Success(_values);
    }

    private static List<decimal?> StdDevWithNulls(IReadOnlyList<decimal?> data, int period)
    {
        var results = new List<decimal?>(data.Count);
        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
                continue;
            }

            decimal sum = 0m;
            bool hasNull = false;
            for (int j = i - period + 1; j <= i; j++)
            {
                if (!data[j].HasValue)
                {
                    hasNull = true;
                    break;
                }
                sum += data[j]!.Value;
            }

            if (hasNull)
            {
                results.Add(null);
                continue;
            }

            decimal avg = sum / period;
            decimal sqSum = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                decimal diff = data[j]!.Value - avg;
                sqSum += diff * diff;
            }

            results.Add(IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)(sqSum / period))));
        }
        return results;
    }
}
