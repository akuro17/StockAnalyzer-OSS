using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.AdaptiveRSI)]
public class CoreAdaptiveRsiIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.AdaptiveRsiDefaultWindowSize;
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiMaxPeriod;

    public override string Name => $"Adaptive RSI ({WindowSize}, {DefaultPeriod})";
    public override bool IsOverlay => false;

    public List<decimal?> DominantPeriod { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdaptiveRsiParameter p)
        {
            WindowSize = p.WindowSize;
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
        }
    }

    public new IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
    {
        throw new NotSupportedException("Adaptive RSI requires async execution via Python. Use CalculateAsync instead.");
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        throw new NotSupportedException("Adaptive RSI requires async execution via Python. Use CalculateAsync instead.");
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        DominantPeriod.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (dynamicPeriods != null)
        {
            DominantPeriod.AddRange(dynamicPeriods);
        }
        else
        {
            for (int i = 0; i < series.Count; i++) DominantPeriod.Add(null);
        }

        var rsiValues = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(
            series,
            dynamicPeriods,
            DefaultPeriod,
            MinPeriod,
            MaxPeriod);

        _values.AddRange(rsiValues);
        GenerateSignals(_values);

        return CreateAutomaticResult();
    }

    private void GenerateSignals(IReadOnlyList<decimal?> values)
    {
        decimal? prevRsi = null;
        foreach (var rsi in values)
        {
            if (prevRsi.HasValue && rsi.HasValue)
            {
                // Bullish: exit oversold (< 30)
                if (prevRsi <= 30m && rsi > 30m)
                {
                    BullishSignals.Add(1m);
                }
                else
                {
                    BullishSignals.Add(null);
                }

                // Bearish: exit overbought (> 70)
                if (prevRsi >= 70m && rsi < 70m)
                {
                    BearishSignals.Add(1m);
                }
                else
                {
                    BearishSignals.Add(null);
                }
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }

            prevRsi = rsi;
        }
    }

    public override async Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
    {
        _values.Clear();
        DominantPeriod.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        var candleList = candles.ToList();
        List<decimal?>? dynamicPeriods = null;

        if (context?.PythonService != null)
        {
            try
            {
                await context.PythonService.InitializeExternalProcessAsync();
                var pythonService = context.PythonService;

                var responseJson = await pythonService.ExecuteTransactionAsync(async () =>
                {
                    await pythonService.SendCandlesAsync(candleList.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList());
                    return await pythonService.CalculateFftCycleAsync(WindowSize);
                });

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var status) && status.GetString() == "ok"
                    && root.TryGetProperty("result", out var resultElement)
                    && resultElement.TryGetProperty("cycle", out var cycleArray))
                {
                    dynamicPeriods = new List<decimal?>(candleList.Count);
                    foreach (var val in cycleArray.EnumerateArray())
                    {
                        var cycleVal = val.ValueKind == JsonValueKind.Null ? (decimal?)null : val.GetDecimal();
                        dynamicPeriods.Add(cycleVal);
                        DominantPeriod.Add(cycleVal);
                    }
                }
            }
            catch (PythonUnavailableException ex)
            {
                Debug.WriteLine($"[{Name}] Circuit breaker open - graceful degradation to static RSI: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{Name}] Python calculation exception - falling back to static RSI: {ex.Message}");
            }
        }

        // If Python was unavailable or returned null/error, fill DominantPeriod with nulls
        if (DominantPeriod.Count == 0)
        {
            for (int i = 0; i < candleList.Count; i++)
            {
                DominantPeriod.Add(null);
            }
        }

        // Perform Adaptive Wilder RSI calculation
        var rsiValues = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(
            candleList,
            dynamicPeriods,
            DefaultPeriod,
            MinPeriod,
            MaxPeriod);

        _values.AddRange(rsiValues);
        GenerateSignals(_values);

        return CreateAutomaticResult();
    }
}
