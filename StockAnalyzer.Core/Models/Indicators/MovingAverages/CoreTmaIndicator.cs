using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.TMA)]
public class CoreTmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"TMA ({Period})";

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

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        return CalculateSeriesCore(priceSeries, null);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        if (series.Count < Period || Period <= 0)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, series.Count));
            return IndicatorResult.Success(_values);
        }

        int n1 = (Period % 2 == 0) ? (Period / 2) : ((Period + 1) / 2);
        int n2 = (Period / 2) + 1;

        var sma1Values = IndicatorCalculationHelper.CalculateSmaWithNulls(series, n1);
        var sma2Values = IndicatorCalculationHelper.CalculateSmaWithNulls(sma1Values, n2);

        _values.AddRange(sma2Values);
        return IndicatorResult.Success(_values);
    }
}
