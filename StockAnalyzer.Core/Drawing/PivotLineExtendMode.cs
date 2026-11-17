namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// How far the horizontal classic pivot lines are drawn beyond their period.
/// Single definition shared by the GUI drawing object and the headless value calculator. Values are persisted by name: never rename or renumber.
/// </summary>
public enum PivotLineExtendMode
{
    None = 0,
    ExtendRight = 1,
    ExtendLeft = 2,
    ExtendBoth = 3
}
