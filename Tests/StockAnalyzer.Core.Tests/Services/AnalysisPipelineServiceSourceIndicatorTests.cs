using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class AnalysisPipelineServiceSourceIndicatorTests : IDisposable
{
    private readonly SourceIndicatorService _sourceIndicatorService;
    private readonly AnalysisPipelineService _pipelineService;
    private readonly string _sourceFilePath;

    public AnalysisPipelineServiceSourceIndicatorTests()
    {
        _sourceFilePath = Path.Combine(Path.GetTempPath(), $"source_indicators_pipeline_test_{Guid.NewGuid():N}.json");
        _sourceIndicatorService = new SourceIndicatorService(_sourceFilePath);
        _pipelineService = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default,
            sourceIndicatorService: _sourceIndicatorService);
    }

    public void Dispose()
    {
        _sourceIndicatorService.Dispose();
        if (File.Exists(_sourceFilePath))
        {
            try { File.Delete(_sourceFilePath); } catch { }
        }
    }

    private static List<CoreCandleData> CreateTestCandles(int count = 50)
    {
        var startDate = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            // Up and down oscillation to generate valid RSI movements
            price += (i % 2 == 0 ? 2m : -1m);
            candles.Add(new CoreCandleData(
                startDate.AddDays(i),
                price,
                price + 1.5m,
                price - 1.5m,
                price,
                1000L));
        }
        return candles;
    }

    [Fact]
    public void CalculateIndicators_WithOffChartSourceIndicator_ResolvesAndCalculatesChain()
    {
        // Arrange
        // Register an off-chart RSI source indicator
        var sourceRsi = new CoreIndicatorSettings
        {
            Id = "Source_RSI_14",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "Registered RSI (14)",
            IsEnabled = true,
            IsOverlay = false,
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };
        _sourceIndicatorService.SaveSourceIndicatorAsync(sourceRsi).GetAwaiter().GetResult();

        // Consumer: SMA with SourceIndicatorId set to the registered RSI
        var consumerSma = new CoreIndicatorSettings
        {
            Id = "SMA_Over_RSI",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA (5) of RSI",
            IsEnabled = true,
            IsOverlay = false,
            SourceIndicatorId = "Source_RSI_14",
            ParameterObject = new CoreSmaParameter { Period = 5 }
        };

        var candles = CreateTestCandles(50);
        // Note: Only consumerSma is passed to the pipeline (sourceRsi is NOT in the chart settings)
        var chartSettings = new List<CoreIndicatorSettings> { consumerSma };

        // Act
        var results = _pipelineService.CalculateIndicators(candles, chartSettings);

        // Assert
        Assert.NotNull(results);
        // 1. Off-chart source indicator must have been computed
        Assert.True(results.ContainsKey("Source_RSI_14"), "Registered Source Indicator should be resolved and computed.");
        var rsiResult = results["Source_RSI_14"];
        Assert.True(rsiResult.IsSuccessful);
        Assert.NotEmpty(rsiResult.MainValues);

        // 2. Consumer SMA must have been computed using RSI values
        Assert.True(results.ContainsKey("SMA_Over_RSI"));
        var smaResult = results["SMA_Over_RSI"];
        Assert.True(smaResult.IsSuccessful);
        Assert.NotEmpty(smaResult.MainValues);

        // Verify values are in RSI scale (0-100), not raw candle price scale (~100-150)
        var nonNullSma = smaResult.MainValues.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        Assert.NotEmpty(nonNullSma);
        Assert.All(nonNullSma, v => Assert.InRange(v, 0m, 100m));
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_WithOffChartSourceIndicator_ResolvesAndCalculatesChain()
    {
        // Arrange
        var sourceRsi = new CoreIndicatorSettings
        {
            Id = "Source_RSI_Async",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "Registered RSI Async",
            IsEnabled = true,
            IsOverlay = false,
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };
        await _sourceIndicatorService.SaveSourceIndicatorAsync(sourceRsi);

        var consumerSma = new CoreIndicatorSettings
        {
            Id = "SMA_Async_RSI",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA Async of RSI",
            IsEnabled = true,
            IsOverlay = false,
            SourceIndicatorId = "Source_RSI_Async",
            ParameterObject = new CoreSmaParameter { Period = 5 }
        };

        var candles = CreateTestCandles(50);
        var chartSettings = new List<CoreIndicatorSettings> { consumerSma };

        // Act
        var results = await _pipelineService.CalculateIndicatorsAsync(candles, chartSettings);

        // Assert
        Assert.NotNull(results);
        Assert.True(results.ContainsKey("Source_RSI_Async"));
        Assert.True(results.ContainsKey("SMA_Async_RSI"));
        var smaResult = results["SMA_Async_RSI"];
        Assert.True(smaResult.IsSuccessful);

        var nonNullSma = smaResult.MainValues.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        Assert.NotEmpty(nonNullSma);
        Assert.All(nonNullSma, v => Assert.InRange(v, 0m, 100m));
    }

    [Fact]
    public void CalculateIndicators_WithMultiOutputSourceIndicator_ResolvesSelectedOutputSeries()
    {
        // Arrange
        // Register MACD with OutputSeriesName = "Signal"
        var sourceMacd = new CoreIndicatorSettings
        {
            Id = "Source_MACD_Signal",
            TypeEnum = IndicatorType.MACD,
            DisplayName = "Registered MACD Signal",
            IsEnabled = true,
            IsOverlay = false,
            OutputSeriesName = "Signal",
            ParameterObject = new CoreMacdParameter { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 }
        };
        _sourceIndicatorService.SaveSourceIndicatorAsync(sourceMacd).GetAwaiter().GetResult();

        // Consumer SMA referencing the MACD source indicator
        var consumerSma = new CoreIndicatorSettings
        {
            Id = "SMA_Over_MACD_Signal",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA (5) of MACD Signal",
            IsEnabled = true,
            IsOverlay = false,
            SourceIndicatorId = "Source_MACD_Signal",
            ParameterObject = new CoreSmaParameter { Period = 5 }
        };

        var candles = CreateTestCandles(60);
        var chartSettings = new List<CoreIndicatorSettings> { consumerSma };

        // Act
        var results = _pipelineService.CalculateIndicators(candles, chartSettings);

        // Assert
        Assert.NotNull(results);
        Assert.True(results.ContainsKey("Source_MACD_Signal"));
        var macdResult = results["Source_MACD_Signal"];
        Assert.True(macdResult.IsSuccessful);

        var signalSeries = macdResult.GetSeries("Signal");
        Assert.NotEmpty(signalSeries);

        Assert.True(results.ContainsKey("SMA_Over_MACD_Signal"));
        var smaResult = results["SMA_Over_MACD_Signal"];
        Assert.True(smaResult.IsSuccessful);
        Assert.NotEmpty(smaResult.MainValues);
    }

    [Fact]
    public void CalculateIndicators_WithUnresolvedSourceIndicatorId_FallsBackToPriceAndSetsWarning()
    {
        // Arrange: SourceIndicatorId references an id that was never registered (e.g. deleted from
        // the catalog after being selected). No SourceIndicatorService.SaveSourceIndicatorAsync call
        // is made for it, so the pipeline cannot resolve it.
        var consumerSma = new CoreIndicatorSettings
        {
            Id = "SMA_Over_Deleted_Source",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA (5)",
            IsEnabled = true,
            IsOverlay = false,
            SourceIndicatorId = "Nonexistent_Deleted_Source_Id",
            ParameterObject = new CoreSmaParameter { Period = 5 }
        };

        var candles = CreateTestCandles(50);
        var chartSettings = new List<CoreIndicatorSettings> { consumerSma };

        // Act
        var results = _pipelineService.CalculateIndicators(candles, chartSettings);

        // Assert: calculation still proceeds (falls back to the Price series) rather than failing outright...
        Assert.True(results.ContainsKey("SMA_Over_Deleted_Source"));
        var smaResult = results["SMA_Over_Deleted_Source"];
        Assert.True(smaResult.IsSuccessful);
        Assert.NotEmpty(smaResult.MainValues);

        // ...but the fallback is surfaced as a non-null warning on the setting itself, so the UI's
        // existing per-row warning icon (bound to CoreIndicatorSettings.ErrorMessage) can show it,
        // instead of silently substituting the raw Price series with no visible signal.
        Assert.False(string.IsNullOrEmpty(consumerSma.ErrorMessage));
        Assert.Contains("Base Indicator", consumerSma.ErrorMessage);
    }
}
