using System;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Drawing interaction resource bounds bound from the "DrawingInteraction" section of appsettings.json (via <c>IStockAnalyzerSettings</c>).
/// The defaults are the values of <see cref="DrawingInteractionLimits"/>, which stays the single place that defines them; the interface default,
/// the configuration files and any direct construction all follow it.
/// </summary>
public class DrawingInteractionSettings
{
    /// <summary>Maximum undo entries kept per drawing context.</summary>
    public int MaxHistoryEntriesPerContext { get; set; } = DrawingInteractionLimits.MaxHistoryEntriesPerContext;

    /// <summary>Maximum undo payload bytes kept per drawing context (sum of UTF-8 Before/After states).</summary>
    public long MaxHistoryPayloadBytesPerContext { get; set; } = DrawingInteractionLimits.MaxHistoryPayloadBytesPerContext;

    /// <summary>Maximum undo payload bytes kept across every drawing context of the session.</summary>
    public long MaxSessionHistoryPayloadBytes { get; set; } = DrawingInteractionLimits.MaxSessionHistoryPayloadBytes;

    /// <summary>Maximum raw points captured by one freehand stroke (also the pre-allocated stroke buffer size).</summary>
    public int MaxStrokeSamples { get; set; } = DrawingInteractionLimits.MaxStrokeSamples;

    public void Validate()
    {
        RequirePositive(MaxHistoryEntriesPerContext, nameof(MaxHistoryEntriesPerContext));
        RequirePositive(MaxHistoryPayloadBytesPerContext, nameof(MaxHistoryPayloadBytesPerContext));
        RequirePositive(MaxSessionHistoryPayloadBytes, nameof(MaxSessionHistoryPayloadBytes));
        RequirePositive(MaxStrokeSamples, nameof(MaxStrokeSamples));
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"DrawingInteractionSettings: {name} must be > 0.");
        }
    }
}
