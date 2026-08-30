using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    public class CoreEgarchIndicator : CoreIndicatorBase
    {
        public override string Name => "EGARCH";
        public string ShortName => $"EGARCH({P},{Q})";

        public int P { get; set; } = 1;
        public int Q { get; set; } = 1;

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
             // Synchronous calculation is not supported due to IPC.
             // Callers must use the Async version via casting or new interface.
             throw new NotSupportedException("EGARCH requires async execution. Use CalculateAsync instead.");
        }

        public override async Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
        {
            if (context.PythonService == null)
            {
                return IndicatorResult.Failure("Python processing requires IPythonService which is not configured.");
            }
            
            await context.PythonService.InitializeExternalProcessAsync();
            var pythonService = context.PythonService;

            if (candles == null || !candles.Any())
            {
                return IndicatorResult.Failure("No data");
            }

            var candleList = candles.ToList();

            try
            {
                var responseJson = await pythonService.ExecuteTransactionAsync(async () =>
                {
                    // 1. Send Data (Stateful: overwrites previous data on server)
                    // In production, we might check if data hash changed before sending to optimize speed.
                    // For now, robustly send it.
                    await pythonService.SendCandlesAsync(candleList.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList());

                    // 2. Invoke Analysis
                    return await pythonService.CalculateEgarchAsync(P, Q);
                });
                
                // 3. Parse Result
                // Expected format: { "status": "ok", "result": [null, null, ..., 1.2, 1.3], "offset": 5 }
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
                {
                    var error = root.TryGetProperty("error", out var err) ? (err.GetString() ?? "Unknown python error") : "Unknown python error";
                    return IndicatorResult.Failure(error);
                }

                if (root.TryGetProperty("result", out var resultElement))
                {
                    var values = new List<decimal?>();
                    foreach (var val in resultElement.EnumerateArray())
                    {
                        if (val.ValueKind == JsonValueKind.Null)
                        {
                            values.Add(null);
                        }
                        else
                        {
                            values.Add(val.GetDecimal());
                        }
                    }

                    // Result should be same length as input if server padded it, 
                    // or potentially shorter depending on offset logic.
                    // Server implementation attempt to match length.
                    
                    var series = new Dictionary<string, IReadOnlyList<decimal?>>
                    {
                        { IndicatorResult.MainSeriesName, values }
                    };
                    return IndicatorResult.Success(series);
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
}
