using System.Reflection;

namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// The indicator types that count as a moving average for the colour assignment of an AI chart image, with the one parameter that gives each its period.
/// There is no such grouping elsewhere in the application (<c>CoreIndicatorCategory</c> has no moving-average category, VWMA is filed under Volume), and a
/// name-based guess would pick a convention silently, so the set is written out here once (owner decision D-15k=B; the parameters were read from the real
/// catalogue in T0 of plan 15). A type without a period (MESA) is ranked after every type with one.
/// Not in the set on purpose: GMA (a bundle of lines), AMA (obsolete duplicate of KAMA), VolumeMA (volume scale), MovingAverageCross (signal), MaDeviationRate, MACD, ARIMA.
/// </summary>
public static class AiChartMovingAverageSet
{
    private const string PeriodParameter = "Period";

    private static readonly IReadOnlyDictionary<IndicatorType, string?> Table = new Dictionary<IndicatorType, string?>
    {
        [IndicatorType.SMA] = PeriodParameter,
        [IndicatorType.EMA] = PeriodParameter,
        [IndicatorType.WMA] = PeriodParameter,
        [IndicatorType.HMA] = PeriodParameter,
        [IndicatorType.TMA] = PeriodParameter,
        [IndicatorType.DEMA] = PeriodParameter,
        [IndicatorType.TEMA] = PeriodParameter,
        [IndicatorType.VWMA] = PeriodParameter,
        [IndicatorType.VAMA] = PeriodParameter,
        [IndicatorType.SMMA] = PeriodParameter,
        [IndicatorType.LRMA] = PeriodParameter,
        [IndicatorType.ALMA] = PeriodParameter,
        [IndicatorType.ZLEMA] = PeriodParameter,
        [IndicatorType.LSMA] = PeriodParameter,
        [IndicatorType.Dma] = PeriodParameter,
        [IndicatorType.KAMA] = PeriodParameter,
        [IndicatorType.JMA] = PeriodParameter,
        [IndicatorType.FRAMA] = PeriodParameter,
        [IndicatorType.BSMA] = PeriodParameter,
        [IndicatorType.ClothoidMovingAverage] = PeriodParameter,
        [IndicatorType.MtfLaggedEma] = PeriodParameter,
        [IndicatorType.VIDYA] = "SmoothPeriod",
        [IndicatorType.MESA] = null,
    };

    /// <summary>Every type of the set, in the table's order.</summary>
    public static IEnumerable<IndicatorType> Types => Table.Keys;

    public static bool Contains(IndicatorType type) => Table.ContainsKey(type);

    /// <summary>The name of the parameter that gives the period of a type of the set; null when the type has none (or is not in the set).</summary>
    public static string? RepresentativeParameter(IndicatorType type) => Table.TryGetValue(type, out string? name) ? name : null;

    /// <summary>The period in bars of an indicator of the set read from its parameter object; null when the type has no period parameter or the object does not hold it.</summary>
    public static int? RepresentativePeriod(IndicatorType type, object? parameterObject)
    {
        string? name = RepresentativeParameter(type);
        if (name is null || parameterObject is null)
        {
            return null;
        }

        PropertyInfo? property = parameterObject.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        object? value = property?.GetValue(parameterObject);
        return value switch
        {
            int period => period,
            long period => checked((int)period),
            _ => null,
        };
    }
}
