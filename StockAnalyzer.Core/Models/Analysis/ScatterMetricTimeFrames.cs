using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// The time frames a scatter metric can be registered on. The bars of a Week or Month metric are the ones the data service reads for that time frame
/// (the Weekly and Monthly Parquet files), so nothing here converts a count of bars between time frames.
/// </summary>
public static class ScatterMetricTimeFrames
{
    /// <summary>The supported time frames in display order: Day, Week, Month.</summary>
    public static IReadOnlyList<TimeFrame> Supported { get; } = Array.AsReadOnly(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 });

    /// <summary>True when <paramref name="timeFrame"/> is one of <see cref="Supported"/>.</summary>
    public static bool IsSupported(TimeFrame timeFrame)
    {
        foreach (TimeFrame supported in Supported)
        {
            if (supported == timeFrame)
            {
                return true;
            }
        }

        return false;
    }
}
