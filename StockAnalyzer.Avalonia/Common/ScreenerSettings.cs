namespace StockAnalyzer.Avalonia.Common;

public class ScreenerSettings
{
    public string[] DefaultSymbols { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Deepest AND/OR nesting the Filters condition tree may have (root group = 1). Safe extension
    /// (ScreenerFilterConditionTree Task 2): defaulted so existing
    /// configuration files without this key keep binding unchanged. Same rationale/default as the Backtest
    /// tree's <see cref="StockAnalyzer.Core.Models.Settings.BacktestSettings.MaxConditionTreeDepth"/>.
    /// </summary>
    public int MaxFilterTreeDepth { get; set; } = StockAnalyzer.Core.Models.Settings.ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth;

    /// <summary>
    /// Most nodes (leaves and groups, root included) the Filters condition tree may contain. This value and <see cref="MaxFilterTreeDepth"/> are
    /// compared with the code contract <see cref="StockAnalyzer.Core.Models.Settings.ScreenerFilterTreeLimits"/> at startup.
    /// </summary>
    public int MaxFilterTreeNodes { get; set; } = StockAnalyzer.Core.Models.Settings.ScreenerFilterTreeLimits.DefaultMaxFilterTreeNodes;

    /// <summary>Longest user-entered Filters group name, in characters after trimming.</summary>
    public int MaxFilterGroupNameLength { get; set; } = 32;

    /// <summary>
    /// Distance (device-independent pixels, along either axis) a pressed pointer must travel over a Filters condition
    /// row before the press becomes a drag that moves the condition (ScreenerFilterConditionDragMove
    /// Task 1). Same rationale and default as Backtest's <see cref="StockAnalyzer.Core.Models.Settings.BacktestSettings.ConditionDragStartDistance"/>,
    /// kept as its own key rather than reused so each screen's drag sensitivity stays independently tunable.
    /// </summary>
    public int ConditionDragStartDistance { get; set; } = 4;

    /// <summary>
    /// Largest Offset (bars before the evaluated bar) a Screener condition may use: the upper bound of the registration dialog's Offset input boxes
    /// and of the Offset a loaded template may carry. Its own key rather than Backtest's, so each screen stays independently tunable; the shipped
    /// value is the former fixed input limit. The accepted range is the code contract <see cref="StockAnalyzer.Core.Models.Settings.ScreenerConditionOffsetLimits"/>.
    /// </summary>
    public int MaxConditionOffset { get; set; } = StockAnalyzer.Core.Models.Settings.ScreenerConditionOffsetLimits.DefaultMaxConditionOffset;

    public void Validate()
    {
        if (DefaultSymbols == null) throw new System.InvalidOperationException("ScreenerSettings: DefaultSymbols cannot be null.");
        if (MaxFilterGroupNameLength < 1) throw new System.InvalidOperationException("ScreenerSettings: MaxFilterGroupNameLength must be >= 1.");
        if (ConditionDragStartDistance < 1) throw new System.InvalidOperationException("ScreenerSettings: ConditionDragStartDistance must be >= 1.");
    }
}
