using System;
using System.Globalization;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Common;

/// <summary>
/// <see cref="DisplayFormat"/> is the single definition of the Data tab texts: the expected texts are the same under every current culture, including the
/// ones whose decimal separator, group separator or date separator differ from the invariant culture.
/// </summary>
public class DisplayFormatTests
{
    private static void UnderCulture(string cultureName, Action assertions)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            assertions();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    [InlineData("th-TH")] // Buddhist calendar: the year would differ if the current culture were used
    public void Texts_DoNotDependOnTheCurrentCulture(string cultureName) => UnderCulture(cultureName, () =>
    {
        Assert.Equal("1234.500", DisplayFormat.Price(1234.5m));
        Assert.Equal("-2.000", DisplayFormat.Price(-2m));
        Assert.Equal("1,234,567", DisplayFormat.Volume(1234567L));
        Assert.Equal("1,234,567", DisplayFormat.Volume(1234567.4m));
        Assert.Equal("0", DisplayFormat.Volume(0L));
        Assert.Equal("2026-01-02", DisplayFormat.Date(new DateTime(2026, 1, 2, 9, 5, 0)));
        Assert.Equal("2026-01-02 09:05", DisplayFormat.DateAndTime(new DateTime(2026, 1, 2, 9, 5, 0)));
        Assert.Equal("+2.000", DisplayFormat.ChangeAmount(2m));
        Assert.Equal("-2.000", DisplayFormat.ChangeAmount(-2m));
        Assert.Equal("+1.23%", DisplayFormat.ChangeRatio(1.234m));
        Assert.Equal("-1.23%", DisplayFormat.ChangeRatio(-1.234m));
        Assert.Equal("1,234.568", DisplayFormat.ComparisonPrice(1234.5678m));
        Assert.Equal("-3", DisplayFormat.Count(-3));
        Assert.Equal("12", DisplayFormat.Count(12));
        Assert.Equal("0", DisplayFormat.Days(0));
        Assert.Equal("1234", DisplayFormat.Days(1234));
    });

    [Fact]
    public void ChangeRows_AZeroChangeCarriesNoSign()
    {
        Assert.Equal("0.000", DisplayFormat.ChangeAmount(0m));
        Assert.Equal("0.00%", DisplayFormat.ChangeRatio(0m));
    }

    [Fact]
    public void Timestamp_ShowsTheTimeOfDayOnlyWhenItIsNotMidnight()
    {
        Assert.Equal("2026-01-02", DisplayFormat.Timestamp(new DateTime(2026, 1, 2, 0, 0, 0)));
        Assert.Equal("2026-01-02 00:01", DisplayFormat.Timestamp(new DateTime(2026, 1, 2, 0, 1, 0)));
        Assert.Equal("2026-01-02 10:00", DisplayFormat.Timestamp(new DateTime(2026, 1, 2, 10, 0, 0)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ComparisonValue_UsesTheUnitOfTheMode_UnderAnyCulture(string cultureName) => UnderCulture(cultureName, () =>
    {
        Assert.Equal("+5.000%", DisplayFormat.ComparisonValue(ComparisonMode.Performance, 5m));
        Assert.Equal("-2.000%", DisplayFormat.ComparisonValue(ComparisonMode.Performance, -2m));
        Assert.Equal("0.000%", DisplayFormat.ComparisonValue(ComparisonMode.Performance, 0m));
        Assert.Equal("1.5000", DisplayFormat.ComparisonValue(ComparisonMode.Ratio, 1.5m));
        Assert.Equal("Z:+1.23", DisplayFormat.ComparisonValue(ComparisonMode.ZScore, 1.234m));
        Assert.Equal("Z:-1.23", DisplayFormat.ComparisonValue(ComparisonMode.ZScore, -1.234m));
        Assert.Equal("Z:0.00", DisplayFormat.ComparisonValue(ComparisonMode.ZScore, 0m));
        Assert.Equal("+1,234.50", DisplayFormat.ComparisonValue(ComparisonMode.Spread, 1234.5m));
        Assert.Equal("-0.50", DisplayFormat.ComparisonValue(ComparisonMode.Spread, -0.5m));
    });

    [Fact]
    public void ComparisonValue_AnUnknownMode_FallsBackToFixedTwoDecimals()
    {
        Assert.Equal("1234.50", DisplayFormat.ComparisonValue((ComparisonMode)99, 1234.5m));
    }

    [Fact]
    public void DateFormats_AreTheIsoFormatOfTheDatePickers()
    {
        Assert.Equal(IsoDatePickerFormat.Format, ChartConstants.DataDateFormat);
        Assert.Equal(IsoDatePickerFormat.Format + " HH:mm", ChartConstants.DataDateTimeFormat);
    }

    [Fact]
    public void Culture_IsTheInvariantCulture()
    {
        Assert.Same(CultureInfo.InvariantCulture, DisplayFormat.Culture);
    }
}
