using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Advanced
{
    [StockAnalyzerIndicator(IndicatorType.StructuralDtw)]
    public class CoreStructuralDtwIndicator : CoreIndicatorBase
    {
        public override string Name => "Structural DTW Oscillator";
        public string ShortName => $"DTW_Osc({Period},{Lag})";

        public int Period { get; set; } = 14;
        public int Lag { get; set; } = 14;
        public int WarpingRadius { get; set; } = IndicatorDefaultConstants.DtwDefaultWarpingRadius;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreStructuralDtwParameter p)
            {
                Period = p.Period;
                Lag = p.Lag;
                WarpingRadius = p.WarpingRadius;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            throw new NotSupportedException("Structural DTW requires async execution. Use CalculateAsync instead.");
        }

        public override async Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
        {
            if (context.PythonService == null)
            {
                return IndicatorResult.Failure("Python processing requires IPythonService which is not configured.");
            }

            if (candles == null || !candles.Any())
            {
                return IndicatorResult.Failure("No data");
            }

            var candleList = candles.ToList();

            if (candleList.Count < Period + Lag)
            {
                // Not enough data, return array of nulls
                var emptyObj = new Dictionary<string, IReadOnlyList<decimal?>>
                {
                    { IndicatorResult.MainSeriesName, new decimal?[candleList.Count] }
                };
                return IndicatorResult.Success(emptyObj);
            }

            try
            {
                await context.PythonService.InitializeExternalProcessAsync();
                var pythonService = context.PythonService;

                var responseJson = await pythonService.ExecuteTransactionAsync(async () =>
                {
                    // Send Data
                    await pythonService.SendCandlesAsync(candleList.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList());

                    // Invoke Analysis
                    return await pythonService.CalculateStructuralDtwOscillatorAsync(Period, Lag, WarpingRadius);
                });

                // Parse Result
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
