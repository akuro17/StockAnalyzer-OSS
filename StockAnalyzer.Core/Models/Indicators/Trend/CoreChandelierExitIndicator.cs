using System.Globalization;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Chandelier Exit (Charles LeBeau): two plain trailing-stop lines, no direction latch and no ratchet.
/// <c>Main</c> (long stop) = highest High over <see cref="Period"/> bars - <see cref="Multiplier"/> x ATR; <c>ShortStop</c> = lowest Low over <see cref="Period"/> bars + <see cref="Multiplier"/> x ATR.
/// ATR is the Wilder ATR of <see cref="CoreAtrIndicator"/> with the same period. Unit: price-native (the same unit as the candles). Causal: bar t uses bars &lt;= t only.
/// A bar whose highest high, lowest low or ATR is undefined is null (the first value is at bar index <see cref="Period"/>). Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.ChandelierExit)]
public class CoreChandelierExitIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.ChandelierExitDefaultPeriod;
    public decimal Multiplier { get; set; } = IndicatorDefaultConstants.ChandelierExitDefaultMultiplier;

    public override string Name => string.Create(CultureInfo.InvariantCulture, $"Chandelier Exit ({Period}, {Multiplier})");

    /// <summary>
    /// The Main series under the output name "LongStop". Output-name discovery (IndicatorOutputSeriesResolver) lists the public series properties,
    /// so without this alias the ShortStop property would be the only selectable output and the long stop could not be chosen by name.
    /// </summary>
    public IReadOnlyList<decimal?> LongStop => _values;

    /// <summary>The short-position stop: lowest Low + Multiplier x ATR.</summary>
    public List<decimal?> ShortStop { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreChandelierExitParameter p)
        {
            p.Validate();
            Period = p.Period;
            Multiplier = p.Multiplier;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        ShortStop.Clear();
        int count = candles.Count;

        var highs = PriceDataHelper.ExtractPriceSeries(candles, PriceType.High);
        var lows = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Low);
        var highestHigh = RollingExtremeHelper.CalculateRollingMax(highs, Period);
        var lowestLow = RollingExtremeHelper.CalculateRollingMin(lows, Period);

        var atr = new CoreAtrIndicator { Period = Period };
        atr.Calculate(candles);
        var atrValues = atr.Values;
        // CoreAtrIndicator leaves its list empty for fewer than two candles.
        bool atrAvailable = atrValues.Count == count;

        _values.Capacity = count;
        ShortStop.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            decimal? atrValue = atrAvailable ? atrValues[i] : null;
            if (atrValue is null || highestHigh[i] is null || lowestLow[i] is null)
            {
                _values.Add(null);
                ShortStop.Add(null);
                continue;
            }

            decimal distance = Multiplier * atrValue.Value;
            _values.Add(highestHigh[i]!.Value - distance);
            ShortStop.Add(lowestLow[i]!.Value + distance);
        }

        // Explicit result: CreateAutomaticResult would also emit the LongStop alias as a duplicate series.
        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { nameof(ShortStop), ShortStop },
        });
    }
}
