using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.WilderSmoothing)]
public class CoreWilderSmoothingIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Wilder's Smoothing ({Period})";

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
        if (Period <= 0 || series.Count < Period)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        decimal? prev = null;
        int validCount = 0;

        for (int i = 0; i < series.Count; i++)
        {
            if (!series[i].HasValue)
            {
                _values.Add(null);
                continue;
            }

            validCount++;
            if (validCount < Period)
            {
                _values.Add(null);
            }
            else if (validCount == Period)
            {
                decimal sum = 0m;
                int found = 0;
                for (int j = i; j >= 0 && found < Period; j--)
                {
                    if (series[j].HasValue)
                    {
                        sum += series[j]!.Value;
                        found++;
                    }
                }
                prev = sum / Period;
                _values.Add(prev);
            }
            else
            {
                // WSMA = (Prior WSMA * (n-1) + Current Price) / n
                prev = (prev!.Value * (Period - 1) + series[i]!.Value) / Period;
                _values.Add(prev);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
