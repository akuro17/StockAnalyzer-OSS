using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Technical indicator that evaluates structural trend line strength using Hough Transform.
/// Outputs an oscillator value (0 to 100) reflecting the prominence and consensus of detected trendlines.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HoughTrendStrength)]
public class CoreHoughTrendStrengthIndicator : CoreIndicatorBase
{
    public int Lookback { get; set; } = 100;
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public int MaxLines { get; set; } = 5;
    public HoughNormalizationMode Normalization { get; set; } = HoughNormalizationMode.MinMax;

    public override string Name => $"HoughTrendStrength({Lookback},{PivotWindow},{VoteThreshold})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHoughTrendStrengthParameter p)
        {
            Lookback = p.Lookback;
            PivotWindow = p.PivotWindow;
            VoteThreshold = p.VoteThreshold;
            MaxLines = p.MaxLines;
            Normalization = p.Normalization;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Failure("No candle data provided.");
        }

        int count = candles.Count;
        _values.Clear();
        if (_values.Capacity < count)
        {
            _values.Capacity = count;
        }

        // Convert CoreCandleData to CandleData struct for engine calculation
        var candleStructs = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            var c = candles[i];
            candleStructs[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
        }

        int warmup = Math.Max(Lookback - 1, (PivotWindow * 2) + 2);
        var subCandles = new CandleData[Lookback];

        for (int i = 0; i < count; i++)
        {
            if (i < warmup)
            {
                _values.Add(null);
                continue;
            }

            // Rolling window calculation
            int windowStart = i - Lookback + 1;
            Array.Copy(candleStructs, windowStart, subCandles, 0, Lookback);

            var result = HoughTransformEngine.DetectLinesFromCandles(
                subCandles,
                lookback: Lookback,
                pivotWindow: PivotWindow,
                voteThreshold: VoteThreshold,
                maxLines: MaxLines,
                normalization: Normalization);

            if (result.Lines.Count > 0)
            {
                // Dominant line strength [0, 100]
                var dominant = result.Lines[0];
                double score = Math.Clamp(dominant.Strength, 0.0, 100.0);
                _values.Add((decimal)Math.Round(score, 2));
            }
            else
            {
                _values.Add(0m);
            }
        }

        return CreateAutomaticResult();
    }
}
