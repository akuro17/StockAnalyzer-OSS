using StockAnalyzer.ViewModels;
using System.Diagnostics;
using System.Windows.Media;

namespace StockAnalyzer.Tests.Performance;

/// <summary>
/// Performance benchmarks for indicator calculations
/// </summary>
public class IndicatorPerformanceBenchmark
{
    [Fact]
    public void RecalculateIndicators_LargeDataset_CompletesInReasonableTime()
    {
        // Arrange: Create 10,000 candles
        var candles = GenerateLargeDataset(10000);
        var symbol = Symbol.Create("TEST", "Test Stock", "NYSE");
        var marketData = new MarketData(symbol, TimeInterval.OneDay, candles, "Benchmark");

        var viewModel = new IndicatorManagementViewModel();
        
        // Add 5 indicators (SMA, EMA, BB, RSI, MACD)
        viewModel.Indicators.Add(new SmaIndicator(25, Colors.Cyan, 1.5));
        viewModel.Indicators.Add(new EmaIndicator(12, Colors.Orange, 1.5));
        viewModel.Indicators.Add(new BollingerBandsIndicator(20, 2.0m, Colors.Lime, 1.0));
        viewModel.Indicators.Add(new RsiIndicator(14, Colors.Purple, 1.5));
        viewModel.Indicators.Add(new MacdIndicator(12, 26, 9, Colors.Blue, 1.5));

        // Act: Measure calculation time
        var stopwatch = Stopwatch.StartNew();
        viewModel.RecalculateIndicators(marketData);
        stopwatch.Stop();

        // Assert: Should complete in less than 2 seconds (generous limit)
        // With parallelization, expect ~700ms on 4-core CPU
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, 
            $"Calculation took {stopwatch.ElapsedMilliseconds}ms, expected <2000ms");

        // Output actual time for reference
        Console.WriteLine($"Parallel calculation time: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(Skip = "Performance benchmark is hardware-dependent and unreliable in CI environments")]
    public void RecalculateIndicators_ParallelVsSequential_ShowsSpeedup()
    {
        // Arrange
        var candles = GenerateLargeDataset(5000);
        var symbol = Symbol.Create("TEST", "Test Stock", "NYSE");
        var marketData = new MarketData(symbol, TimeInterval.OneDay, candles, "Benchmark");

        // Create 5 indicators
        var indicators = new List<IIndicator>
        {
            new SmaIndicator(25, Colors.Cyan, 1.5),
            new EmaIndicator(12, Colors.Orange, 1.5),
            new BollingerBandsIndicator(20, 2.0m, Colors.Lime, 1.0),
            new RsiIndicator(14, Colors.Purple, 1.5),
            new MacdIndicator(12, 26, 9, Colors.Blue, 1.5)
        };

        // Sequential calculation (for comparison)
        var stopwatchSeq = Stopwatch.StartNew();
        foreach (var indicator in indicators)
        {
            indicator.Calculate(marketData);
        }
        stopwatchSeq.Stop();

        // Parallel calculation
        var viewModel = new IndicatorManagementViewModel();
        foreach (var indicator in indicators)
        {
            viewModel.Indicators.Add(indicator);
        }

        var stopwatchPar = Stopwatch.StartNew();
        viewModel.RecalculateIndicators(marketData);
        stopwatchPar.Stop();

        // Output results
        Console.WriteLine($"Sequential: {stopwatchSeq.ElapsedMilliseconds}ms");
        Console.WriteLine($"Parallel:   {stopwatchPar.ElapsedMilliseconds}ms");
        
        double speedup = (double)stopwatchSeq.ElapsedMilliseconds / stopwatchPar.ElapsedMilliseconds;
        Console.WriteLine($"Speedup:    {speedup:F2}x");

        // Assert: Parallel should be faster (at least 1.1x on any multi-core system)
        // Note: CI environments may have limited CPU, so using conservative threshold
        Assert.True(speedup >= 1.1, 
            $"Expected at least 1.1x speedup, got {speedup:F2}x");
    }

    private List<CandleData> GenerateLargeDataset(int count)
    {
        var candles = new List<CandleData>();
        var random = new Random(42); // Fixed seed for reproducibility
        decimal basePrice = 1000m;

        for (int i = 0; i < count; i++)
        {
            // Generate realistic price movement
            decimal change = (decimal)(random.NextDouble() * 20 - 10); // ±10
            decimal open = basePrice;
            decimal close = basePrice + change;
            decimal high = Math.Max(open, close) + (decimal)(random.NextDouble() * 5);
            decimal low = Math.Min(open, close) - (decimal)(random.NextDouble() * 5);
            long volume = random.Next(1000000, 10000000);

            candles.Add(new CandleData
            {
                Timestamp = DateTime.Now.AddMinutes(-count + i),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });

            basePrice = close; // Next candle starts where this one ended
        }

        return candles;
    }
}
