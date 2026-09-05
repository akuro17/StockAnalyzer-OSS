using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.MESA)]
    public class CoreMesaIndicator : CoreIndicatorBase
    {
        public decimal FastLimit { get; set; } = 0.5m;
        public decimal SlowLimit { get; set; } = 0.05m;
        public override string Name => "MESA";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMesaParameter p)
            {
                FastLimit = p.FastLimit;
                SlowLimit = p.SlowLimit;
            }
        }
        
        // This list will hold the calculated FAMA values. The base class holds values for MAMA.
        public List<decimal?> Fama { get; } = new();

        public new IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
        {
            throw new NotSupportedException("MESA requires async execution via Python. Use CalculateAsync instead.");
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
             throw new NotSupportedException("MESA requires async execution via Python. Use CalculateAsync instead.");
        }

        public override async Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
        {
            if (context.PythonService == null)
            {
                return IndicatorResult.Failure("Python processing requires IPythonService which is not configured.");
            }
            
            await context.PythonService.InitializeExternalProcessAsync();
            var pythonService = context.PythonService;

            Fama.Clear();
            _values.Clear();
            var candleList = candles.ToList();

            if (candleList.Count == 0)
                return IndicatorResult.Success(_values);

            try
            {
                var responseJson = await pythonService.ExecuteTransactionAsync(async () =>
                {
                    // 1. Send Data (Stateful: overwrites previous data on server)
                    await pythonService.SendCandlesAsync(candleList.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList());

                    // 2. Invoke Analysis
                    return await pythonService.CalculateMesaAsync(FastLimit, SlowLimit);
                });
                
                // 3. Parse Result
                // Expected format: { "status": "ok", "result": { "mama": [...], "fama": [...] } }
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
                {
                    var error = root.TryGetProperty("error", out var err) ? (err.GetString() ?? "Unknown python error") : "Unknown python error";
                    return IndicatorResult.Failure(error);
                }

                if (root.TryGetProperty("result", out var resultElement))
                {
                    if (resultElement.TryGetProperty("mama", out var mamaArray))
                    {
                        foreach (var val in mamaArray.EnumerateArray())
                        {
                            _values.Add(val.ValueKind == JsonValueKind.Null ? null : val.GetDecimal());
                        }
                    }

                    if (resultElement.TryGetProperty("fama", out var famaArray))
                    {
                        foreach (var val in famaArray.EnumerateArray())
                        {
                            Fama.Add(val.ValueKind == JsonValueKind.Null ? null : val.GetDecimal());
                        }
                    }

                    var series = new Dictionary<string, IReadOnlyList<decimal?>>
                    {
                        { IndicatorResult.MainSeriesName, _values.ToList() },
                        { "Fama", Fama.ToList() }
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
