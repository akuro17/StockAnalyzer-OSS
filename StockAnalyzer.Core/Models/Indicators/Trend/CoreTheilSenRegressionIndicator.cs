using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Rolling robust regression line of the last <see cref="Period"/> prices (<see cref="RobustRegressionMath"/>, price-native <see cref="decimal"/> arithmetic): the Theil-Sen line (median of all pair slopes)
/// or, with <see cref="UseSiegelMedian"/>, the Siegel repeated-median line. <c>Main</c> = the line value at the newest bar of the window (a price); <c>UpperBand</c> and <c>LowerBand</c> =
/// <c>Main</c> +/- <see cref="BandMultiplier"/> x 1.4826 x the median absolute deviation of the residuals. <c>Slope</c> (price per bar) and <c>Intercept</c> (the line value at the OLDEST bar of the
/// window, so it is window-relative) are chart-suppressed: they are not price levels of the bar (see <see cref="IndicatorChartSuppression"/>).
/// The window is limited to <see cref="RobustRegressionMath.MaxWindowLength"/> bars (cost quadratic in the window). Null for bars before bar <c>Period - 1</c> and for a window with a missing price.
/// Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TheilSenRegression)]
public class CoreTheilSenRegressionIndicator : CoreIndicatorBase
{
    public const string UpperBandSeriesName = nameof(UpperBand);
    public const string LowerBandSeriesName = nameof(LowerBand);
    public const string SlopeSeriesName = nameof(Slope);
    public const string InterceptSeriesName = nameof(Intercept);

    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.TheilSenDefaultPeriod;

    public double BandMultiplier { get; set; } = IndicatorDefaultConstants.TheilSenDefaultBandMultiplier;

    public bool UseSiegelMedian { get; set; } = IndicatorDefaultConstants.TheilSenDefaultUseSiegelMedian;

    public List<decimal?> UpperBand { get; } = new();

    public List<decimal?> LowerBand { get; } = new();

    public List<decimal?> Slope { get; } = new();

    public List<decimal?> Intercept { get; } = new();

    public override string Name => UseSiegelMedian ? $"Siegel Regression ({Period})" : $"Theil-Sen Regression ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTheilSenRegressionParameter p)
        {
            p.Validate();
            Period = p.Period;
            BandMultiplier = p.BandMultiplier;
            UseSiegelMedian = p.UseSiegelMedian;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        UpperBand.Clear();
        LowerBand.Clear();
        Slope.Clear();
        Intercept.Clear();

        var parameters = new CoreTheilSenRegressionParameter { Period = Period, BandMultiplier = BandMultiplier, UseSiegelMedian = UseSiegelMedian };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        decimal? multiplier = IndicatorCalculationHelper.ToDecimalOrNull(BandMultiplier);
        var prices = new PriceSeriesWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(Period, UseSiegelMedian)];
        for (int i = 0; i < prices.Count; i++)
        {
            decimal? main = null;
            decimal? upper = null;
            decimal? lower = null;
            decimal? slope = null;
            decimal? intercept = null;
            int first = i - Period + 1;
            if (first >= 0 && multiplier.HasValue && prices.IsComplete(first, i)
                && RobustRegressionMath.TryFit(prices.Values.AsSpan(first, Period), UseSiegelMedian, scratch, out RobustLine line))
            {
                try
                {
                    decimal distance = multiplier.Value * line.ResidualSigma;
                    upper = line.FittedLast + distance;
                    lower = line.FittedLast - distance;
                    main = line.FittedLast;
                    slope = line.Slope;
                    intercept = line.Intercept;
                }
                catch (OverflowException)
                {
                    main = null;
                    upper = null;
                    lower = null;
                    slope = null;
                    intercept = null;
                }
            }

            _values.Add(main);
            UpperBand.Add(upper);
            LowerBand.Add(lower);
            Slope.Add(slope);
            Intercept.Add(intercept);
        }

        return CreateAutomaticResult();
    }
}
