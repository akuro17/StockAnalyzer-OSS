using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Owner decision D-02a (plan 02): with an as-of date a Column side must be one that is read from the bars (<see cref="ScreenerCatalog.ReadsFromBars"/>). Every other
/// column (market cap, P/E, dividend yield, sector, ...) is the stored snapshot of today: evaluated "as of" a past date it would use information that did not
/// exist then, which is the look-ahead the as-of date exists to remove. Such a side is refused instead of evaluated or warned about.
/// </summary>
internal sealed class ScreenerAsOfColumnGuard
{
    private readonly ScreenerCatalog _catalog;

    public ScreenerAsOfColumnGuard(ScreenerCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Names of the columns that stay available with an as-of date, in catalogue order.</summary>
    public IReadOnlyList<string> AllowedColumns => _catalog.Columns
        .Where(column => _catalog.FindColumn(column.Name) is { } item && ScreenerCatalog.ReadsFromBars(ScreenerCatalog.ColumnSide(item, 0)))
        .Select(column => column.Name)
        .ToList();

    /// <summary>
    /// Refuses the first comparison side that reads a snapshot column. <paramref name="sidePaths"/> maps a leaf to the argument paths of its left and right side
    /// (a condition tree names them <c>left</c>/<c>right</c> and appends <c>.column</c>, a saved template names them <c>leftHand</c>/<c>rightHand</c>).
    /// </summary>
    public void Require(IReadOnlyList<ScreenerLeaf> leaves, Func<ScreenerLeaf, (string Left, string Right)> sidePaths, string asOfArgument)
    {
        foreach (ScreenerLeaf leaf in leaves)
        {
            (string left, string right) = sidePaths(leaf);
            Require(leaf.Entry.LeftHand, left, asOfArgument);
            if (leaf.Entry.TargetMode == RightHandTargetMode.Indicator)
            {
                Require(leaf.Entry.RightHand, right, asOfArgument);
            }
        }
    }

    private void Require(ScreenerIndicatorSideConfig? side, string path, string asOfArgument)
    {
        if (side is not { CategoryType: ScreenerItemCategoryType.Column } || ScreenerCatalog.ReadsFromBars(side))
        {
            return;
        }

        string name = side.OutputName ?? side.CustomDisplayName ?? side.DisplayName ?? "(unnamed)";
        throw new ToolException(
            ToolErrorCodes.InvalidArgument,
            $"Column '{name}' is the stored snapshot of today, not its value on a past date, so it cannot be combined with {asOfArgument}.",
            path,
            $"With {asOfArgument} only these columns can be used (they are read from the bars): {string.Join(", ", AllowedColumns)}. Use an indicator or price side for other conditions, or omit {asOfArgument}.");
    }
}
