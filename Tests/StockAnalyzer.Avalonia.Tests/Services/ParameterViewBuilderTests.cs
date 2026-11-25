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
[Collection("LocalizationSharedState")]
public class ParameterViewBuilderTests
{
    private static TextBlock? FindLabel(Control built, string labelText)
    {
        var expectedLoc = LocalizationManager.Instance.Get($"Param_{labelText}") ?? labelText;
        var stackPanel = Assert.IsType<StackPanel>(built);
        foreach (var child in stackPanel.Children)
        {
            var rowBorder = Assert.IsType<Border>(child);
            var rowGrid = Assert.IsType<Grid>(rowBorder.Child);
            var labelCell = (Border)rowGrid.Children[0];
            var label = (TextBlock)labelCell.Child!;
            if (label.Text == labelText || label.Text == expectedLoc) return label;
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

    [AvaloniaFact]
    public void Build_WithTwoColumnFields_RendersResponsiveFormPanelWithFields()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreSmaParameter { Period = 25 };

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.TwoColumnFields);
        var panel = Assert.IsType<StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel>(control);

        Assert.NotEmpty(panel.Children);
        var field = Assert.IsType<StackPanel>(panel.Children[0]);
        var label = Assert.IsType<TextBlock>(field.Children[0]);
        var expectedLoc = LocalizationManager.Instance.Get("Param_Period") ?? "Period";
        Assert.True(label.Text == "Period" || label.Text == expectedLoc);

        var nud = Assert.IsType<NumericUpDown>(field.Children[1]);
        Assert.Equal(double.NaN, nud.Width);
        Assert.Equal(25, nud.Value);

        // Verify two-way binding writes through
        nud.Value = 30;
        Assert.Equal(30, parameter.Period);
    }

    [AvaloniaFact]
    public void Build_WithTwoColumnFields_SetsFullWidthOnCategoryHeaders()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreIchimokuParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.TwoColumnFields);
        var panel = Assert.IsType<StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel>(control);

        var headers = panel.Children.OfType<Border>().ToList();
        if (headers.Count > 0)
        {
            foreach (var header in headers)
            {
                Assert.True(StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel.GetIsFullWidth(header));
            }
        }
    }

    [AvaloniaFact]
    public void Build_WithMarketBreadthParameter_SortsMetricModeAndExpandsInlineComboBoxWidth()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreMarketBreadthParameter { MetricMode = MarketBreadthMetricMode.Cvi };

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var stackPanel = Assert.IsType<StackPanel>(control);

        ComboBox? metricCombo = null;
        AutoCompleteBox? universeBox = null;
        foreach (var child in stackPanel.Children)
        {
            if (child is Border b && b.Child is Grid g)
            {
                var input = g.Children.FirstOrDefault(c => Grid.GetColumn((Control)c) == 1);
                if (input is ComboBox cb && cb.SelectedItem is MarketBreadthMetricMode)
                {
                    metricCombo = cb;
                }
                else if (input is AutoCompleteBox ab)
                {
                    universeBox = ab;
                }
            }
        }

        Assert.NotNull(universeBox);
        Assert.Equal("List / Filter", universeBox!.Watermark);

        Assert.NotNull(metricCombo);
        Assert.Equal(double.NaN, metricCombo!.Width);
        Assert.Equal(220, metricCombo.MinWidth);
        Assert.NotNull(metricCombo.ItemTemplate);

        var actualOrder = metricCombo.ItemsSource!.Cast<MarketBreadthMetricMode>().ToList();
        var expectedOrder = Enum.GetValues<MarketBreadthMetricMode>()
            .OrderBy(m => ParameterViewBuilder.ResolveEnumDisplayText(m), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => (int)m)
            .ToList();
        Assert.Equal(expectedOrder, actualOrder);

        metricCombo.SelectedItem = MarketBreadthMetricMode.Trin;
        Assert.Equal(MarketBreadthMetricMode.Trin, parameter.MetricMode);
    }

    [AvaloniaFact]
    public void Build_WithoutEnumDisplaySort_PreservesDeclarationOrderAndDefaultWidth()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreVwapParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var stackPanel = Assert.IsType<StackPanel>(control);

        ComboBox? enumCombo = null;
        foreach (var child in stackPanel.Children)
        {
            if (child is Border b && b.Child is Grid g)
            {
                var input = g.Children.FirstOrDefault(c => Grid.GetColumn((Control)c) == 1);
                if (input is ComboBox cb)
                {
                    enumCombo = cb;
                    break;
                }
            }
        }

        Assert.NotNull(enumCombo);
        Assert.Equal(140, enumCombo!.Width);
        Assert.Equal(Enum.GetValues<VwapResetPeriod>().Cast<object>().ToList(), enumCombo.ItemsSource!.Cast<object>().ToList());
    }

    [AvaloniaFact]
    public void Build_WithInlineBoolParameters_DoesNotDuplicateLabelInCheckBoxContent()
    {
        var builder = new ParameterViewBuilder();
        var parameter = CoreCandlePatternParameter.CreateFor(StockAnalyzer.Core.Models.IndicatorType.CandleHammer);

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var checkBoxRows = stackPanel.Children
            .OfType<Border>()
            .Select(b => b.Child as Grid)
            .Where(g => g != null && g.Children.Count >= 2 && Grid.GetColumn((Control)g.Children[1]) == 1 && g.Children[1] is CheckBox)
            .ToList();

        Assert.NotEmpty(checkBoxRows);
        foreach (var rowGrid in checkBoxRows)
        {
            var labelCell = Assert.IsType<Border>(rowGrid!.Children[0]);
            var label = Assert.IsType<TextBlock>(labelCell.Child);
            var checkBox = Assert.IsType<CheckBox>(rowGrid.Children[1]);

            Assert.False(string.IsNullOrWhiteSpace(label.Text));
            Assert.Null(checkBox.Content);
            Assert.Equal(label.Text, global::Avalonia.Automation.AutomationProperties.GetName(checkBox));
        }
    }

    [AvaloniaFact]
    public void Build_WithTwoColumnFieldsBoolParameter_RetainsLabelInCheckBoxContent()
    {
        var builder = new ParameterViewBuilder();
        var parameter = new CoreStructureTrendParameter();

        var control = builder.Build(parameter, Array.Empty<string>(), ParameterFormLayout.TwoColumnFields);
        var panel = Assert.IsType<StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel>(control);

        var checkBox = Assert.Single(panel.Children.OfType<CheckBox>());
        var content = checkBox.Content?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(content));
    }
}


