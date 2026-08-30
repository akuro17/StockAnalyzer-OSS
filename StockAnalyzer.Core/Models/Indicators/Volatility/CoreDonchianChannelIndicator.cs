using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.Donchian)]
public class CoreDonchianChannelIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Donchian ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Donchian Channel is defined against the candles' actual High/Low (not a user-selectable
        // Price Source): it always plots the true rolling high/low, matching its conventional definition.
        var highSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.High);
        var lowSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Low);

        var upper = RollingExtremeHelper.CalculateRollingMax(highSeries, Period);
        var lower = RollingExtremeHelper.CalculateRollingMin(lowSeries, Period);

        UpperBand.Clear();
        LowerBand.Clear();
        UpperBand.AddRange(upper);
        LowerBand.AddRange(lower);

        _values.Clear();
        for (int i = 0; i < candles.Count; i++)
        {
            _values.Add(upper[i].HasValue && lower[i].HasValue ? (upper[i]!.Value + lower[i]!.Value) / 2 : null);
        }

        return IndicatorResult.Success(_values);
    }
}
