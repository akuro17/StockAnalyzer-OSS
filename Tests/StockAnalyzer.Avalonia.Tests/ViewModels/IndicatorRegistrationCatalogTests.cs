using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Unit tests of the pure helpers shared by the two indicator registration dialogs.</summary>
public class IndicatorRegistrationCatalogTests
{
    private static readonly IReadOnlyList<string> NoOutputs = Array.Empty<string>();

    private static CoreIndicatorSettings Sma(int period) => new()
    {
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new CoreSmaParameter { Period = period }
    };

    [Fact]
    public void BuildItems_SkipsPrice_OrdersByShortName_AndMapsMissingDefaultToOther()
    {
        var types = new[] { IndicatorType.SMA, IndicatorType.Price, IndicatorType.MACD, IndicatorType.EMA };
        var defaults = new List<CoreIndicatorSettings>
        {
            new() { TypeEnum = IndicatorType.SMA, Category = CoreIndicatorCategory.Trend },
            new() { TypeEnum = IndicatorType.MACD, Category = CoreIndicatorCategory.Oscillator },
        };

        var items = IndicatorRegistrationCatalog.BuildItems(types, defaults);

        Assert.Equal(new[] { "EMA", "MACD", "SMA" }, items.Select(i => i.ShortName).ToArray());
        Assert.DoesNotContain(items, i => i.Type == IndicatorType.Price);
        Assert.Equal(CoreIndicatorCategory.Other, items.Single(i => i.Type == IndicatorType.EMA).Category);
        Assert.Equal(CoreIndicatorCategory.Oscillator, items.Single(i => i.Type == IndicatorType.MACD).Category);
        Assert.Equal(CoreIndicatorCategory.Trend, items.Single(i => i.Type == IndicatorType.SMA).Category);
        Assert.All(items, i => Assert.Equal(i.Type.GetDescription(), i.DisplayName));
    }

    [Fact]
    public void BuildItems_OrdersByShortNameWithOrdinalComparison_UpperCaseAcronymsBeforePascalCaseNames()
    {
        var types = new[] { IndicatorType.AccelerationOscillator, IndicatorType.ADX, IndicatorType.SMA };

        var items = IndicatorRegistrationCatalog.BuildItems(types, new List<CoreIndicatorSettings>());

        string[] expected = types.Select(t => t.ToString()).ToArray();
        Array.Sort(expected, StringComparer.Ordinal);
        Assert.Equal(expected, items.Select(i => i.ShortName).ToArray());
        Assert.True(items.FindIndex(i => i.ShortName == "ADX") < items.FindIndex(i => i.ShortName == "AccelerationOscillator"));
    }

    [Fact]
    public void BuildItems_NoTypes_ReturnsEmptyList()
    {
        Assert.Empty(IndicatorRegistrationCatalog.BuildItems(Array.Empty<IndicatorType>(), new List<CoreIndicatorSettings>()));
    }

    [Fact]
    public void FormatPreviewName_NullSettings_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, IndicatorRegistrationCatalog.FormatPreviewName(null, false, false, null, NoOutputs));
    }

    [Fact]
    public void FormatPreviewName_PriceSource_UsesLabelOrShortName()
    {
        var price = new CoreIndicatorSettings { TypeEnum = IndicatorType.Price, PriceSource = PriceType.Typical };

        Assert.Equal(PriceDataHelper.FormatPriceTypeLabel(PriceType.Typical), IndicatorRegistrationCatalog.FormatPreviewName(price, false, false, null, NoOutputs));
        Assert.Equal("Typical", IndicatorRegistrationCatalog.FormatPreviewName(price, true, false, null, NoOutputs));
    }

    [Fact]
    public void FormatPreviewName_ParameterisedIndicator_LongKeepsSpaceShortRemovesIt()
    {
        string longName = IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), false, false, null, NoOutputs);
        string shortName = IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), true, false, null, NoOutputs);

        Assert.Equal(new CoreSmaParameter { Period = 20 }.GetDisplayName(IndicatorType.SMA.GetDescription()), longName);
        Assert.Equal("SMA(20)", shortName);
    }

    [Fact]
    public void FormatPreviewName_WithoutParameterObject_UsesTheBaseName()
    {
        var settings = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA };

        Assert.Equal(IndicatorType.SMA.GetDescription(), IndicatorRegistrationCatalog.FormatPreviewName(settings, false, false, null, NoOutputs));
        Assert.Equal("SMA", IndicatorRegistrationCatalog.FormatPreviewName(settings, true, false, null, NoOutputs));
    }

    [Fact]
    public void FormatPreviewName_NoTypeEnum_UsesUnknown()
    {
        Assert.Equal("Unknown", IndicatorRegistrationCatalog.FormatPreviewName(new CoreIndicatorSettings(), false, false, null, NoOutputs));
    }

    [Theory]
    [InlineData("Signal", true, true)]      // not the first output, not main -> suffix
    [InlineData("MacdLine", true, false)]   // the first output -> no suffix
    [InlineData("Main", true, false)]       // the main series name -> no suffix (value taken from IndicatorResult.MainSeriesName below)
    [InlineData("", true, false)]           // empty selection -> no suffix
    [InlineData("Signal", false, false)]    // indicator has a single output -> no suffix
    public void FormatPreviewName_OutputSuffix_FollowsTheSelectionRules(string selected, bool multiple, bool expectSuffix)
    {
        string output = selected == "Main" ? IndicatorResult.MainSeriesName : selected;
        var outputs = new List<string> { "MacdLine", "Signal", "Histogram" };
        string baseName = IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), false, false, null, outputs);

        string actual = IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), false, multiple, output, outputs);

        Assert.Equal(expectSuffix ? $"{baseName} ({output})" : baseName, actual);
    }

    [Fact]
    public void FormatPreviewName_EmptyOutputList_AddsSuffixForANonMainSelection()
    {
        string baseName = IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), false, false, null, NoOutputs);

        Assert.Equal($"{baseName} (Signal)", IndicatorRegistrationCatalog.FormatPreviewName(Sma(20), false, true, "Signal", NoOutputs));
    }
}
