using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.DivergenceCross;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks for divergence or moving average crosses from a specified source indicator.
/// </summary>
public class DivergenceCrossCondition : IScreeningCondition
{
    private readonly CoreDivergenceCrossParameter _parameter;
    private readonly IndicatorType _sourceIndicatorType;
    private readonly SignalType _targetSignal;
    private readonly int _lookbackBars;

    public DivergenceCrossCondition(
        IndicatorType sourceIndicatorType = IndicatorType.RSI,
        SignalType targetSignal = SignalType.RegularBullishDivergence,
        CoreDivergenceCrossParameter? parameter = null,
        int lookbackBars = 1)
    {
        _sourceIndicatorType = sourceIndicatorType;
        _targetSignal = targetSignal;
        _parameter = parameter ?? new CoreDivergenceCrossParameter();
        _lookbackBars = Math.Max(1, lookbackBars);
    }

    public override string ToString()
    {
        var sourceName = _sourceIndicatorType.ToString();
        var signalName = _targetSignal.ToString();
        return $"{sourceName} - {signalName} (Lookback: {_lookbackBars})";
    }

    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        return IsMetAsync(candles).AsTask().GetAwaiter().GetResult();
    }

    public async System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < _parameter.LongMaPeriod)
            return false;

        // 1. Calculate Source Indicator
        var sourceIndicator = IndicatorFactory.Default.Create(_sourceIndicatorType);
        if (sourceIndicator == null) return false;

        // Note: For screening, we use default parameters for the source indicator to keep it simple,
        // unless we want to expand this to accept a pre-configured indicator instance.
        var context = new CoreExecutionContext(null);

        // CoreIndicator expects IEnumerable<CoreCandleData>
        var coreCandles = candles.Select(c => new CoreCandleData(
            c.Time, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        // Calculate async without blocking the ThreadPool
        var result = await sourceIndicator.CalculateAsync(coreCandles, context);

        if (!result.IsSuccessful || result.Count == 0)
            return false;

        // Get the main series
        var sourceValues = result.MainValues;
        
        // Ensure sizes match
        if (sourceValues.Count != candles.Count)
            return false;

        // 2. Extract arrays for Divergence & Cross Detectors
        var highs = candles.Select(c => c.High).ToList();
        var lows = candles.Select(c => c.Low).ToList();

        // 3. Check if the target signal appeared within the lookback window
        int startIndex = candles.Count - _lookbackBars;
        if (startIndex < 0) startIndex = 0;

        // Check Divergences
        if (IsDivergenceSignal(_targetSignal))
        {
            var divergences = DivergenceCrossDetector.DetectDivergences(
                highs, lows, sourceValues, _parameter.PivotLookback);

            return divergences.Any(d => 
                d.Type == _targetSignal && 
                d.PriceEndIndex >= startIndex);
        }

        // Check Crosses
        if (IsCrossSignal(_targetSignal))
        {
            // The user requested to use the RAW source values, not SMA.
            // If the indicator returns multiple series (like MACD: MACD Line and Signal Line), cross them.
            // If it returns only one (like RSI), cross the value against the ShortMaPeriod parameter treated as a threshold line.
            
            IReadOnlyList<decimal?> series1 = sourceValues;
            IReadOnlyList<decimal?> series2;

            if (result.SeriesNames.Count() > 1)
            {
                // Has multiple lines, use the second line as the signal line
                var secondSeriesName = result.SeriesNames.ElementAt(1);
                series2 = result.GetSeries(secondSeriesName);
            }
            else
            {
                // Single line indicator (e.g., RSI). Treat ShortMaPeriod as the threshold line to cross.
                decimal threshold = _parameter.ShortMaPeriod;
                series2 = Enumerable.Repeat((decimal?)threshold, sourceValues.Count).ToList();
            }

            var crosses = DivergenceCrossDetector.DetectCrosses(series1, series2);

            return crosses.Any(c => 
                c.Type == _targetSignal && 
                c.CrossIndex >= startIndex);
        }

        return false;
    }

    private bool IsDivergenceSignal(SignalType type)
    {
        return type == SignalType.RegularBullishDivergence ||
               type == SignalType.RegularBearishDivergence ||
               type == SignalType.HiddenBullishDivergence ||
               type == SignalType.HiddenBearishDivergence;
    }

    private bool IsCrossSignal(SignalType type)
    {
        return type == SignalType.GoldenCross ||
               type == SignalType.DeadCross;
    }
}
