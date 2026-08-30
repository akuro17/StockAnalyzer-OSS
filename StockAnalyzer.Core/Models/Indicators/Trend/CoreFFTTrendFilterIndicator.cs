using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.FFTTrendFilter)]
public class CoreFFTTrendFilterIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = ChartConstants.FftTrendFilterDefaultWindowSize;
    public int NumHarmonics { get; set; } = ChartConstants.FftTrendFilterDefaultNumHarmonics;
    public override string Name => $"FFT Trend Filter ({WindowSize}, {NumHarmonics})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFFTTrendFilterParameter p)
        {
            WindowSize = p.WindowSize;
            NumHarmonics = p.NumHarmonics;
        }
    }

    public new IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
    {
        throw new NotSupportedException("FFT Trend Filter requires async execution via Python. Use CalculateAsync instead.");
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        throw new NotSupportedException("FFT Trend Filter requires async execution via Python. Use CalculateAsync instead.");
    }

    public override async Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        if (context.PythonService == null)
        {
            return IndicatorResult.Failure("Python processing requires IPythonService which is not configured.");
        }

        await context.PythonService.InitializeExternalProcessAsync();
        var pythonService = context.PythonService;

        _values.Clear();
        var candleList = candles.ToList();

        if (candleList.Count == 0)
            return IndicatorResult.Success(_values);

        try
        {
            var responseJson = await pythonService.ExecuteTransactionAsync(async () =>
            {
                await pythonService.SendCandlesAsync(candleList.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList());
                return await pythonService.CalculateFftTrendFilterAsync(WindowSize, NumHarmonics);
            });

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
            {
                var error = root.TryGetProperty("error", out var err) ? (err.GetString() ?? "Unknown python error") : "Unknown python error";
                return IndicatorResult.Failure(error);
            }

            if (root.TryGetProperty("result", out var resultElement) && resultElement.TryGetProperty("trend", out var trendArray))
            {
                foreach (var val in trendArray.EnumerateArray())
                {
                    _values.Add(val.ValueKind == JsonValueKind.Null ? null : val.GetDecimal());
                }

                return IndicatorResult.Success(_values);
            }

            return IndicatorResult.Failure("No result in response");
        }
        catch (PythonUnavailableException ex)
        {
            Debug.WriteLine($"[{Name}] Circuit breaker open - graceful degradation: {ex.Message}");
            return IndicatorResult.Failure(
                "Python service temporarily unavailable (circuit breaker open). This feature will auto-recover when the service is restored.");
        }
        catch (Exception ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }
    }
}
