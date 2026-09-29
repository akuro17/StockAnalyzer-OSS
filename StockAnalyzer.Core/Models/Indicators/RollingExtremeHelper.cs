using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Shared rolling max/min-over-window calculation, used by Highest/Lowest and Donchian
/// so the windowing logic has a single definition (SSoT) instead of being duplicated per indicator.
/// </summary>
public static class RollingExtremeHelper
{
    public static List<decimal?> CalculateRollingMax(IReadOnlyList<decimal?> series, int period)
    {
        return CalculateRollingExtreme(series, period, isMax: true);
    }

    public static List<decimal?> CalculateRollingMin(IReadOnlyList<decimal?> series, int period)
    {
        return CalculateRollingExtreme(series, period, isMax: false);
    }

    private static List<decimal?> CalculateRollingExtreme(IReadOnlyList<decimal?> series, int period, bool isMax)
    {
        var results = new List<decimal?>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
                continue;
            }

            decimal? extreme = null;
            for (int j = i - period + 1; j <= i; j++)
            {
                if (!series[j].HasValue)
                {
                    extreme = null;
                    break;
                }

                if (!extreme.HasValue ||
                    (isMax && series[j]!.Value > extreme.Value) ||
                    (!isMax && series[j]!.Value < extreme.Value))
                {
                    extreme = series[j];
                }
            }

            results.Add(extreme);
        }

        return results;
    }
}
