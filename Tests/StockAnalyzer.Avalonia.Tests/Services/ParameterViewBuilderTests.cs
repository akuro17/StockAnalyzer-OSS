using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Coverage for ParameterViewBuilder's row-building logic. The hiddenTags parameter is a general
/// extensibility point (currently unused by any tag - see ParameterViewBuilder.Build's doc comment)
/// and is asserted here to have no effect, regardless of what is passed.
/// </summary>
public class ParameterViewBuilderTests
{
    private static TextBlock? FindLabel(Control built, string labelText)
    {
        var stackPanel = Assert.IsType<StackPanel>(built);
        foreach (var child in stackPanel.Children)
        {
            var rowBorder = Assert.IsType<Border>(child);
            var rowGrid = Assert.IsType<Grid>(rowBorder.Child);
            var labelCell = (Border)rowGrid.Children[0];
            var label = (TextBlock)labelCell.Child!;
            if (label.Text == labelText) return label;
        }
        return null;
    }

    [AvaloniaFact]
    public void Build_WithoutHiddenTags_IncludesPeriodRow()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreSmaParameter { Period = 20 };

        var result = builder.Build(parameter, Array.Empty<string>());

        Assert.NotNull(FindLabel(result, "Period"));
    }

    [AvaloniaFact]
    public void Build_WithAnyHiddenTags_StillIncludesPeriodRow()
    {
        // hiddenTags no longer filters anything (Dynamic Period Driver's tag-based row hiding was
        // removed - see ParameterViewBuilder.Build's doc comment); passing any value must be a no-op.
        var builder = new ParameterViewBuilder();
        var parameter = new CoreSmaParameter { Period = 20 };

        var result = builder.Build(parameter, new[] { "SomeTag" });

        Assert.NotNull(FindLabel(result, "Period"));
    }

    [AvaloniaFact]
    public void Build_AllParameterTypes_BuildsSuccessfullyWithoutCrashing()
    {
        var builder = new ParameterViewBuilder();
        var baseType = typeof(StockAnalyzer.Core.Models.CoreIndicatorParameterBase);
        var types = baseType.Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        foreach (var type in types)
        {
            var instance = Activator.CreateInstance(type);
            Assert.NotNull(instance);
            var control = builder.Build(instance);
            Assert.NotNull(control);
        }
    }

    [AvaloniaFact]
    public void Build_WithCorrelationParameter_RendersComparisonSymbolAutoCompleteBox()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreCorrelationParameter { Period = 20, ComparisonSymbol = "MSFT" };

        var control = builder.Build(parameter);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var autoCompleteBoxFound = false;
        foreach (var child in stackPanel.Children)
        {
            if (child is Border b && b.Child is Grid g)
            {
                var input = g.Children.FirstOrDefault(c => Grid.GetColumn((Control)c) == 1);
                if (input is AutoCompleteBox ab)
                {
                    autoCompleteBoxFound = true;
                    Assert.Equal("MSFT", ab.Text);
                    Assert.Equal("Price vs Volume", ab.Watermark);
                    Assert.NotNull(ab.ItemTemplate);
                }
            }
        }

        Assert.True(autoCompleteBoxFound, "Expected to find an AutoCompleteBox for ComparisonSymbol property.");
    }
}


