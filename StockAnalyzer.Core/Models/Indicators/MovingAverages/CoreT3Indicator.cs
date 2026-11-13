using System.Globalization;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

/// <summary>
/// T3 moving average (Tim Tillson, 1998). With v = <see cref="VolumeFactor"/> and e1..e6 six cascaded EMAs of the price source (each EMA of the previous one, same <see cref="Period"/>):
/// c1 = -v^3, c2 = 3v^2 + 3v^3, c3 = -6v^2 - 3v - 3v^3, c4 = 1 + 3v + v^3 + 3v^2 and <c>Main</c> = c1 x e6 + c2 x e5 + c3 x e4 + c4 x e3.
/// The four coefficients always sum to 1. Unit: price-native. Causal: bar t uses bars &lt;= t only.
/// A bar where e6 is undefined is null; for a series without gaps the first value is at bar index 6 x (Period - 1). Output length equals the input length.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.T3)]
public class CoreT3Indicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.T3DefaultPeriod;
    public decimal VolumeFactor { get; set; } = IndicatorDefaultConstants.T3DefaultVolumeFactor;

    public override string Name => string.Create(CultureInfo.InvariantCulture, $"T3 ({Period}, {VolumeFactor})");

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreT3Parameter p)
        {
            p.Validate();
            Period = p.Period;
            VolumeFactor = p.VolumeFactor;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        var e1 = IndicatorCalculationHelper.CalculateEmaWithNulls(series, Period);
        var e2 = IndicatorCalculationHelper.CalculateEmaWithNulls(e1, Period);
        var e3 = IndicatorCalculationHelper.CalculateEmaWithNulls(e2, Period);
        var e4 = IndicatorCalculationHelper.CalculateEmaWithNulls(e3, Period);
        var e5 = IndicatorCalculationHelper.CalculateEmaWithNulls(e4, Period);
        var e6 = IndicatorCalculationHelper.CalculateEmaWithNulls(e5, Period);

        decimal v = VolumeFactor;
        decimal v2 = v * v;
        decimal v3 = v2 * v;
        decimal c1 = -v3;
        decimal c2 = 3m * v2 + 3m * v3;
        decimal c3 = -6m * v2 - 3m * v - 3m * v3;
        decimal c4 = 1m + 3m * v + v3 + 3m * v2;

        int count = series.Count;
        _values.Clear();
        _values.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            if (e6[i] is null)
            {
                _values.Add(null);
                continue;
            }

            // e6 defined implies e5, e4 and e3 are defined at the same bar (each EMA starts no later than the next one).
            _values.Add(c1 * e6[i]!.Value + c2 * e5[i]!.Value + c3 * e4[i]!.Value + c4 * e3[i]!.Value);
        }

        return IndicatorResult.Success(_values);
    }
}
