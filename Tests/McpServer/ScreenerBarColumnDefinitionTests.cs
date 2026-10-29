using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Which catalog columns are read from the bars of their own time frame (<see cref="ScreenerCatalog.ReadsFromBars"/>, one definition with the frame of a side).
/// Before the catalog delegated to the evaluation core, the two answered separately; this pins the classification they agreed on for every column of the catalog.
/// </summary>
public sealed class ScreenerBarColumnDefinitionTests
{
    private static readonly string[] BarColumns = { "Change", "Change %", "Close", "High", "Low", "Open", "Volume" };

    [Fact]
    public void OnlyTheSevenBarValuesAreReadFromBarsAmongTheCatalogColumns()
    {
        IReadOnlyList<ScreenerCatalogItem> columns = new ScreenerCatalogProvider().GetCatalogItems(new IndicatorFactory())
            .Where(item => item.CategoryType == ScreenerItemCategoryType.Column).ToList();
        Assert.NotEmpty(columns);

        var barColumns = new List<string>();
        foreach (ScreenerCatalogItem column in columns)
        {
            bool expected = BarColumns.Contains(column.ShortName, StringComparer.Ordinal);
            if (expected)
            {
                barColumns.Add(column.ShortName);
            }

            foreach (int offset in new[] { 0, 1 })
            {
                ScreenerIndicatorSideConfig side = ScreenerCatalog.ColumnSide(column, offset);
                Assert.True(expected == ScreenerCatalog.ReadsFromBars(side), $"column '{column.ShortName}' (offset {offset}): expected reads-bars = {expected}");
            }
        }

        Assert.Equal(BarColumns, barColumns.OrderBy(n => n, StringComparer.Ordinal));
    }
}
