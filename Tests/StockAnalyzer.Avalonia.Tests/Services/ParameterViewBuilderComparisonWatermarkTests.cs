using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// G-1 / G1-T5: the hint of the comparison-symbol box asks <see cref="ICrossTickerParameter.ComparisonSymbolOptional"/> instead of naming the two types that accept a blank
/// symbol. The result is the same for EVERY parameter object as it was with the type names: "Price vs Volume" for Correlation, CrossCorrelation and every parameter object that is
/// not a two-symbol one, empty for the other two-symbol types.
/// </summary>
public class ParameterViewBuilderComparisonWatermarkTests
{
    private const string OptionalHint = "Price vs Volume";

    /// <summary>The rule as it was written before the change (the oracle): the two types were named.</summary>
    private static string TheRuleBeforeTheChange(object parameterObject)
        => parameterObject is ICrossTickerParameter && parameterObject is not CoreCorrelationParameter && parameterObject is not CoreCrossCorrelationParameter
            ? string.Empty
            : OptionalHint;

    private static IEnumerable<object> EveryDefaultParameterObject()
        => IndicatorFactory.Default.GetRegisteredTypes()
            .Select(type => IndicatorFactory.CreateStatic(type)?.GetDefaultSettings().ParameterObject)
            .Where(parameters => parameters is not null)
            .Cast<object>();

    [Fact]
    public void TheHintIsTheSameAsBefore_ForEveryRegisteredIndicatorsParameterObject()
    {
        object[] all = EveryDefaultParameterObject().ToArray();

        foreach (object parameters in all)
        {
            Assert.Equal(TheRuleBeforeTheChange(parameters), ParameterViewBuilder.ComparisonSymbolWatermark(parameters));
        }

        // Not vacuous: all three kinds are among them.
        Assert.Contains(all, p => p is ICrossTickerParameter { ComparisonSymbolOptional: true });
        Assert.Contains(all, p => p is ICrossTickerParameter { ComparisonSymbolOptional: false });
        Assert.Contains(all, p => p is not ICrossTickerParameter);
    }

    [Fact]
    public void TheOptionalTypesKeepThePriceVersusVolumeHint_TheRequiredOnesHaveNone()
    {
        Assert.Equal(OptionalHint, ParameterViewBuilder.ComparisonSymbolWatermark(new CoreCorrelationParameter()));
        Assert.Equal(OptionalHint, ParameterViewBuilder.ComparisonSymbolWatermark(new CoreCrossCorrelationParameter()));

        foreach (object required in EveryDefaultParameterObject().Where(p => p is ICrossTickerParameter { ComparisonSymbolOptional: false }))
        {
            Assert.Equal(string.Empty, ParameterViewBuilder.ComparisonSymbolWatermark(required));
        }
    }

    [Fact]
    public void AnObjectThatIsNotATwoSymbolParameterKeepsTheHint()
    {
        Assert.Equal(OptionalHint, ParameterViewBuilder.ComparisonSymbolWatermark(new CoreSmaParameter()));
        Assert.Equal(OptionalHint, ParameterViewBuilder.ComparisonSymbolWatermark(new object()));
    }

    [AvaloniaFact]
    public void TheBoxOfTheBuiltFormShowsTheHint_ForACorrelationAndNoneForARollingBeta()
    {
        Assert.Equal(OptionalHint, ComparisonSymbolBoxOf(new CoreCorrelationParameter { ComparisonSymbol = "MSFT" }).Watermark);
        Assert.Equal(string.Empty, ComparisonSymbolBoxOf(new CoreRollingBetaParameter { ComparisonSymbol = "MSFT" }).Watermark);
    }

    private static AutoCompleteBox ComparisonSymbolBoxOf(object parameters)
    {
        Control control = new ParameterViewBuilder().Build(parameters);
        var panel = Assert.IsType<StackPanel>(control);
        foreach (Control child in panel.Children)
        {
            if (child is Border { Child: Grid grid } && grid.Children.FirstOrDefault(c => Grid.GetColumn((Control)c) == 1) is AutoCompleteBox box)
            {
                return box;
            }
        }

        throw new InvalidOperationException("No AutoCompleteBox for the ComparisonSymbol property was built.");
    }
}
