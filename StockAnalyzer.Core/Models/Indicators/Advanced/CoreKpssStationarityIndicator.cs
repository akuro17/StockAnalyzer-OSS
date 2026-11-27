using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling KPSS level-stationarity test of the last <see cref="Period"/> prices (or, with <see cref="UseReturns"/>, of the last <see cref="Period"/> log returns): the statistic
/// <c>eta = sum(S_t^2) / (n^2 x sigma_LR^2)</c> of <see cref="ArimaMath.ComputeKpssStatistic"/> (partial sums of the deviations from the mean, Bartlett / Newey-West long-run variance,
/// lag <c>floor(4 (n / 100)^0.25)</c>). <c>Main</c> is dimensionless and not negative: the HIGHER the value, the stronger the evidence against stationarity (the opposite reading of the ADF
/// statistic). <c>CriticalValue5Pct</c> (0.463) and <c>CriticalValue1Pct</c> (0.739) are constant reference lines, present exactly where <c>Main</c> has a value.
/// The kernel answers 0.0 (not NaN) for a window it cannot test, so the window is guarded here: complete (no unusable price / return), at least 8 observations (the parameter range),
/// and not flat (<see cref="WindowMoments"/>: the series rule for prices, the returns rule for returns); otherwise <c>Main</c> is null.
/// Null for bars before bar <c>Period - 1</c> (prices) or <c>Period</c> (returns). Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.KpssStationarity)]
public class CoreKpssStationarityIndicator : CoreIndicatorBase
{
    public const string CriticalValue5PctSeriesName = nameof(CriticalValue5Pct);
    public const string CriticalValue1PctSeriesName = nameof(CriticalValue1Pct);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.KpssStationarityDefaultPeriod;

    public bool UseReturns { get; set; } = IndicatorDefaultConstants.KpssStationarityDefaultUseReturns;

    public List<decimal?> CriticalValue5Pct { get; } = new();

    public List<decimal?> CriticalValue1Pct { get; } = new();

    public override string Name => UseReturns ? $"KPSS ({Period}, returns)" : $"KPSS ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKpssStationarityParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseReturns = p.UseReturns;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        CriticalValue5Pct.Clear();
        CriticalValue1Pct.Clear();

        var parameters = new CoreKpssStationarityParameter { Period = Period, UseReturns = UseReturns };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        IReadOnlyList<decimal?> prices = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        int count = prices.Count;
        double[] observations;
        Func<int, int, bool> isUsable;
        FlatWindowRule flatRule;
        int firstBar;
        if (UseReturns)
        {
            var returns = ReturnSeriesWindow.FromPrices(prices, useLogReturn: true);
            observations = returns.Returns;
            isUsable = returns.IsValid;
            flatRule = FlatWindowRule.Returns;
            firstBar = Period; // return 0 does not exist, so the first window of Period returns ends at bar Period
        }
        else
        {
            var window = new PriceSeriesWindow(prices);
            observations = new double[count];
            for (int i = 0; i < count; i++)
            {
                observations[i] = (double)window.Values[i];
            }

            isUsable = window.IsComplete;
            flatRule = FlatWindowRule.Series;
            firstBar = Period - 1;
        }

        for (int i = 0; i < count; i++)
        {
            decimal? statistic = null;
            if (i >= firstBar)
            {
                int first = i - Period + 1;
                ReadOnlySpan<double> span = observations.AsSpan(first, Period);
                if (isUsable(first, i) && WindowMoments.TryCompute(span, flatRule, out WindowMoments moments) && !moments.IsFlat)
                {
                    statistic = IndicatorCalculationHelper.ToDecimalOrNull(ArimaMath.ComputeKpssStatistic(span));
                }
            }

            _values.Add(statistic);
            CriticalValue5Pct.Add(statistic.HasValue ? IndicatorDefaultConstants.KpssCriticalValue5Percent : null);
            CriticalValue1Pct.Add(statistic.HasValue ? IndicatorDefaultConstants.KpssCriticalValue1Percent : null);
        }

        return CreateAutomaticResult();
    }
}
