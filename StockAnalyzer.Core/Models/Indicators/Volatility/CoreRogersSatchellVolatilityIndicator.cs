using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Rogers-Satchell (1991) volatility, which allows a non-zero drift. Per-bar contribution v_k = ln(H/C) x ln(H/O) + ln(L/C) x ln(L/O);
/// a bar with O, H, L or C &lt;= 0, or a non-finite v_k, is invalid (a negative v_k from inconsistent prices is a valid input).
/// <c>Main</c> = sqrt(max(0, mean of v_k over the last <see cref="Period"/> bars)) x 100 (x sqrt(<see cref="PeriodsPerYear"/>) when <see cref="Annualize"/> is on).
/// Unit: percentage per bar by default, so the meaning does not change with the chart time frame; annual percentage when Annualize is on
/// (use 252 on daily, 52 on weekly, 12 on monthly bars). Null while fewer than Period bars exist and when any bar of the window is invalid.
/// Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RogersSatchellVolatility)]
public class CoreRogersSatchellVolatilityIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.RangeVolatilityDefaultPeriod;

    /// <summary>When true the value is multiplied by sqrt(<see cref="PeriodsPerYear"/>).</summary>
    public bool Annualize { get; set; }

    /// <summary>Bars per year for the annualisation; used only when <see cref="Annualize"/> is true.</summary>
    public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

    public override string Name => $"Rogers-Satchell Volatility ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRogersSatchellVolatilityParameter p)
        {
            p.Validate();
            Period = p.Period;
            Annualize = p.Annualize;
            PeriodsPerYear = p.PeriodsPerYear;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        int count = candles.Count;
        var contribution = new double[count];
        var valid = new bool[count];
        for (int k = 0; k < count; k++)
        {
            CoreCandleData candle = candles[k];
            if (candle.Open <= 0m || candle.High <= 0m || candle.Low <= 0m || candle.Close <= 0m)
            {
                continue;
            }

            double open = (double)candle.Open;
            double high = (double)candle.High;
            double low = (double)candle.Low;
            double close = (double)candle.Close;

            double v = Math.Log(high / close) * Math.Log(high / open) + Math.Log(low / close) * Math.Log(low / open);
            if (double.IsFinite(v))
            {
                contribution[k] = v;
                valid[k] = true;
            }
        }

        _values.AddRange(RangeVolatilityMath.Rolling(contribution, valid, Period, Annualize, PeriodsPerYear));
        return IndicatorResult.Success(_values);
    }
}
