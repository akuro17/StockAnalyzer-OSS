using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Price indicator: Renders raw or transformed price series (Close, Median, Heikin-Ashi, True High, etc.)
/// as a price overlay or panel line on the chart.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Price)]
public class CorePriceIndicator : CoreIndicatorBase
{
    public override string Name => "Price";

    public override bool IsOverlay => true;

    public override PriceType PriceSource { get; set; } = PriceType.Close;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        // Parameterless indicator
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        _values.AddRange(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        return IndicatorResult.Success(_values);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        _values.AddRange(series);
        return IndicatorResult.Success(_values);
    }
}
