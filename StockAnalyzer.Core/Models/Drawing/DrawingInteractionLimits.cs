namespace StockAnalyzer.Core.Models.Drawing;

/// <summary>
/// Domain resource boundaries for drawing interactions, memory limits, and undo/redo history (G3 specification).
/// Provides the single source of truth (SSoT) for the DEFAULT buffer allocations, capacity thresholds, and payload limits.
/// The effective values are configurable: see <c>DrawingInteractionSettings</c> ("DrawingInteraction" section of appsettings.json),
/// which defaults to these constants. Production code must consume the configured values; these constants are the fallback
/// for callers that have no settings source (direct construction, tests).
/// </summary>
public static class DrawingInteractionLimits
{
    /// <summary>
    /// Maximum history entries preserved per drawing context (128 entries).
    /// </summary>
    public const int MaxHistoryEntriesPerContext = 128;

    /// <summary>
    /// Maximum history payload bytes preserved per drawing context (64 MiB = 67,108,864 bytes).
    /// Measured as the sum of standard UTF-8 serialized representations of Before and After states.
    /// </summary>
    public const long MaxHistoryPayloadBytesPerContext = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum history payload bytes preserved across all drawing contexts in a session (256 MiB = 268,435,456 bytes).
    /// </summary>
    public const long MaxSessionHistoryPayloadBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Maximum raw stroke sampling points captured during a single freehand input interaction (65,536 points).
    /// </summary>
    public const int MaxStrokeSamples = 65536;

    /// <summary>
    /// Maximum coordinates points allowed per single mutating operation (65,536 points).
    /// </summary>
    public const int MaxPointsPerOperation = 65536;
}
