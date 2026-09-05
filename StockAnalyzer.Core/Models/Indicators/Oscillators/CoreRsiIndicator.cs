using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.RSI)]
public class CoreRsiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;

    public override string Name => $"RSI ({Period})";
    public override bool IsOverlay => false;

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRsiParameter rsiParam)
        {
            Period = rsiParam.Period;
        }
        else if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series.Count < 2) return IndicatorResult.Success(_values);

        if (dynamicPeriods != null && dynamicPeriods.Count > 0)
        {
            var dynamicResults = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(series, dynamicPeriods, Period);
            _values.Clear();
            _values.AddRange(dynamicResults);
            GenerateSignals(_values);
            return CreateAutomaticResult();
        }

        var results = CalculateParallel(series, CalculateSegment);

        _values.Clear();
        _values.AddRange(results);
        GenerateSignals(_values);

        return CreateAutomaticResult();
    }

    private void GenerateSignals(IReadOnlyList<decimal?> results)
    {
        BullishSignals.Clear();
        BearishSignals.Clear();
        decimal? prevRsi = null;
        foreach (var rsi in results)
        {
            if (prevRsi.HasValue && rsi.HasValue)
            {
                // Bullish: exit oversold (< 30)
                if (prevRsi <= 30m && rsi > 30m) BullishSignals.Add(1m);
                else BullishSignals.Add(null);

                // Bearish: exit overbought (> 70)
                if (prevRsi >= 70m && rsi < 70m) BearishSignals.Add(1m);
                else BearishSignals.Add(null);
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }
            prevRsi = rsi;
        }
    }

    private List<decimal?> CalculateSegment(IReadOnlyList<decimal?> series, int start, int end)
    {
        var results = new List<decimal?>(end - start);
        int overlap = Period * IndicatorDefaultConstants.EmaConvergenceMultiplier; 
        int calcStart = Math.Max(0, start - overlap);

        decimal avgGain = 0;
        decimal avgLoss = 0;
        int validChanges = 0;
        bool isInitialized = false;

        for (int i = calcStart; i < end; i++)
        {
            if (i == 0 || !series[i].HasValue || !series[i - 1].HasValue)
            {
                if (!isInitialized)
                {
                    avgGain = 0;
                    avgLoss = 0;
                    validChanges = 0;
                }
                if (i >= start) results.Add(null);
                continue;
            }

            decimal priceCurr = series[i]!.Value;
            decimal pricePrev = series[i - 1]!.Value;
            decimal change = priceCurr - pricePrev;
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0;

            decimal? currentRsi = null;

            if (!isInitialized)
            {
                avgGain += gain;
                avgLoss += loss;
                validChanges++;

                if (validChanges == Period)
                {
                    avgGain /= Period;
                    avgLoss /= Period;
                    currentRsi = CalculateRsi(avgGain, avgLoss);
                    isInitialized = true;
                }
            }
            else
            {
                avgGain = (avgGain * (Period - 1) + gain) / Period;
                avgLoss = (avgLoss * (Period - 1) + loss) / Period;
                currentRsi = CalculateRsi(avgGain, avgLoss);
            }

            if (i >= start)
            {
                results.Add(currentRsi);
            }
        }

        return results;
    }

    private static decimal CalculateRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m)
        {
            return avgGain == 0m ? 50.0m : 100.0m;
        }

        decimal rs = avgGain / avgLoss;
        return 100.0m - (100.0m / (1.0m + rs));
    }
}

