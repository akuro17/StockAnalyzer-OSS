using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// True Low indicator: Today's low, or the previous close, whichever is lower.
/// Rendered as a price overlay on the main chart.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TrueLow)]
public class CoreTrueLowIndicator : CoreIndicatorBase
{
    public override string Name => "True Low";

    public override bool IsOverlay => true;

    public override PriceType PriceSource { get; set; } = PriceType.TrueLow;

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

        _values.AddRange(PriceDataHelper.ExtractPriceSeries(candles, PriceType.TrueLow));
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
