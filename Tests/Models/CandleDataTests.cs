
namespace StockAnalyzer.Tests.Models;

public class CandleDataTests
{
    [Fact]
    public void Create_ValidData_ReturnsInstance()
    {
        // Arrange
        var timestamp = DateTime.UtcNow;
        decimal open = 100m, high =  110m, low = 95m, close = 105m;
        long volume = 1000;

        // Act
        var candle = new CandleData
        {
            Timestamp = timestamp,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume
        };

        // Assert

        Assert.Equal(timestamp, candle.Timestamp);
        Assert.Equal(open, candle.Open);
        Assert.Equal(high, candle.High);
        Assert.Equal(low, candle.Low);
        Assert.Equal(close, candle.Close);
        Assert.Equal(volume, candle.Volume);
    }

    [Fact]
    public void Create_InvalidData_IsNotValid()
    {
        // Arrange
        var timestamp = DateTime.UtcNow;
        decimal open = 100m, high = 90m, low = 95m, close = 105m; // High < Low
        long volume = 1000;

        // Act
        var candle = new CandleData
        {
            Timestamp = timestamp,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume
        };

        // Assert
        Assert.False(candle.IsValid()); // Invalid data
    }

    [Fact]
    public void IsBullish_CloseGreaterThanOpen_ReturnsTrue()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act & Assert
        Assert.True(candle.IsBullish);
    }

    [Fact]
    public void IsBullish_CloseLessThanOpen_ReturnsFalse()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 98m,
            Volume = 1000
        };

        // Act & Assert
        Assert.False(candle.IsBullish);
    }

    [Fact]
    public void BodyHeight_CalculatesCorrectly()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act
        var bodyHeight = candle.BodyHeight;

        // Assert
        Assert.Equal(5m, bodyHeight); // |105 - 100| = 5
    }

    [Fact]
    public void UpperShadow_CalculatesCorrectly()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act
        var upperShadow = candle.UpperShadow;

        // Assert
        Assert.Equal(5m, upperShadow); // 110 - max(100, 105) = 110 - 105 = 5
    }

    [Fact]
    public void LowerShadow_CalculatesCorrectly()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act
        var lowerShadow = candle.LowerShadow;

        // Assert
        Assert.Equal(5m, lowerShadow); // min(100, 105) - 95 = 100 - 95 = 5
    }

    [Fact]
    public void IsValid_ValidCandle_ReturnsTrue()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act & Assert
        Assert.True(candle.IsValid());
    }

    [Fact]
    public void IsValid_HighLessThanLow_ReturnsFalse()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 90m,  // Invalid: High < Low
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act & Assert
        Assert.False(candle.IsValid());
    }

    [Fact]
    public void IsValid_OpenAboveHigh_ReturnsFalse()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 115m, // Invalid: Open > High
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = 1000
        };

        // Act & Assert
        Assert.False(candle.IsValid());
    }

    [Fact]
    public void IsValid_NegativeVolume_ReturnsFalse()
    {
        // Arrange
        var candle = new CandleData
        {
            Timestamp = DateTime.UtcNow,
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            Volume = -1 // Invalid: Negative volume
        };

        // Act & Assert
        Assert.False(candle.IsValid());
    }
}
