using System;
using System.Linq;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Converters;

/// <summary>
/// The category lists of the Indicator Manager and of the three picker dialogs show <see cref="CoreIndicatorCategory"/> through <see cref="EnumToLocalizedDisplayNameConverter"/>
/// (decision D-6 A'). Only the six chart-pattern categories have an <c>Enum_CoreIndicatorCategory_*</c> key and a [Description]; the seven existing categories must display exactly as
/// before in every language (the raw member name), and the six new ones must follow the enum order directly after <c>Other</c>.
/// </summary>
[Collection("LocalizationSharedState")]
public class IndicatorCategoryDisplayTests : IDisposable
{
    private static readonly CoreIndicatorCategory[] ExistingCategories =
    {
        CoreIndicatorCategory.Trend, CoreIndicatorCategory.Oscillator, CoreIndicatorCategory.Volume, CoreIndicatorCategory.Volatility,
        CoreIndicatorCategory.Math, CoreIndicatorCategory.Chart, CoreIndicatorCategory.Other,
    };

    private static readonly (CoreIndicatorCategory Category, string English, string Japanese)[] NewCategories =
    {
        (CoreIndicatorCategory.CandlestickPatterns, "Candlestick Patterns", "ローソク足パターン"),
        (CoreIndicatorCategory.Harmonic, "Harmonic", "ハーモニック"),
        (CoreIndicatorCategory.MarketStructure, "Market Structure", "市場構造"),
        (CoreIndicatorCategory.PatternRecognition, "Pattern Recognition", "パターン認識"),
        (CoreIndicatorCategory.TradingRules, "Trading Rules", "トレーディングルール"),
        (CoreIndicatorCategory.WaveTheory, "Wave Theory", "波動理論"),
    };

    public IndicatorCategoryDisplayTests() => LocalizationManager.Instance.Initialize("en");

    public void Dispose() => LocalizationManager.Instance.Initialize("en");

    [Fact]
    public void TheSixNewCategories_FollowOtherInTheDocumentedOrder_AndTheExistingValuesAreUnchanged()
    {
        for (int i = 0; i < ExistingCategories.Length; i++)
        {
            Assert.Equal(i, (int)ExistingCategories[i]);
        }

        var ordered = Enum.GetValues<CoreIndicatorCategory>().OrderBy(c => c).ToArray();
        Assert.Equal(ExistingCategories.Concat(NewCategories.Select(n => n.Category)), ordered);
        Assert.Equal(13, ordered.Length);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void TheExistingCategories_DisplayExactlyAsBefore_InEveryLanguage(string language)
    {
        LocalizationManager.Instance.Initialize(language);

        foreach (CoreIndicatorCategory category in ExistingCategories)
        {
            Assert.Equal(category.ToString(), EnumToLocalizedDisplayNameConverter.ResolveDisplayText(category));
        }
    }

    [Fact]
    public void TheNewCategories_DisplayTheirEnglishAndJapaneseNames()
    {
        foreach (var (category, english, japanese) in NewCategories)
        {
            LocalizationManager.Instance.Initialize("en");
            Assert.Equal(english, EnumToLocalizedDisplayNameConverter.ResolveDisplayText(category));

            LocalizationManager.Instance.Initialize("ja");
            Assert.Equal(japanese, EnumToLocalizedDisplayNameConverter.ResolveDisplayText(category));
        }
    }

    [Fact]
    public void TheConverterInstance_ConvertsACategoryAndFallsBackToToStringForANonEnum()
    {
        Assert.Equal("Wave Theory", EnumToLocalizedDisplayNameConverter.Instance.Convert(CoreIndicatorCategory.WaveTheory, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("text", EnumToLocalizedDisplayNameConverter.Instance.Convert("text", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
