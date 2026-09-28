using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Tests.Models;

public class TimeIntervalTests
{
    [Theory]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMinute, "1m")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FiveMinutes, "5m")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FifteenMinutes, "15m")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.ThirtyMinutes, "30m")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneHour, "1h")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FourHours, "4h")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneDay, "1d")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneWeek, "1w")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMonth, "1M")]
    public void ToApiString_ValidInterval_ReturnsCorrectString(StockAnalyzer.Core.Models.TimeInterval interval, string expected)
    {
        // Act
        var result = interval.ToApiString();

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMinute, "1 minute")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FiveMinutes, "5 minutes")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FifteenMinutes, "15 minutes")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneHour, "1 hour")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneDay, "1 day")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneWeek, "1 week")]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMonth, "1 month")]
    public void ToDisplayString_ValidInterval_ReturnsCorrectString(StockAnalyzer.Core.Models.TimeInterval interval, string expected)
    {
        // Act
        var result = interval.ToDisplayString();

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMinute, 1)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FiveMinutes, 5)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FifteenMinutes, 15)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.ThirtyMinutes, 30)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneHour, 60)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.FourHours, 240)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneDay, 1440)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneWeek, 10080)]
    [InlineData(StockAnalyzer.Core.Models.TimeInterval.OneMonth, 43200)]
    public void ToTimeSpan_ValidInterval_ReturnsCorrectTimeSpan(StockAnalyzer.Core.Models.TimeInterval interval, int expectedMinutes)
    {
        // Act
        var result = interval.ToTimeSpan();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result);
    }

    [Fact]
    public void ToApiString_UndefinedInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var invalidInterval = (StockAnalyzer.Core.Models.TimeInterval)999;

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => invalidInterval.ToApiString());
        Assert.Contains("未対応の TimeInterval 値です", ex.Message);
    }

    [Fact]
    public void ToDisplayString_UndefinedInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var invalidInterval = (StockAnalyzer.Core.Models.TimeInterval)999;

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>invalidInterval.ToDisplayString());
        Assert.Contains("未対応の TimeInterval 値です", ex.Message);
    }

    [Fact]
    public void ToTimeSpan_UndefinedInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var invalidInterval = (StockAnalyzer.Core.Models.TimeInterval)999;

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => invalidInterval.ToTimeSpan());
        Assert.Contains("未対応の TimeInterval 値です", ex.Message);
    }
}
