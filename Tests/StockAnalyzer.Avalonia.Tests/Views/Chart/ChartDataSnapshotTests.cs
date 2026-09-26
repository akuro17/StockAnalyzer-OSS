using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// ChartDataSnapshot のスナップショット生成ロジックを検証するテスト。
/// </summary>
public class ChartDataSnapshotTests
{
    /// <summary>
    /// テスト用のローソク足データを生成します。
    /// </summary>
    private static List<Candle> CreateTestCandles(int count, decimal startPrice = 100m)
    {
        var candles = new List<Candle>();
        var random = new Random(42); // 再現性のためシード固定
        decimal price = startPrice;

        for (int i = 0; i < count; i++)
        {
            decimal change = (decimal)(random.NextDouble() - 0.5) * 10;
            decimal open = price;
            decimal close = price + change;
            decimal high = Math.Max(open, close) + (decimal)random.NextDouble() * 5;
            decimal low = Math.Min(open, close) - (decimal)random.NextDouble() * 5;

            candles.Add(new Candle
            {
                Date = DateTime.Now.AddDays(-count + i),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = random.Next(10000, 100000)
            });

            price = close;
        }

        return candles;
    }

    [Fact]
    public void Empty_ShouldReturnEmptySnapshot()
    {
        // Arrange & Act
        var snapshot = ChartDataSnapshot.Empty;

        // Assert
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Candles);
        // 空の場合、デフォルト値が設定される
        Assert.Equal(100m, snapshot.MaxPrice);  // デフォルト最高値
        Assert.Equal(0m, snapshot.MinPrice);    // デフォルト最安値
        Assert.Equal(100m, snapshot.PriceRange); // 100 - 0 = 100
    }

    [Fact]
    public void Constructor_ShouldCopyCandleData()
    {
        // Arrange
        var originalCandles = CreateTestCandles(10);

        // Act
        var snapshot = new ChartDataSnapshot(originalCandles);

        // Assert
        Assert.Equal(10, snapshot.Candles.Count);
        
        // 元のコレクションを変更してもスナップショットに影響しないことを確認
        originalCandles.Clear();
        Assert.Equal(10, snapshot.Candles.Count);
    }

    // Helper to create a dummy candle
    private CoreCandleData CreateCandle(DateTime time, decimal close)
    {
        return new CoreCandleData(time, close, close, close, close, 100);
    }

    [Fact]
    public void Constructor_ShouldCalculateCorrectMaxPrice()
    {
        // Arrange
        var candles = new List<Candle>
        {
            new Candle { High = 100m, Low = 90m, Open = 95m, Close = 98m },
            new Candle { High = 150m, Low = 95m, Open = 98m, Close = 145m },  // Max
            new Candle { High = 120m, Low = 100m, Open = 145m, Close = 110m }
        };

        // Act
        var snapshot = new ChartDataSnapshot(candles);

        // Assert
        Assert.Equal(150m, snapshot.MaxPrice);
    }

    [Fact]
    public void Constructor_ShouldCalculateCorrectMinPrice()
    {
        // Arrange
        var candles = new List<Candle>
        {
            new Candle { High = 100m, Low = 90m, Open = 95m, Close = 98m },   // Min
            new Candle { High = 150m, Low = 95m, Open = 98m, Close = 145m },
            new Candle { High = 120m, Low = 100m, Open = 145m, Close = 110m }
        };

        // Act
        var snapshot = new ChartDataSnapshot(candles);

        // Assert
        Assert.Equal(90m, snapshot.MinPrice);
    }

    [Fact]
    public void Constructor_ShouldHandleSingleCandleWithZeroPriceRange()
    {
        // Arrange - 同じ価格のローソク足1本
        var candles = new List<Candle>
        {
            new Candle { High = 100m, Low = 100m, Open = 100m, Close = 100m }
        };

        // Act
        var snapshot = new ChartDataSnapshot(candles);

        // Assert
        Assert.Equal(100m, snapshot.MaxPrice);
        Assert.Equal(100m, snapshot.MinPrice);
        Assert.Equal(1m, snapshot.PriceRange); // ゼロ除算防止のため1を返す
    }

    [Fact]
    public void CandleSnapshot_IsBullish_ShouldReturnTrueWhenCloseGreaterThanOpen()
    {
        // Arrange
        var bullishCandle = new CoreCandleData(
            DateTime.Now, 
            100m, 
            110m, 
            95m, 
            105m,  // Close > Open
            1000
        );

        // Assert
        Assert.True(bullishCandle.IsBullish);
    }

    [Fact]
    public void CandleSnapshot_IsBullish_ShouldReturnTrueWhenCloseEqualsOpen()
    {
        // Arrange - 同値終わり（陽線扱い）
        var dojiCandle = new CoreCandleData(
            DateTime.Now,
            100m,
            110m,
            95m,
            100m,  // Close == Open
            1000
        );

        // Assert
        Assert.True(dojiCandle.IsBullish);
    }

    [Fact]
    public void CandleSnapshot_IsBullish_ShouldReturnFalseWhenCloseLessThanOpen()
    {
        // Arrange
        var bearishCandle = new CoreCandleData(
            DateTime.Now,
            100m,
            105m,
            90m,
            95m,  // Close < Open
            1000
        );

        // Assert
        Assert.False(bearishCandle.IsBullish);
    }

    [Fact]
    public void Constructor_ShouldPreserveVolumeData()
    {
        // Arrange
        var candles = new List<Candle>
        {
            new Candle { High = 100m, Low = 90m, Open = 95m, Close = 98m, Volume = 50000 },
            new Candle { High = 110m, Low = 95m, Open = 98m, Close = 105m, Volume = 75000 }
        };

        // Act
        var snapshot = new ChartDataSnapshot(candles);

        // Assert
        Assert.Equal(50000L, snapshot.Candles[0].Volume);
        Assert.Equal(75000L, snapshot.Candles[1].Volume);
    }

    [Fact]
    public void Snapshot_ShouldBeImmutable()
    {
        // Arrange
        var candles = CreateTestCandles(5);
        var snapshot = new ChartDataSnapshot(candles);

        // Act & Assert - IReadOnlyList なので変更不可
        Assert.IsAssignableFrom<IReadOnlyList<CoreCandleData>>(snapshot.Candles);
    }
}
