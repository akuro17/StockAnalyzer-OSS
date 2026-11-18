using System.Buffers;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling augmented Dickey-Fuller unit-root test with a constant and <see cref="LagOrder"/> lagged differences on the last <see cref="Period"/> prices
/// (<see cref="StationarityTestMath"/>; the log price when <see cref="UseLogPrice"/> is true). <c>Main</c> = the statistic tau (dimensionless; the lower, the stronger the evidence of mean reversion).
/// <c>CriticalValue5Pct</c> = the MacKinnon (2010) 5 % critical value as a constant reference line, present exactly where <c>Main</c> has a value (tau below it rejects a unit root).
/// <c>Rho</c> (the coefficient on the lagged level) and <c>HalfLife</c> (bars, <c>-ln 2 / ln(1 + Rho)</c>, only for <c>-1 &lt; Rho &lt; 0</c>) need only a full-rank regression,
/// so they can have a value where tau has none; both are chart-suppressed (scale), see <see cref="IndicatorChartSuppression"/>. No p-value is produced.
/// Null for bars before bar <c>Period - 1</c>, for a window with an unusable price (log price: a price that is not positive), for a window shorter than
/// <c>2 x LagOrder + 5</c> and for a rank-deficient (flat) window. Causal: bar t uses bars &lt;= t only.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.ADFStationarity)]
public class CoreAdfStationarityIndicator : CoreIndicatorBase
{
    public const string CriticalValue5PctSeriesName = nameof(CriticalValue5Pct);
    public const string HalfLifeSeriesName = nameof(HalfLife);
    public const string RhoSeriesName = nameof(Rho);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.AdfStationarityDefaultPeriod;

    public int LagOrder { get; set; } = IndicatorDefaultConstants.AdfStationarityDefaultLagOrder;

    public bool UseLogPrice { get; set; } = IndicatorDefaultConstants.AdfStationarityDefaultUseLogPrice;

    public List<decimal?> CriticalValue5Pct { get; } = new();

    public List<decimal?> HalfLife { get; } = new();

    public List<decimal?> Rho { get; } = new();

    public override string Name => UseLogPrice ? $"ADF ({Period}, {LagOrder})" : $"ADF ({Period}, {LagOrder}, price)";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdfStationarityParameter p)
        {
            p.Validate();
            Period = p.Period;
            LagOrder = p.LagOrder;
            UseLogPrice = p.UseLogPrice;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        CriticalValue5Pct.Clear();
        HalfLife.Clear();
        Rho.Clear();

        var parameters = new CoreAdfStationarityParameter { Period = Period, LagOrder = LagOrder, UseLogPrice = UseLogPrice };
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
        double[] levels = new double[count];
        var invalidLevels = new InvalidValueIndex(count);
        for (int i = 0; i < count; i++)
        {
            decimal? price = prices[i];
            double level = double.NaN;
            if (price.HasValue && (!UseLogPrice || price.Value > 0m))
            {
                level = UseLogPrice ? Math.Log((double)price.Value) : (double)price.Value;
            }

            levels[i] = level;
            invalidLevels.Add(!double.IsFinite(level));
        }

        // Window shorter than 2 x LagOrder + 5: every bar is null (no regression can have two residual degrees of freedom).
        bool windowLongEnough = Period >= StationarityTestMath.MinWindowLength(LagOrder);
        int scratchLength = windowLongEnough ? StationarityTestMath.ScratchLength(Period, LagOrder) : 0;
        double[]? scratch = scratchLength > 0 ? ArrayPool<double>.Shared.Rent(scratchLength) : null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                decimal? tau = null;
                decimal? critical = null;
                decimal? halfLife = null;
                decimal? rho = null;
                int first = i - Period + 1;
                if (scratch != null && first >= 0 && invalidLevels.IsClean(first, i)
                    && StationarityTestMath.TryAugmentedDickeyFuller(
                        levels.AsSpan(first, Period), LagOrder, IndicatorDefaultConstants.HalfLifeLogEpsilon, scratch, out AugmentedDickeyFullerResult result))
                {
                    tau = IndicatorCalculationHelper.ToDecimalOrNull(result.Tau);
                    critical = tau.HasValue ? IndicatorCalculationHelper.ToDecimalOrNull(result.CriticalValue5Percent) : null;
                    rho = IndicatorCalculationHelper.ToDecimalOrNull(result.Rho);
                    halfLife = IndicatorCalculationHelper.ToDecimalOrNull(result.HalfLife);
                }

                _values.Add(tau);
                CriticalValue5Pct.Add(critical);
                HalfLife.Add(halfLife);
                Rho.Add(rho);
            }
        }
        finally
        {
            if (scratch != null)
            {
                ArrayPool<double>.Shared.Return(scratch);
            }
        }

        return CreateAutomaticResult();
    }
}
