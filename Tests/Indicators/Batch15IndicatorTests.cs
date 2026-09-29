using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Services;
using System.Windows.Media;
using Xunit;

namespace StockAnalyzer.Tests.Indicators;

/// <summary>
/// Tests for Batch 15 WebAI Advanced Indicators
/// </summary>
public class Batch15IndicatorTests
{
    private readonly List<CandleData> _testCandles;
    
    public Batch15IndicatorTests()
    {
        // Generate test data with 100 candles
        _testCandles = GenerateTestCandles(100);
    }
    
    private static List<CandleData> GenerateTestCandles(int count)
    {
        var candles = new List<CandleData>();
        var baseDate = DateTime.Now.AddDays(-count);
        var random = new Random(42); // Fixed seed for reproducibility
        
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            decimal change = (decimal)(random.NextDouble() * 2 - 1) * 2m;
            decimal high = price + Math.Abs(change) + 1m;
            decimal low = price - Math.Abs(change) - 1m;
            price += change;
            
            candles.Add(new CandleData
            {
                Timestamp = baseDate.AddDays(i),
                Open = price - change / 2,
                High = high,
                Low = low,
                Close = price,
                Volume = 1000 + random.Next(500)
            });
        }
        
        return candles;
    }
    
    [Fact]
    public void KalmanFilter_Calculate_ReturnsValues()
    {
        var indicator = new KalmanFilterIndicator(20, Colors.Blue);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
        Assert.Contains(indicator.Values, v => v.HasValue);
    }
    
    [Fact]
    public void FRAMA_Calculate_ReturnsValues()
    {
        var indicator = new FramaIndicator(20, Colors.Blue); // 20 is even
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void FRAMA_ThrowsForOddPeriod()
    {
        Assert.Throws<ArgumentException>(() => new FramaIndicator(21, Colors.Blue));
    }
    
    [Fact]
    public void Entropy_Calculate_ReturnsValues()
    {
        var indicator = new EntropyIndicator(20, Colors.Blue, 10);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void AnchoredVWAP_Calculate_ReturnsValuesAndBands()
    {
        var indicator = new AnchoredVwapIndicator(14, Colors.Blue, 2.0);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.NotEmpty(indicator.UpperBand);
        Assert.NotEmpty(indicator.LowerBand);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void GarmanKlassVolatility_Calculate_ReturnsValues()
    {
        var indicator = new GarmanKlassVolatilityIndicator(20, Colors.Blue);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void ZScore_Calculate_ReturnsValues()
    {
        var indicator = new ZScoreIndicator(20, Colors.Blue);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void KylesLambda_Calculate_ReturnsValues()
    {
        var indicator = new KylesLambdaIndicator(20, Colors.Blue);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void PVT_Calculate_ReturnsValues()
    {
        var indicator = new PriceVolumeTrendIndicator(Colors.Blue);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Fact]
    public void MtfLaggedEma_Calculate_ReturnsValues()
    {
        var indicator = new MtfLaggedEmaIndicator(20, Colors.Blue, 4);
        indicator.Calculate(_testCandles);
        
        Assert.NotEmpty(indicator.Values);
        Assert.Equal(_testCandles.Count, indicator.Values.Count);
    }
    
    [Theory]
    [InlineData(IndicatorType.KalmanFilter)]
    [InlineData(IndicatorType.FRAMA)]
    [InlineData(IndicatorType.Entropy)]
    [InlineData(IndicatorType.AnchoredVWAP)]
    [InlineData(IndicatorType.GarmanKlassVolatility)]
    [InlineData(IndicatorType.ZScore)]
    [InlineData(IndicatorType.KylesLambda)]
    [InlineData(IndicatorType.PVT)]
    [InlineData(IndicatorType.MtfLaggedEma)]
    public void IndicatorRegistry_HasBatch15Registered(IndicatorType type)
    {
        Assert.True(IndicatorRegistry.IsRegistered(type), $"{type} should be registered");
    }
}
