using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Provides extension methods for TimeframeType to simplify conversion to core models and time spans.
/// </summary>
public static class TimeframeTypeExtensions
{
    /// <summary>
    /// Converts TimeframeType to the core TimeFrame enum.
    /// </summary>
    public static TimeFrame ToCoreTimeFrame(this TimeframeType type)
    {
        return type switch
        {
            TimeframeType.M1 => TimeFrame.M1,
            TimeframeType.M5 => TimeFrame.M5,
            TimeframeType.M15 => TimeFrame.M15,
            TimeframeType.H1 => TimeFrame.H1,
            TimeframeType.H4 => TimeFrame.H4,
            TimeframeType.Daily => TimeFrame.D1,
            TimeframeType.Weekly => TimeFrame.W1,
            TimeframeType.Monthly => TimeFrame.MN1,
            _ => TimeFrame.D1
        };
    }

    /// <summary>
    /// Returns the approximate TimeSpan for a given TimeframeType.
    /// </summary>
    public static TimeSpan ToTimeSpan(this TimeframeType type)
    {
        return type switch
        {
            TimeframeType.M1 => TimeSpan.FromMinutes(1),
            TimeframeType.M5 => TimeSpan.FromMinutes(5),
            TimeframeType.M15 => TimeSpan.FromMinutes(15),
            TimeframeType.H1 => TimeSpan.FromHours(1),
            TimeframeType.H4 => TimeSpan.FromHours(4),
            TimeframeType.Daily => TimeSpan.FromDays(1),
            TimeframeType.Weekly => TimeSpan.FromDays(7),
            TimeframeType.Monthly => TimeSpan.FromDays(30), // Approximation
            _ => TimeSpan.FromDays(1)
        };
    }
}
