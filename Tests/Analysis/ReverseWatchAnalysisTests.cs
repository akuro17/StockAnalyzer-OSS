using StockAnalyzer.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Analysis;

public class ReverseWatchAnalysisTests
{
    private readonly DateTime _baseDate = new DateTime(2025, 1, 1);

    [Fact]
    public void Calculate_ValidData_ReturnsCorrectPointCount()
    {
        var candles = CreateTestCandles(50);
        var service = new ReverseWatchAnalysisService();
        var result = service.Calculate(candles, period: 25);
        Assert.Equal(26, result.Points.Count);
    }

    [Fact]
    public void Calculate_InvalidChronologicalOrder_ThrowsException()
    {
        var service = new ReverseWatchAnalysisService();
        var candles = new List<CandleData>
        {
            new() { Timestamp = _baseDate.AddDays(2), Close = 100, Volume = 100 },
            new() { Timestamp = _baseDate.AddDays(1), Close = 100, Volume = 100 }
        };

        Assert.Throws<ArgumentException>(() => service.Calculate(candles, period: 1));
    }

    [Fact]
    public void Calculate_InsufficientData_ThrowsException()
    {
        var candles = CreateTestCandles(10);
        var service = new ReverseWatchAnalysisService();
        
        Assert.Throws<ArgumentException>(() => service.Calculate(candles, period: 25));
    }

    [Fact]
    public void Calculate_InvalidPeriod_ThrowsException()
    {
        var candles = CreateTestCandles(50);
        var service = new ReverseWatchAnalysisService();
        
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Calculate(candles, period: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Calculate(candles, period: -1));
    }

    [Theory]
    [InlineData(100, 25, 76)]
    [InlineData(200, 50, 151)]
    public void Calculate_Scalability_VerifyResultCount(int inputCount, int period, int expectedOutput)
    {
        var candles = CreateTestCandles(inputCount);
        var service = new ReverseWatchAnalysisService();
        var result = service.Calculate(candles, period: period);
        Assert.Equal(expectedOutput, result.Points.Count);
    }

    [Fact]
    public void Calculate_Bounds_AreCorrectlyCalculated()
    {
        var candles = CreateTestCandles(50);
        var service = new ReverseWatchAnalysisService();
        var result = service.Calculate(candles, period: 25);
        
        Assert.NotNull(result.Bounds);
        Assert.True(result.Bounds.MinPrice <= result.Bounds.MaxPrice);
        Assert.True(result.Bounds.MinVolume <= result.Bounds.MaxVolume);
    }

    [Fact]
    public void Calculate_DataCount_CalculatesBoundsOnlyForTargetWindow()
    {
        // 100 candles: first 50 have low prices (10..20), last 50 have high prices (500..510)
        var candles = new List<CandleData>();
        for (int i = 0; i < 50; i++)
        {
            candles.Add(new CandleData { Timestamp = _baseDate.AddDays(i), Close = 10m + i % 5, Volume = 100 });
        }
        for (int i = 50; i < 100; i++)
        {
            candles.Add(new CandleData { Timestamp = _baseDate.AddDays(i), Close = 500m + i % 5, Volume = 1000 });
        }

        var service = new ReverseWatchAnalysisService();
        
        // Full bounds without dataCount restriction: MinPrice around 10, MaxPrice around 500
        var resultFull = service.Calculate(candles, period: 10, dataCount: 0);
        Assert.True(resultFull.Bounds.MinPrice < 50m);

        // Restricted bounds with dataCount = 20: MinPrice around 500
        var resultRestricted = service.Calculate(candles, period: 10, dataCount: 20);
        Assert.True(resultRestricted.Bounds.MinPrice > 450m);
    }

    [Fact]
    public void Calculate_NonMaBased_ReturnsCloseAndVolumeDirectly()
    {
        var candles = CreateTestCandles(50);
        var service = new ReverseWatchAnalysisService();
        var result = service.Calculate(candles, period: 25, stockCode: "", isMaBased: false, isLogScaleVolume: false);
        
        // period is 25, so first point is at index 24 (25th candle)
        Assert.Equal(candles[24].Close, result.Points[0].PriceAverage);
        Assert.Equal(candles[24].Volume, result.Points[0].VolumeAverage);
        
        // last point
        Assert.Equal(candles[49].Close, result.Points[^1].PriceAverage);
        Assert.Equal(candles[49].Volume, result.Points[^1].VolumeAverage);
    }

    [Fact]
    public void Calculate_LogScaleVolume_ReturnsLog10Volume()
    {
        var candles = CreateTestCandles(50);
        var service = new ReverseWatchAnalysisService();
        
        var resultMaLog = service.Calculate(candles, period: 25, stockCode: "", isMaBased: true, isLogScaleVolume: true);
        var resultNonMaLog = service.Calculate(candles, period: 25, stockCode: "", isMaBased: false, isLogScaleVolume: true);
        
        // For non-MA, the 25th candle (index 24) volume is 1000 + 240 = 1240.
        // Log10(1240) = 3.09342...
        decimal expectedLogVol = (decimal)Math.Log10(1240);
        Assert.Equal(expectedLogVol, resultNonMaLog.Points[0].VolumeAverage);

        // For MA, volume sum = 25 * 1000 + 10 * (0+1+...+24) = 25000 + 3000 = 28000
        // Avg = 1120. Log10(1120) = 3.049218...
        decimal expectedMaLogVol = (decimal)Math.Log10(1120);
        Assert.Equal(expectedMaLogVol, resultMaLog.Points[0].VolumeAverage);
    }

    private List<CandleData> CreateTestCandles(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData
            {
                Timestamp = _baseDate.AddDays(i),
                Close = 100m + (i % 10),
                Volume = 1000L + (i * 10)
            });
        }
        return candles;
    }
}
