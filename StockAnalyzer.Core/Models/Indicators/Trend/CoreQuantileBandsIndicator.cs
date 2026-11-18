using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Rolling quantile bands of the last <see cref="Period"/> prices (<see cref="OrderStatisticsMath"/>, price-native <see cref="decimal"/> arithmetic, no precision lost):
/// <c>Main</c> = the median, <c>UpperBand</c> = the type 7 quantile at <see cref="UpperQuantile"/>, <c>LowerBand</c> = the type 7 quantile at <see cref="LowerQuantile"/>. The upper and lower
/// bands follow the data (not a symmetric distance), so a skewed window gives an asymmetric channel. <c>IQR</c> = Q(0.75) - Q(0.25) and <c>MAD</c> = median(|x - median|) (raw, not scaled)
/// are price-unit spreads that are chart-suppressed (they are not price levels), see <see cref="IndicatorChartSuppression"/>.
/// Null for bars before bar <c>Period - 1</c> and for a window with a missing price. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.QuantileBands)]
public class CoreQuantileBandsIndicator : CoreIndicatorBase
{
    public const string UpperBandSeriesName = nameof(UpperBand);
    public const string LowerBandSeriesName = nameof(LowerBand);
    public const string IqrSeriesName = nameof(IQR);
    public const string MadSeriesName = nameof(MAD);

    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.QuantileBandsDefaultPeriod;

    public double UpperQuantile { get; set; } = IndicatorDefaultConstants.QuantileBandsDefaultUpperQuantile;

    public double LowerQuantile { get; set; } = IndicatorDefaultConstants.QuantileBandsDefaultLowerQuantile;

    public List<decimal?> UpperBand { get; } = new();

    public List<decimal?> LowerBand { get; } = new();

    public List<decimal?> IQR { get; } = new();

    public List<decimal?> MAD { get; } = new();

    public override string Name => $"Quantile Bands ({Period}, {LowerQuantile}-{UpperQuantile})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreQuantileBandsParameter p)
        {
            p.Validate();
            Period = p.Period;
            UpperQuantile = p.UpperQuantile;
            LowerQuantile = p.LowerQuantile;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        UpperBand.Clear();
        LowerBand.Clear();
        IQR.Clear();
        MAD.Clear();

        var parameters = new CoreQuantileBandsParameter { Period = Period, UpperQuantile = UpperQuantile, LowerQuantile = LowerQuantile };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        decimal? upperProbability = IndicatorCalculationHelper.ToDecimalOrNull(UpperQuantile);
        decimal? lowerProbability = IndicatorCalculationHelper.ToDecimalOrNull(LowerQuantile);
        var prices = new PriceSeriesWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        decimal[] buffer = new decimal[Period];
        decimal[] scratch = new decimal[Period];
        for (int i = 0; i < prices.Count; i++)
        {
            decimal? median = null;
            decimal? upper = null;
            decimal? lower = null;
            decimal? iqr = null;
            decimal? mad = null;
            int first = i - Period + 1;
            if (first >= 0 && upperProbability.HasValue && lowerProbability.HasValue && prices.IsComplete(first, i))
            {
                // Every order-statistic call only permutes the buffer, so one copy of the window serves all five statistics.
                prices.Values.AsSpan(first, Period).CopyTo(buffer);
                median = OrderStatisticsMath.Median<decimal>(buffer);
                upper = OrderStatisticsMath.QuantileType7<decimal>(buffer, upperProbability.Value);
                lower = OrderStatisticsMath.QuantileType7<decimal>(buffer, lowerProbability.Value);
                iqr = OrderStatisticsMath.InterquartileRange<decimal>(buffer);
                mad = OrderStatisticsMath.MedianAbsoluteDeviation<decimal>(buffer, scratch);
            }

            _values.Add(median);
            UpperBand.Add(upper);
            LowerBand.Add(lower);
            IQR.Add(iqr);
            MAD.Add(mad);
        }

        return CreateAutomaticResult();
    }
}
