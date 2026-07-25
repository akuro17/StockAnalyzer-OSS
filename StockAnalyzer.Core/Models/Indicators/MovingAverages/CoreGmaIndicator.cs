using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.GMA)]
public class CoreGmaIndicator : CoreIndicatorBase
{
    internal static readonly int[] ShortTermPeriods = { 3, 5, 8, 10, 12, 15 };
    internal static readonly int[] LongTermPeriods = { 30, 35, 40, 45, 50, 60 };

    public new static List<IReadOnlyList<decimal?>> Calculate(IReadOnlyList<CoreCandleData> candles)
    {
        var indicator = new CoreGmaIndicator();
        var result = ((ICoreIndicator)indicator).Calculate(candles);
        var list = new List<IReadOnlyList<decimal?>>();
        foreach (var name in result.SeriesNamesList)
        {
            if (name == IndicatorResult.MainSeriesName) continue;
            list.Add(result.GetSeries(name));
        }
        return list;
    }

    public override string Name => "GMA";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        // Currently GMA uses fixed periods, but we support the base parameter for future extension
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

        var series = new Dictionary<string, IReadOnlyList<decimal?>>();
        var allPeriods = ShortTermPeriods.Concat(LongTermPeriods);

        foreach (var period in allPeriods)
        {
            series[$"EMA{period}"] = CalculateEma(candles, period);
        }

        // Set the first EMA as the "Main" series for backward compatibility
        if (series.Count > 0)
        {
            var first = series.First();
            series[IndicatorResult.MainSeriesName] = first.Value;
        }

        return IndicatorResult.Success(series);
    }

    private List<decimal?> CalculateEma(IReadOnlyList<CoreCandleData> candles, int period)
    {
        var emaValues = new List<decimal?>();
        if (candles.Count < period)
        {
            for (int i = 0; i < candles.Count; i++)
            {
                emaValues.Add(null);
            }
            return emaValues;
        }

        decimal multiplier = 2.0m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                emaValues.Add(null);
            }
            else if (i == period - 1)
            {
                decimal sum = 0;
                for (int j = 0; j < period; j++)
                {
                    sum += candles[i - j].Close;
                }
                prevEma = sum / period;
                emaValues.Add(prevEma);
            }
            else
            {
                prevEma = (candles[i].Close - prevEma) * multiplier + prevEma;
                emaValues.Add(prevEma);
            }
        }
        return emaValues;
    }
}
