using System;
using System.Runtime.InteropServices;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Immutable, blittable struct representing a single 2D scatter plot observation.
/// Memory layout is aligned to 32 bytes without reference type overhead.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly record struct ScatterPoint(
    double X,
    double Y,
    long TimestampUtcTicks = 0, // 0 if timeless; otherwise DateTime.Ticks (UTC)
    uint ColorArgb = 0,         // 0 for default theme color
    float Radius = 0.0f         // 0.0f for default theme radius (typically 3.0f - 4.0f px)
)
{
    public bool HasTimestamp => TimestampUtcTicks > 0;
    public DateTime? TimestampUtc => HasTimestamp ? new DateTime(TimestampUtcTicks, DateTimeKind.Utc) : null;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public ScatterPoint(double x, double y, DateTime timestampUtc, uint colorArgb = 0, float radius = 0.0f)
        : this(x, y, timestampUtc.ToUniversalTime().Ticks, colorArgb, radius)
    {
    }
}
