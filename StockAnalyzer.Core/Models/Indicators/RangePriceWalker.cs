using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// The selected price of the bars of a drawing range, one bar at a time, oldest first (the single definition of the initial state of a range; used by the distribution range and by
/// <c>DrawingRangePriceSeries</c>). The bar BEFORE the first bar of the range is never read:
/// <list type="bullet">
/// <item>The first bar has no previous close, so True High / True Low are its High / Low (<see cref="PriceDataHelper.ExtractPrice(in CoreCandleData, PriceType, decimal?, decimal?, decimal?)"/> with a null previous close); every later bar uses the close of the bar before it.</item>
/// <item>The Heikin-Ashi state starts at the first bar (<see cref="HeikinAshiConverter.CalculateInitialValues"/>) and moves on with <see cref="HeikinAshiConverter.NextState"/>.</item>
/// <item>The other price types read the bar itself.</item>
/// </list>
/// A mutable value type: keep it in a local variable and call <see cref="Next"/> for every bar in order. No allocation. Every field is derived from the price type at the time of the call, so
/// <c>default</c> is a complete, consistent walker of <see cref="PriceType.Close"/> (the zero value of the enum) and cannot disagree with itself.
/// </summary>
public struct RangePriceWalker
{
    private readonly PriceType _priceType;
    private decimal? _previousClose;
    private decimal? _previousHaOpen;
    private decimal? _previousHaClose;

    public RangePriceWalker(PriceType priceType)
    {
        _priceType = priceType;
        _previousClose = null;
        _previousHaOpen = null;
        _previousHaClose = null;
    }

    /// <summary>The price of <paramref name="candle"/>, the next bar of the range; the state moves on to it.</summary>
    public decimal Next(in CoreCandleData candle)
    {
        decimal price = PriceDataHelper.ExtractPrice(in candle, _priceType, _previousClose, _previousHaOpen, _previousHaClose);
        _previousClose = candle.Close;
        if (PriceDataHelper.IsHeikinAshi(_priceType))
        {
            (_previousHaOpen, _previousHaClose) = (_previousHaOpen.HasValue && _previousHaClose.HasValue)
                ? HeikinAshiConverter.NextState(candle, _previousHaOpen.Value, _previousHaClose.Value)
                : HeikinAshiConverter.CalculateInitialValues(candle);
        }

        return price;
    }
}
