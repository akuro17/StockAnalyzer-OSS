namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Immutable point sample delivered from pointer devices for freehand stroke capturing.
/// </summary>
public readonly record struct FreehandInputSample(long PointerId, double X, double Y);

/// <summary>
/// Result status returned by drawing input controllers.
/// </summary>
public enum DrawingInputResult
{
    Ignored,
    Started,
    Updated,
    Committed,
    Cancelled,
    Rejected
}

/// <summary>
/// Reason why an in-progress drawing interaction was cancelled.
/// </summary>
public enum DrawingCancelReason
{
    CaptureLost,
    ContextChanged,
    TransformRevisionChanged,
    CapacityExceeded,
    TransformInvalid
}
