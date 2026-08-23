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
        int overlap = Period * ChartConstants.EmaConvergenceMultiplier; 
        int calcStart = Math.Max(0, start - overlap);

        decimal avgGain = 0;
        decimal avgLoss = 0;
        bool isInitialized = false;

        for (int i = calcStart; i < end; i++)
        {
            if (i == 0)
            {
                if (i >= start) results.Add(null);
                continue;
            }

            decimal priceCurr = series[i] ?? 0m;
            decimal pricePrev = series[i - 1] ?? 0m;
            decimal change = priceCurr - pricePrev;
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0;

            int localStep = i - calcStart;
            if (calcStart == 0) localStep = i - 1;

            decimal? currentRsi = null;

            if (localStep < Period)
            {
                avgGain += gain;
                avgLoss += loss;
                currentRsi = null;
            }
            else if (localStep == Period)
            {
                avgGain = (avgGain + gain) / Period;
                avgLoss = (avgLoss + loss) / Period;
                decimal rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
                currentRsi = 100 - (100 / (1 + rs));
                isInitialized = true;
            }
            else
            {
                avgGain = (avgGain * (Period - 1) + gain) / Period;
                avgLoss = (avgLoss * (Period - 1) + loss) / Period;
                
                decimal rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
                currentRsi = 100 - (100 / (1 + rs));
            }

            if (i >= start)
            {
                results.Add(currentRsi);
            }
        }

        return results;
    }
}
