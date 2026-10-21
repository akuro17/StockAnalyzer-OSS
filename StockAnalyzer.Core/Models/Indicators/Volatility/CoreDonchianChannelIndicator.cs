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
    public List<decimal?> Position { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Donchian Channel is defined against the candles' actual High/Low (not a user-selectable
        // Price Source): it always plots the true rolling high/low, matching its conventional definition.
        var highSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.High);
        var lowSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Low);
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        var upper = RollingExtremeHelper.CalculateRollingMax(highSeries, Period);
        var lower = RollingExtremeHelper.CalculateRollingMin(lowSeries, Period);

        UpperBand.Clear();
        LowerBand.Clear();
        Position.Clear();
        _values.Clear();

        if (_values is List<decimal?> vList) vList.Capacity = candles.Count;
        UpperBand.Capacity = candles.Count;
        LowerBand.Capacity = candles.Count;
        Position.Capacity = candles.Count;

        UpperBand.AddRange(upper);
        LowerBand.AddRange(lower);

        for (int i = 0; i < candles.Count; i++)
        {
            if (upper[i].HasValue && lower[i].HasValue)
            {
                decimal u = upper[i]!.Value;
                decimal l = lower[i]!.Value;
                _values.Add((u + l) / 2m);

                decimal delta = u - l;
                var price = priceSeries[i];
                if (!price.HasValue)
                {
                    Position.Add(null);
                }
                else if (delta == 0m)
                {
                    Position.Add(0.5m);
                }
                else
                {
                    Position.Add(Math.Clamp((price.Value - l) / delta, 0.0m, 1.0m));
                }
            }
            else
            {
                _values.Add(null);
                Position.Add(null);
            }
        }

        var series = new Dictionary<string, IReadOnlyList<decimal?>>(StringComparer.OrdinalIgnoreCase)
        {
            { nameof(UpperBand), UpperBand },
            { IndicatorResult.MainSeriesName, _values },
            { nameof(LowerBand), LowerBand },
            { nameof(Position), Position }
        };

        return IndicatorResult.Success(series);
    }
}
