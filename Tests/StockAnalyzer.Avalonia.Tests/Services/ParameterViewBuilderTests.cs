using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Coverage for the ParameterTagAttribute-based row filtering added for the Dynamic Period Driver
/// feature (DynamicPeriodParameterVisibility). CoreSmaParameter.Period carries
/// [ParameterTag(ParameterTags.DynamicPeriodSensitive)], so it is a real, already-tagged parameter
/// class rather than a test-only stub.
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
    public void Build_WithMatchingHiddenTag_ExcludesTaggedRow()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreSmaParameter { Period = 20 };

        var result = builder.Build(parameter, new[] { ParameterTags.DynamicPeriodSensitive });

        Assert.Null(FindLabel(result, "Period"));
    }

    [AvaloniaFact]
    public void Build_WithUnrelatedHiddenTag_StillIncludesPeriodRow()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreSmaParameter { Period = 20 };

        var result = builder.Build(parameter, new[] { "SomeOtherTag" });

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
}
