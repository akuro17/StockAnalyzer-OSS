#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// S-1: the predicate that says "this side needs the daily bars of a comparison symbol" and the optional-member contract it reads
/// (<see cref="ICrossTickerParameter.ComparisonSymbolOptional"/>, an EXPLICIT interface implementation, never a public property).
/// </summary>
public class ScreenerNeedsDailyComparisonBarsTests
{
    private const string OptionalMemberName = nameof(ICrossTickerParameter.ComparisonSymbolOptional);

    private static ScreenerIndicatorSideConfig Side(IndicatorType type, string? comparisonSymbol = null, ScreenerItemCategoryType category = ScreenerItemCategoryType.Indicator)
    {
        var side = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            CategoryType = category,
            OutputName = IndicatorResult.MainSeriesName,
            TimeFrame = TimeFrame.D1,
        };
        if (comparisonSymbol != null)
        {
            side.Parameters[nameof(ICrossTickerParameter.ComparisonSymbol)] = comparisonSymbol;
        }

        return side;
    }

    private static IEnumerable<IndicatorType> TwoTickerTypes()
        => IndicatorFactory.Default.GetRegisteredTypes()
            .Where(type => IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal);

    // ----- the optional member -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    [InlineData(IndicatorType.RankCorrelation)]
    public void IsComparisonSymbolOptional_IsTrueForTheTwoTypesThatAcceptABlankSymbol(IndicatorType type)
    {
        Assert.True(ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
    }

    [Theory]
    [InlineData(IndicatorType.RollingBeta)]
    [InlineData(IndicatorType.RollingAlpha)]
    [InlineData(IndicatorType.RollingRSquared)]
    [InlineData(IndicatorType.CointegrationSpread)]
    [InlineData(IndicatorType.CrossTickerDtw)]
    [InlineData(IndicatorType.PairRatio)]
    [InlineData(IndicatorType.PairSpread)]
    [InlineData(IndicatorType.Ratiocator)]
    public void IsComparisonSymbolOptional_IsFalseForTheTypesThatNeedASymbol(IndicatorType type)
    {
        Assert.True(IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator, "the fixture must name a two-ticker type");
        Assert.False(ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
    }

    [Theory]
    [InlineData(IndicatorType.SMA)]
    [InlineData(IndicatorType.RSI)]
    [InlineData(IndicatorType.Price)]
    public void IsComparisonSymbolOptional_IsFalseForATypeThatDoesNotCompareTwoSymbols(IndicatorType type)
    {
        Assert.False(ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
    }

    [Fact]
    public void IsComparisonSymbolOptional_IsTrueForExactlyCorrelationAndCrossCorrelation_AmongEveryRegisteredTwoTickerType()
    {
        IndicatorType[] optional = TwoTickerTypes().Where(ScreenerComparisonSymbols.IsComparisonSymbolOptional).ToArray();

        Assert.Equal(new[] { IndicatorType.CrossCorrelation, IndicatorType.Correlation, IndicatorType.RankCorrelation }.OrderBy(t => t.ToString(), StringComparer.Ordinal), optional);
    }

    [Fact]
    public void TheOptionalMember_DefaultsToFalse_AndTheTwoCorrelationParametersReturnTrueThroughTheInterfaceOnly()
    {
        Assert.False(((ICrossTickerParameter)new CoreRollingBetaParameter()).ComparisonSymbolOptional);
        Assert.False(((ICrossTickerParameter)new CoreRatiocatorParameter()).ComparisonSymbolOptional);
        Assert.False(((ICrossTickerParameter)new CoreCrossTickerDtwParameter()).ComparisonSymbolOptional);
        Assert.True(((ICrossTickerParameter)new CoreCorrelationParameter()).ComparisonSymbolOptional);
        Assert.True(((ICrossTickerParameter)new CoreCrossCorrelationParameter()).ComparisonSymbolOptional);
        Assert.True(((ICrossTickerParameter)new CoreRankCorrelationParameter()).ComparisonSymbolOptional);
    }

    // Reflection-based readers (IndicatorRegistrationViewModel.BuildParameterDictionary, ParameterViewBuilder, the MCP IndicatorSettingsBinder) take every PUBLIC
    // instance property as a parameter. The optional member must never be one of them, or saved templates, scatter files, the UI and the MCP schema change.
    [Theory]
    [InlineData(typeof(CoreCorrelationParameter), new[] { "Period", "ComparisonSymbol", "ComparisonPriceSource", "CalculationMode" })]
    [InlineData(typeof(CoreCrossCorrelationParameter), new[]
    {
        "PriceSource", "Period", "Lag", "EnableDynamicLag", "MinLag", "MaxLag", "Threshold", "LagSmoothingPeriod", "PeakSelectionMode",
        "ComparisonSymbol", "ComparisonPriceSource", "CalculationMode",
    })]
    [InlineData(typeof(CoreCrossTickerDtwParameter), new[]
    {
        "Period", "ComparisonSymbol", "OutputMode", "WarpingRadius", "PriceSource", "ComparisonPriceSource",
    })]
    [InlineData(typeof(CoreRankCorrelationParameter), new[]
    {
        "Period", "ComparisonSymbol", "PriceSource", "ComparisonPriceSource", "Method",
    })]
    public void TheOptionalMember_IsNotAPublicProperty_AndTheDeclaredPublicPropertiesAreUnchanged(Type parameterType, string[] expectedDeclared)
    {
        PropertyInfo[] allPublic = parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(allPublic, p => p.Name.EndsWith(OptionalMemberName, StringComparison.Ordinal));

        string[] declared = parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedDeclared.OrderBy(n => n, StringComparer.Ordinal), declared);
    }

    // ----- the predicate ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public void Needs_IsTrue_ForAnOptionalTypeThatNamesASymbol(IndicatorType type)
    {
        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type, "SPY")));
        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type, "  spy ")));
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public void Needs_IsFalse_ForAnOptionalTypeWithABlankSymbol_PriceVersusVolume(IndicatorType type)
    {
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type)));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type, "")));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type, "   ")));
    }

    [Theory]
    [InlineData(IndicatorType.RollingBeta)]
    [InlineData(IndicatorType.RollingAlpha)]
    [InlineData(IndicatorType.RollingRSquared)]
    [InlineData(IndicatorType.CointegrationSpread)]
    [InlineData(IndicatorType.PairRatio)]
    [InlineData(IndicatorType.PairSpread)]
    [InlineData(IndicatorType.Ratiocator)]
    public void Needs_IsTrue_ForARequiredTypeWhetherOrNotItNamesASymbol(IndicatorType type)
    {
        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type)));
        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(type, "SPY")));
    }

    [Fact]
    public void Needs_IsFalse_ForATypeThatDoesNotCompareTwoSymbols_EvenWithAComparisonSymbolParameter()
    {
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.SMA, "SPY")));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.Price, "SPY")));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.RSI)));
    }

    [Fact]
    public void Needs_IsFalse_ForNullAndForAnotherCategory()
    {
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(null));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.Correlation, "SPY", ScreenerItemCategoryType.Column)));
        Assert.False(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.RollingBeta, "SPY", ScreenerItemCategoryType.Criteria)));
    }

    [Fact]
    public void Needs_JudgesTheTypeOnly_NotTheTimeFrameOrTheEligibility()
    {
        ScreenerIndicatorSideConfig weekly = Side(IndicatorType.Correlation, "SPY");
        weekly.TimeFrame = TimeFrame.W1;

        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(weekly));
        // RollingBeta is not allowed in the Screener (BacktestIndicatorEligibility), but the predicate is a property of the type.
        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(Side(IndicatorType.RollingBeta, "SPY")));
    }

    [Fact]
    public void Needs_ReadsAJsonStringSymbol_LikeASavedSide()
    {
        ScreenerIndicatorSideConfig side = Side(IndicatorType.Correlation);
        side.Parameters[nameof(ICrossTickerParameter.ComparisonSymbol)] = System.Text.Json.JsonDocument.Parse("\"SPY\"").RootElement.Clone();

        Assert.True(ScreenerComparisonSymbols.NeedsDailyComparisonBars(side));
    }
}
