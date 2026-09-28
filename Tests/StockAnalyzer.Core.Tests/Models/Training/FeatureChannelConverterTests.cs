using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Coverage for <see cref="FeatureChannelConverter"/>, the single conversion point between an
/// indicator <see cref="FeatureChannel"/>'s string-keyed params and a live
/// <see cref="CoreIndicatorParameterBase"/>. Uses the real <see cref="IndicatorFactory"/> (mocking
/// <see cref="IIndicator"/>/<see cref="IndicatorType"/> is prohibited).
/// </summary>
public class FeatureChannelConverterTests
{
    private static readonly IIndicatorFactory Factory = new IndicatorFactory();

    private static string? FirstIntParamName(IndicatorType type)
    {
        var po = Factory.Create(type)?.GetDefaultSettings()?.ParameterObject;
        if (po is null)
        {
            return null;
        }

        foreach (var prop in po.GetType().GetProperties())
        {
            if (prop.CanWrite && prop.PropertyType == typeof(int))
            {
                return prop.Name;
            }
        }

        return null;
    }

    [Fact]
    public void BuildIndicatorSettings_AppliesParamsOverRegistryDefault()
    {
        var paramName = FirstIntParamName(IndicatorType.RSI);
        Assert.NotNull(paramName);

        var channel = new FeatureChannel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = IndicatorType.RSI,
            Params = new Dictionary<string, string> { [paramName!] = "7" },
        };

        var settings = FeatureChannelConverter.BuildIndicatorSettings(channel, Factory, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(IndicatorType.RSI, settings.TypeEnum);
        var applied = settings.ParameterObject!.GetType().GetProperty(paramName!)!.GetValue(settings.ParameterObject);
        Assert.Equal(7, applied);
    }

    [Fact]
    public void ExtractParams_ReturnsOnlyNonDefaultValues()
    {
        var paramName = FirstIntParamName(IndicatorType.RSI)!;
        var settings = Factory.Create(IndicatorType.RSI)!.GetDefaultSettings();
        settings.TypeEnum = IndicatorType.RSI;

        // Unchanged: nothing extracted.
        Assert.Empty(FeatureChannelConverter.ExtractParams(settings, Factory));

        // Changed: exactly that key comes back, as an invariant string.
        settings.ParameterObject!.GetType().GetProperty(paramName)!.SetValue(settings.ParameterObject, 9);
        var extracted = FeatureChannelConverter.ExtractParams(settings, Factory);
        Assert.Equal("9", Assert.Contains(paramName, (IDictionary<string, string>)extracted));
    }

    [Fact]
    public void RoundTrip_BuildThenExtract_IsStable()
    {
        var paramName = FirstIntParamName(IndicatorType.SMA)!;
        var channel = new FeatureChannel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = IndicatorType.SMA,
            Params = new Dictionary<string, string> { [paramName] = "33" },
        };

        var settings = FeatureChannelConverter.BuildIndicatorSettings(channel, Factory, out var warnings);
        Assert.Empty(warnings);
        var extracted = FeatureChannelConverter.ExtractParams(settings, Factory);

        Assert.Equal("33", Assert.Contains(paramName, (IDictionary<string, string>)extracted));
    }

    [Fact]
    public void ApplyParams_UnknownKey_IsIgnoredButReturnsWarning()
    {
        var settings = Factory.Create(IndicatorType.RSI)!.GetDefaultSettings();

        var warnings = FeatureChannelConverter.ApplyParams(
            settings.ParameterObject,
            new Dictionary<string, string> { ["definitely_not_a_param"] = "5" });

        var warning = Assert.Single(warnings);
        Assert.Contains("definitely_not_a_param", warning);
    }

    [Fact]
    public void ApplyParams_KnownKey_ReturnsNoWarnings()
    {
        var paramName = FirstIntParamName(IndicatorType.RSI)!;
        var settings = Factory.Create(IndicatorType.RSI)!.GetDefaultSettings();

        var warnings = FeatureChannelConverter.ApplyParams(
            settings.ParameterObject,
            new Dictionary<string, string> { [paramName] = "7" });

        Assert.Empty(warnings);
    }

    [Fact]
    public void ApplyParams_UnparseableValue_Throws()
    {
        var paramName = FirstIntParamName(IndicatorType.RSI)!;
        var settings = Factory.Create(IndicatorType.RSI)!.GetDefaultSettings();

        Assert.Throws<System.InvalidOperationException>(() =>
            FeatureChannelConverter.ApplyParams(
                settings.ParameterObject,
                new Dictionary<string, string> { [paramName] = "not-a-number" }));
    }

    [Fact]
    public void BuildIndicatorLabel_NullParameterObject_ReturnsShortNameOnly()
    {
        Assert.Equal("RSI", FeatureChannelConverter.BuildIndicatorLabel("RSI", null));
    }

    [Fact]
    public void BuildIndicatorLabel_AtDefault_StillListsCurrentValue()
    {
        // Unlike ExtractParams (diff-only), the label must show the period even when nothing was edited.
        var settings = Factory.Create(IndicatorType.RSI)!.GetDefaultSettings();
        var paramName = FirstIntParamName(IndicatorType.RSI)!;
        var defaultValue = settings.ParameterObject!.GetType().GetProperty(paramName)!.GetValue(settings.ParameterObject);

        var label = FeatureChannelConverter.BuildIndicatorLabel("RSI", settings.ParameterObject);

        Assert.Equal($"RSI ({defaultValue})", label);
    }

    [Fact]
    public void BuildIndicatorLabel_MultipleNumericParams_ListsAllInDeclarationOrder()
    {
        var settings = Factory.Create(IndicatorType.MACD)!.GetDefaultSettings();

        var label = FeatureChannelConverter.BuildIndicatorLabel("MACD", settings.ParameterObject);

        Assert.Equal("MACD (12, 26, 9)", label);
    }
}
