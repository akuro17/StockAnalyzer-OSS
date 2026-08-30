using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class ParameterGroupingTests
{
    public ParameterGroupingTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    [AvaloniaFact]
    public void Build_ParameterWithoutCategories_DoesNotRenderCategoryHeaders()
    {
        var builder = new ParameterViewBuilder();
        var param = new CoreSmaParameter { Period = 20 };

        var control = builder.Build(param);
        var stackPanel = Assert.IsType<StackPanel>(control);

        // Every child should be a property row (Border containing Grid with 2 columns)
        Assert.NotEmpty(stackPanel.Children);
        foreach (var child in stackPanel.Children)
        {
            var border = Assert.IsType<Border>(child);
            var grid = Assert.IsType<Grid>(border.Child);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
        }
    }

    [AvaloniaFact]
    public void Build_ParameterWithCategories_RendersCategoryHeaders()
    {
        var builder = new ParameterViewBuilder();
        var param = new CoreIchimokuParameter();

        var control = builder.Build(param);
        var stackPanel = Assert.IsType<StackPanel>(control);

        // Find category header textblocks
        var headerTexts = stackPanel.Children
            .OfType<Border>()
            .Where(b => b.Child is TextBlock)
            .Select(b => ((TextBlock)b.Child!).Text)
            .ToList();

        Assert.Contains("Periods", headerTexts);
        Assert.Contains("Shifting", headerTexts);
    }

    [AvaloniaFact]
    public void Build_EgarchParameter_RendersGarchCoefficientsHeader()
    {
        var builder = new ParameterViewBuilder();
        var param = new CoreEgarchParameter();

        var control = builder.Build(param);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var headerTexts = stackPanel.Children
            .OfType<Border>()
            .Where(b => b.Child is TextBlock)
            .Select(b => ((TextBlock)b.Child!).Text)
            .ToList();

        Assert.Contains("Periods", headerTexts);
        Assert.Contains("GARCH Coefficients", headerTexts);
    }

    [AvaloniaFact]
    public void Build_WithDynamicPeriodSensitiveHidden_HidesPeriodsCategoriesComprehensively()
    {
        var builder = new ParameterViewBuilder();
        var ichimokuParam = new CoreIchimokuParameter();
        var hiddenTags = new[] { StockAnalyzer.Core.Constants.ParameterTags.DynamicPeriodSensitive };

        var control = builder.Build(ichimokuParam, hiddenTags);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var headerTexts = stackPanel.Children
            .OfType<Border>()
            .Where(b => b.Child is TextBlock)
            .Select(b => ((TextBlock)b.Child!).Text)
            .ToList();

        // "Periods" category should be completely hidden, only "Shifting" remains
        Assert.DoesNotContain("Periods", headerTexts);
        Assert.Contains("Shifting", headerTexts);

        // MACD parameter: all properties are under "Periods", so nothing should remain
        var macdParam = new CoreMacdParameter();
        var macdControl = builder.Build(macdParam, hiddenTags);
        var macdStackPanel = Assert.IsType<StackPanel>(macdControl);
        Assert.Empty(macdStackPanel.Children);
    }
}
