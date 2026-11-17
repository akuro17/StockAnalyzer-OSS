using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Indicator Timeframe, Sprint 3 / T5: the Pivot Points' source-period row reads "Timeframe" and lists Same as Chart / Weekly / Monthly in
/// declaration order (not alphabetically), in English and Japanese. The stored enum names and integers are unchanged.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class ParameterViewBuilderPivotTimeframeTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static (string Label, ComboBox Combo) FindSourcePeriodRow(CoreIndicatorParameterBase parameter)
    {
        var stackPanel = Assert.IsType<StackPanel>(new ParameterViewBuilder().Build(parameter, Array.Empty<string>(), ParameterFormLayout.InlineLabel));
        foreach (var child in stackPanel.Children)
        {
            if (child is Border { Child: Grid grid })
            {
                var input = grid.Children.FirstOrDefault(c => Grid.GetColumn((Control)c) == 1);
                if (input is ComboBox { SelectedItem: PivotSourcePeriod } combo)
                {
                    var label = (TextBlock)((Border)grid.Children[0]).Child!;
                    return (label.Text!, combo);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("The source-period row was not built.");
    }

    [AvaloniaTheory]
    [InlineData("en", false, "Timeframe", "Same as Chart", "Weekly", "Monthly")]
    [InlineData("en", true, "Timeframe", "Same as Chart", "Weekly", "Monthly")]
    // The ja texts are written as \u escapes (code files stay English-only): label "Timeframe (bar type)", then "Same as Chart", "Weekly", "Monthly".
    [InlineData("ja", false, "\u6642\u9593\u8EF8 (\u8DB3\u7A2E)", "\u30C1\u30E3\u30FC\u30C8\u3068\u540C\u3058", "\u9031\u8DB3", "\u6708\u8DB3")]
    [InlineData("ja", true, "\u6642\u9593\u8EF8 (\u8DB3\u7A2E)", "\u30C1\u30E3\u30FC\u30C8\u3068\u540C\u3058", "\u9031\u8DB3", "\u6708\u8DB3")]
    public void SourcePeriodRow_HasTimeframeLabel_AndDeclarationOrderedLocalizedItems(string language, bool classic, string expectedLabel, string first, string second, string third)
    {
        LocalizationManager.Instance.Initialize(language);
        CoreIndicatorParameterBase parameter = classic ? new CoreClassicPivotPointsParameter() : new CorePivotPointsAdvancedParameter();

        var (label, combo) = FindSourcePeriodRow(parameter);

        Assert.Equal(expectedLabel, label);
        Assert.NotNull(combo.ItemTemplate);
        var items = combo.ItemsSource!.Cast<PivotSourcePeriod>().ToList();
        Assert.Equal(new[] { PivotSourcePeriod.PreviousBar, PivotSourcePeriod.Week, PivotSourcePeriod.Month }, items);
        Assert.Equal(new[] { first, second, third }, items.Select(i => ParameterViewBuilder.ResolveEnumDisplayText(i)).ToArray());
    }

    [AvaloniaTheory]
    [InlineData(PivotSourcePeriod.Week)]
    [InlineData(PivotSourcePeriod.Month)]
    [InlineData(PivotSourcePeriod.PreviousBar)]
    public void SelectingAnItem_WritesSourcePeriod_OnBothPivotParameters(PivotSourcePeriod selected)
    {
        LocalizationManager.Instance.Initialize("en");
        var advanced = new CorePivotPointsAdvancedParameter { SourcePeriod = PivotSourcePeriod.Week };
        var classic = new CoreClassicPivotPointsParameter { SourcePeriod = PivotSourcePeriod.Week };

        FindSourcePeriodRow(advanced).Combo.SelectedItem = selected;
        FindSourcePeriodRow(classic).Combo.SelectedItem = selected;

        Assert.Equal(selected, advanced.SourcePeriod);
        Assert.Equal(selected, classic.SourcePeriod);
    }

    [Fact]
    public void StoredNamesAndIntegers_AreUnchanged()
    {
        Assert.Equal(new[] { "PreviousBar", "Week", "Month" }, Enum.GetNames<PivotSourcePeriod>());
        Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<PivotSourcePeriod>().Select(v => (int)v).ToArray());
        Assert.False(((EnumDisplaySortAttribute)Attribute.GetCustomAttribute(typeof(PivotSourcePeriod), typeof(EnumDisplaySortAttribute))!).SortAlphabetically);
    }
}
