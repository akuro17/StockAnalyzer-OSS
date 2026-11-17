using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Hurst exponent by R/S analysis (see <see cref="HurstMath"/>): the slope of ln (R/S) against ln m over the sub-interval lengths m = n, n/2, n/4, ... (&gt;= 8) of the window
/// of the last <see cref="Period"/> log returns r_k = ln(P_k / P_(k-1)); no small-sample (Anis-Lloyd) correction.
/// <c>Main</c> = H, clamped to [0, 1]. Unit: ratio (dimensionless, 0 to 1). Null for bars before bar <see cref="Period"/> and when the window has an unusable price or fewer than two usable
/// sub-interval lengths. Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// Also computes companion series <c>ModifiedHurst</c> (Lo 1991), <c>VarianceRatio</c>, <c>VarianceRatioZScore</c> (Lo-MacKinlay 1988), and <c>DfaAlpha</c> (DFA-1).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HurstExponent)]
public class CoreHurstExponentIndicator : CoreIndicatorBase
{
    public const string ModifiedHurstSeriesName = nameof(ModifiedHurst);
    public const string VarianceRatioSeriesName = nameof(VarianceRatio);
    public const string VarianceRatioZScoreSeriesName = nameof(VarianceRatioZScore);
    public const string DfaAlphaSeriesName = nameof(DfaAlpha);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.HurstDefaultPeriod;

    public int NeweyWestLag { get; set; } = IndicatorDefaultConstants.HurstDefaultNeweyWestLag;

    public int VarianceRatioLag { get; set; } = IndicatorDefaultConstants.HurstDefaultVarianceRatioLag;

    [IndicatorResultIgnore]
    public List<decimal?> ModifiedHurst { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> VarianceRatio { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> VarianceRatioZScore { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> DfaAlpha { get; } = new();

    public override string Name =>
        NeweyWestLag == IndicatorDefaultConstants.HurstDefaultNeweyWestLag &&
        VarianceRatioLag == IndicatorDefaultConstants.HurstDefaultVarianceRatioLag
            ? $"Hurst Exponent ({Period})"
            : $"Hurst Exponent ({Period}, NW:{NeweyWestLag}, VR:{VarianceRatioLag})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHurstExponentParameter p)
        {
            p.Validate();
            Period = p.Period;
            NeweyWestLag = p.NeweyWestLag;
            VarianceRatioLag = p.VarianceRatioLag;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        ModifiedHurst.Clear();
        VarianceRatio.Clear();
        VarianceRatioZScore.Clear();
        DfaAlpha.Clear();

        var parameters = new CoreHurstExponentParameter
        {
            Period = Period,
            NeweyWestLag = NeweyWestLag,
            VarianceRatioLag = VarianceRatioLag
        };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = new LogReturnWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        for (int i = 0; i < returns.Count; i++)
        {
            if (i < Period || !returns.IsValid(i - Period + 1, i))
            {
                _values.Add(null);
                ModifiedHurst.Add(null);
                VarianceRatio.Add(null);
                VarianceRatioZScore.Add(null);
                DfaAlpha.Add(null);
                continue;
            }

            ReadOnlySpan<double> window = returns.Returns.AsSpan(i - Period + 1, Period);
            double hurst = HurstMath.Exponent(window);
            double modifiedHurst = HurstMath.ModifiedRescaledRangeExponent(window, NeweyWestLag);
            double vr = HurstMath.VarianceRatio(window, VarianceRatioLag, out double zStar);
            double dfa = HurstMath.DetrendedFluctuationExponent(window);

            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(hurst));
            ModifiedHurst.Add(IndicatorCalculationHelper.ToDecimalOrNull(modifiedHurst));
            VarianceRatio.Add(IndicatorCalculationHelper.ToDecimalOrNull(vr));
            VarianceRatioZScore.Add(IndicatorCalculationHelper.ToDecimalOrNull(zStar));
            DfaAlpha.Add(IndicatorCalculationHelper.ToDecimalOrNull(dfa));
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { ModifiedHurstSeriesName, ModifiedHurst },
            { VarianceRatioSeriesName, VarianceRatio },
            { VarianceRatioZScoreSeriesName, VarianceRatioZScore },
            { DfaAlphaSeriesName, DfaAlpha }
        });
    }
}
