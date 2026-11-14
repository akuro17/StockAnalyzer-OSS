using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// WebAiExpansion plan 04: every new type is present in all four SSoT artifacts (default settings, default constants through its parameter object,
/// screener group, feature-channel normalization) and in exactly one screener group (owner decisions 2026-11-14, Q1=B and Q5=A).
/// </summary>
public class PlanFourRegistrationTests
{
    [Theory]
    [InlineData(IndicatorType.ParkinsonVolatility, "Volatility", CoreIndicatorCategory.Volatility, ChannelNormalization.WindowZScore)]
    [InlineData(IndicatorType.RogersSatchellVolatility, "Volatility", CoreIndicatorCategory.Volatility, ChannelNormalization.WindowZScore)]
    [InlineData(IndicatorType.Garch11, "Math", CoreIndicatorCategory.Volatility, ChannelNormalization.WindowZScore)]
    [InlineData(IndicatorType.HurstExponent, "Math", CoreIndicatorCategory.Other, ChannelNormalization.None)]
    [InlineData(IndicatorType.Entropy, "Math", CoreIndicatorCategory.Other, ChannelNormalization.None)]
    public void ANewTypeIsRegisteredInAllFourArtifacts(IndicatorType type, string expectedGroup, CoreIndicatorCategory expectedCategory, ChannelNormalization expectedNormalization)
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.Equal(expectedCategory, settings.Category);
        Assert.NotNull(settings.ParameterObject);
        Assert.NotEmpty(settings.SeriesColors);
        Assert.Equal(settings.SeriesColors.Count, settings.SeriesColors.Select(c => c.Color).Distinct().Count());

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { expectedGroup }, groups);

        Assert.Equal(expectedNormalization, FeatureChannelNormalizationDefaults.ForIndicator(type));
        Assert.NotNull(IndicatorFactory.CreateStatic(type));
    }

    [Theory]
    [InlineData(IndicatorType.HurstExponent)]
    [InlineData(IndicatorType.Entropy)]
    public void TheBoundedComplexityMeasuresHaveTheFixedAxisZeroToOne(IndicatorType type)
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);

        Assert.Equal(0m, settings.MinValue);
        Assert.Equal(1m, settings.MaxValue);
    }
}
