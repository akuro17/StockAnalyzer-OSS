using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Heterogeneous Autoregressive Realized Volatility (HAR-RV, Corsi 2009) forecast indicator.
/// Combines short, medium, and long rolling variance components using rolling Ridge-regularized OLS to forecast next-bar volatility.
/// Unit: percentage volatility per bar by default; annual percentage when <see cref="Annualize"/> is enabled.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HarRv)]
public class CoreHarRvIndicator : CoreIndicatorBase
{
    public const string RvShortSeriesName = nameof(RvShort);
    public const string RvMediumSeriesName = nameof(RvMedium);
    public const string RvLongSeriesName = nameof(RvLong);

    public override string Name => $"HAR-RV ({ShortPeriod}/{MediumPeriod}/{LongPeriod})";

    public override bool IsOverlay => false;

    public int EstimationWindow { get; set; } = IndicatorDefaultConstants.HarRvDefaultEstimationWindow;

    public int ShortPeriod { get; set; } = IndicatorDefaultConstants.HarRvDefaultShortPeriod;

    public int MediumPeriod { get; set; } = IndicatorDefaultConstants.HarRvDefaultMediumPeriod;

    public int LongPeriod { get; set; } = IndicatorDefaultConstants.HarRvDefaultLongPeriod;

    public HarRvBaseEstimator BaseEstimator { get; set; } = HarRvBaseEstimator.RogersSatchell;

    public bool Annualize { get; set; }

    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    [IndicatorResultIgnore]
    public List<decimal?> RvShort { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> RvMedium { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> RvLong { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHarRvParameter p)
        {
            p.Validate();
            EstimationWindow = p.EstimationWindow;
            ShortPeriod = p.ShortPeriod;
            MediumPeriod = p.MediumPeriod;
            LongPeriod = p.LongPeriod;
            BaseEstimator = p.BaseEstimator;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        RvShort.Clear();
        RvMedium.Clear();
        RvLong.Clear();

        var parameters = new CoreHarRvParameter
        {
            EstimationWindow = EstimationWindow,
            ShortPeriod = ShortPeriod,
            MediumPeriod = MediumPeriod,
            LongPeriod = LongPeriod,
            BaseEstimator = BaseEstimator,
            Annualize = Annualize,
            PeriodsPerYear = PeriodsPerYear
        };

        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        HarRvMath.Compute(
            candles,
            BaseEstimator,
            ShortPeriod,
            MediumPeriod,
            LongPeriod,
            EstimationWindow,
            Annualize,
            PeriodsPerYear,
            _values,
            RvShort,
            RvMedium,
            RvLong);

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = _values,
            [RvShortSeriesName] = RvShort,
            [RvMediumSeriesName] = RvMedium,
            [RvLongSeriesName] = RvLong
        });
    }
}
