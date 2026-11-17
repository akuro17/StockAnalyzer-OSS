using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Drawing.DrawingTestCandles;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>WebAI UI-exposure follow-up Run 3: Kalman Empty handling, culture independence of the rows added by the plan.</summary>
public class UiExposureRun3DrawingTests
{
    private static KalmanFilterProjectionObject SelectedKalman(IReadOnlyList<CoreCandleData> candles, decimal multiplier)
    {
        var obj = new KalmanFilterProjectionObject { FutureSteps = 10, ConfidenceMultiplier = multiplier };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[29].Timestamp, candles[29].Close));
        return obj;
    }

    [Fact]
    public void Kalman_RefusedBandValue_ClearsEveryPathAndLeavesNoStartPoint()
    {
        var candles = CreateCandles(40);

        var valid = SelectedKalman(candles, 2m);
        valid.Recalculate(candles, TimeSpan.FromHours(1));
        Assert.NotEmpty(valid.ProjectedPath);
        Assert.NotNull(valid.CalculatedResult);

        // A multiplier whose band exceeds the decimal range makes the engine return Empty (no exception, no silent zero).
        var refused = SelectedKalman(candles, 1e28m);
        refused.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.Empty(refused.ProjectedPath);
        Assert.Empty(refused.UpperBandPath);
        Assert.Empty(refused.LowerBandPath);
        Assert.Null(refused.CalculatedResult);
        Assert.Empty(refused.GetCalculatedValues(DateTime.UtcNow));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void SsaCalculatedValueRows_FormatWithEnUsWhateverTheThreadCulture(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            var separability = SsaCalculatedValueRows.Separability(87.62);
            var stability = SsaCalculatedValueRows.Stability(0.5, true);

            Assert.Equal("87.6% (Good)", separability.FormattedText);
            Assert.Equal("0.500 (Stable)", stability.FormattedText);
            Assert.Equal(87.62m, separability.NumericValue);
            Assert.Equal(0.5m, stability.NumericValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void SsaCalculatedValueRows_NonFiniteScore_GivesNoNumericValueInsteadOfThrowing()
    {
        var separability = SsaCalculatedValueRows.Separability(double.NaN);
        var stability = SsaCalculatedValueRows.Stability(double.PositiveInfinity, false);

        Assert.Null(separability.NumericValue);
        Assert.Null(stability.NumericValue);
    }
}

/// <summary>R-9: the effective period text asks the localiser for its label and unit word; Core supplies only the number.</summary>
public class EffectivePeriodTextTests
{
    private static string Fake(string key) => key switch
    {
        EffectivePeriodText.RowLabelKey => "LABEL",
        EffectivePeriodText.BarsUnitKey => "UNIT",
        _ => "[" + key + "]"
    };

    [Fact]
    public void Format_UsesTheLocalisedUnit_AndTheNumberFromTheCoreHelper()
    {
        var periods = new List<decimal?> { null, 12.5m };

        Assert.Equal("LABEL", EffectivePeriodText.Label(Fake));
        Assert.Equal("13 UNIT", EffectivePeriodText.Format(periods, 1, Fake));
        Assert.Equal(Core.Models.Indicators.EffectiveDynamicPeriodDisplay.UnavailableText, EffectivePeriodText.Format(periods, 0, Fake));
    }

    [Theory]
    [InlineData(EffectivePeriodText.RowLabelKey)]
    [InlineData(EffectivePeriodText.BarsUnitKey)]
    public void LocaleFiles_ContainTheKey_InBothLanguages_WithATranslatedJapaneseValue(string key)
    {
        string english = ReadValue("en", key);
        string japanese = ReadValue("ja", key);

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(japanese));
        Assert.NotEqual(english, japanese);
    }

    [Fact]
    public void EnglishLocale_KeepsTheWordsOfTheDocumentedRowText()
    {
        Assert.Equal("Effective Period", ReadValue("en", EffectivePeriodText.RowLabelKey));
        Assert.Equal("bars", ReadValue("en", EffectivePeriodText.BarsUnitKey));
    }

    private static string ReadValue(string language, string key)
    {
        using var document = System.Text.Json.JsonDocument.Parse(ReadLocale(language));
        return document.RootElement.GetProperty(key).GetString() ?? string.Empty;
    }

    private static string ReadLocale(string language)
    {
        using var stream = typeof(StockAnalyzer.Avalonia.Services.LocalizationManager).Assembly
            .GetManifestResourceStream(string.Format(StockAnalyzer.Avalonia.Services.LocalizationManager.LocaleResourceNameFormat, language))!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
