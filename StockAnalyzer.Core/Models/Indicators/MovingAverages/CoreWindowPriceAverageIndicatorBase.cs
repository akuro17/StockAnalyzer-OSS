using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

/// <summary>
/// Base of the price-native window averages (trimmed, winsorized, Huber, biweight, geometric, harmonic): one value per chart bar from the last <see cref="Period"/> prices of the price source
/// (<c>[i - Period + 1, i]</c>), drawn on the price pane. Null for bars before bar <c>Period - 1</c> and for a window with a missing price. The statistic itself is the only part a derived
/// indicator supplies (<see cref="Compute"/>). Causal: bar t uses bars &lt;= t only.
/// </summary>
public abstract class CoreWindowPriceAverageIndicatorBase : CoreIndicatorBase
{
    public int Period { get; set; } = IndicatorDefaultConstants.WindowAverageDefaultPeriod;

    public override bool IsOverlay => true;

    /// <summary>A parameter object with the current settings; its <c>Validate()</c> decides whether the indicator calculates.</summary>
    protected abstract CoreWindowAverageParameterBase CreateParameters();

    /// <summary>The average of one complete window of <see cref="Period"/> prices, or null where it is undefined.</summary>
    protected abstract decimal? Compute(ReadOnlySpan<decimal> window);

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        try
        {
            CreateParameters().Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var prices = new PriceSeriesWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        for (int i = 0; i < prices.Count; i++)
        {
            decimal? value = null;
            int first = i - Period + 1;
            if (first >= 0 && prices.IsComplete(first, i))
            {
                try
                {
                    value = Compute(prices.Values.AsSpan(first, Period));
                }
                catch (OverflowException)
                {
                    value = null;
                }
            }

            _values.Add(value);
        }

        return CreateAutomaticResult();
    }
}
