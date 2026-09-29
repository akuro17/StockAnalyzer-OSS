namespace StockAnalyzer.Avalonia.Common;

public class ScreenerSettings
{
    public string[] DefaultSymbols { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Deepest AND/OR nesting the Filters condition tree may have (root group = 1). Safe extension
    /// (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md Task 2): defaulted so existing
    /// configuration files without this key keep binding unchanged. Same rationale/default as the Backtest
    /// tree's <see cref="StockAnalyzer.Core.Models.Settings.BacktestSettings.MaxConditionTreeDepth"/>.
    /// </summary>
    public int MaxFilterTreeDepth { get; set; } = 8;

    /// <summary>Most nodes (leaves and groups, root included) the Filters condition tree may contain.</summary>
    public int MaxFilterTreeNodes { get; set; } = 64;

    /// <summary>Longest user-entered Filters group name, in characters after trimming.</summary>
    public int MaxFilterGroupNameLength { get; set; } = 32;

    /// <summary>
    /// Distance (device-independent pixels, along either axis) a pressed pointer must travel over a Filters condition
    /// row before the press becomes a drag that moves the condition (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md
    /// Task 1). Same rationale and default as Backtest's <see cref="StockAnalyzer.Core.Models.Settings.BacktestSettings.ConditionDragStartDistance"/>,
    /// kept as its own key rather than reused so each screen's drag sensitivity stays independently tunable.
    /// </summary>
    public int ConditionDragStartDistance { get; set; } = 4;

    public void Validate()
    {
        if (DefaultSymbols == null) throw new System.InvalidOperationException("ScreenerSettings: DefaultSymbols cannot be null.");
        if (MaxFilterTreeDepth < 1) throw new System.InvalidOperationException("ScreenerSettings: MaxFilterTreeDepth must be >= 1.");
        if (MaxFilterTreeNodes < 1) throw new System.InvalidOperationException("ScreenerSettings: MaxFilterTreeNodes must be >= 1.");
        if (MaxFilterGroupNameLength < 1) throw new System.InvalidOperationException("ScreenerSettings: MaxFilterGroupNameLength must be >= 1.");
        if (ConditionDragStartDistance < 1) throw new System.InvalidOperationException("ScreenerSettings: ConditionDragStartDistance must be >= 1.");
    }
}
