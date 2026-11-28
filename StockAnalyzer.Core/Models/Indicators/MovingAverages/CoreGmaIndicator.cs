using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

/// <summary>
/// Core indicator implementation for Guppy Multiple Moving Average (GMMA).
/// Evaluates relationships between short-term trader sentiment (3, 5, 8, 10, 12, 15 EMA)
/// and long-term investor sentiment (30, 35, 40, 45, 50, 60 EMA).
/// Preserves the historical enum identifier <see cref="IndicatorType.GMA"/> for serialization and ABI compatibility.
/// </summary>
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

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>();
        var allPeriods = ShortTermPeriods.Concat(LongTermPeriods);

        foreach (var period in allPeriods)
        {
            resultSeries[$"EMA{period}"] = IndicatorCalculationHelper.CalculateEmaWithNulls(series, period);
        }

        // Set the first EMA as the "Main" series for backward compatibility
        if (resultSeries.Count > 0)
        {
            var first = resultSeries.First();
            resultSeries[IndicatorResult.MainSeriesName] = first.Value;
            _values.AddRange(first.Value);
        }

        return IndicatorResult.Success(resultSeries);
    }
}
