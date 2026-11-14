using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>The Extended Line, Cross Line and Disjoint Channel tools are on the toolbar with localized names.</summary>
// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class ExtendedLineCrossLineToolbarTests
{
    [Theory]
    [InlineData("en", DrawingTool.ExtendedLine, "Extended Line")]
    [InlineData("en", DrawingTool.CrossLine, "Cross Line")]
    [InlineData("ja", DrawingTool.ExtendedLine, "延長線")]
    [InlineData("ja", DrawingTool.CrossLine, "十字線")]
    [InlineData("en", DrawingTool.DisjointChannel, "Disjoint Channel")]
    [InlineData("ja", DrawingTool.DisjointChannel, "独立辺チャネル")]
    public void ToolbarCategory_ListsTheNewTool_WithALocalizedName(string languageCode, DrawingTool tool, string expectedName)
    {
        LocalizationManager.Instance.Initialize(languageCode);

        // Extended Line and Cross Line are in Lines; Disjoint Channel is in Channels.
        string categoryKey = tool == DrawingTool.DisjointChannel ? "DrawCat_Channels" : "DrawCat_Lines";
        var category = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == categoryKey);
        var info = Assert.Single(category.Tools, t => t.Tool == tool);

        Assert.Equal(expectedName, LocalizationManager.Instance[info.NameKey]);
    }
}
