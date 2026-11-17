namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The aggregation period of the classic pivot points: the levels come from the preceding day, week or month.
/// Single definition shared by the GUI drawing object and the headless value calculator. Values are persisted by name: never rename or renumber.
/// </summary>
public enum PivotPeriod
{
    Day,
    Week,
    Month
}
