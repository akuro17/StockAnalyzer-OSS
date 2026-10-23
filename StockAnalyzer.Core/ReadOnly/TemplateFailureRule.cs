namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Which failures of one template an all-templates listing absorbs (D-13 A): the template is left out and counted as unreadable so the others
/// are still listed. Defined once for reading a template file and for rendering a template, so the two stages cannot drift apart.
/// </summary>
public static class TemplateFailureRule
{
    /// <summary>
    /// True for a failure that belongs to one template. Cancellation is never absorbed (the caller asked to stop), and neither is an
    /// out-of-memory condition (the process is not in a state to keep listing).
    /// </summary>
    public static bool IsIsolatable(Exception error) =>
        error is not (OperationCanceledException or OutOfMemoryException);
}
