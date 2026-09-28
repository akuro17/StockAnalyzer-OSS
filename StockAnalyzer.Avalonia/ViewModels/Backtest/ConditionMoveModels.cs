namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>Where a dragged condition lands relative to the row it is dropped on: just before it, just after it (both as its sibling), or at the end of that group.</summary>
public enum ConditionDropPlacement
{
    Before,
    After,
    Inside,
}

/// <summary>Outcome of moving a condition: it moved, it already is at that place (nothing changes, no edit is counted), or the move is not allowed (nothing changes).</summary>
public enum ConditionMoveResult
{
    Moved,
    Unchanged,
    Rejected,
}
