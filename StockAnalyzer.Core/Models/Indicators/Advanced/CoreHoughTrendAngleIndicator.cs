using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Technical indicator that extracts the geometric angle (in degrees, -90 to +90)
/// of dominant trendlines detected via Hough Transform.
/// Positive values indicate upward support/trendlines; negative indicate downward resistance.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HoughTrendAngle)]
public class CoreHoughTrendAngleIndicator : CoreIndicatorBase
{
    public int Lookback { get; set; } = 100;
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public int MaxLines { get; set; } = 5;
    public HoughNormalizationMode Normalization { get; set; } = HoughNormalizationMode.MinMax;

    public override string Name => $"HoughTrendAngle({Lookback},{PivotWindow},{VoteThreshold})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHoughTrendAngleParameter p)
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
                var dominant = result.Lines[0];
                double angleDeg;
                if (dominant.IsVertical || double.IsInfinity(dominant.NormalizedSlope))
                {
                    angleDeg = dominant.NormalizedSlope >= 0 ? 90.0 : -90.0;
                }
                else if (double.IsNaN(dominant.NormalizedSlope))
                {
                    angleDeg = 0.0;
                }
                else
                {
                    // Convert normalized slope (change in ATR per bar) to angle in degrees
                    double angleRad = Math.Atan(dominant.NormalizedSlope);
                    angleDeg = (angleRad * 180.0) / Math.PI;
                }
                _values.Add((decimal)Math.Round(Math.Clamp(angleDeg, -90.0, 90.0), 2));
            }
            else
            {
                _values.Add(0m);
            }
        }

        return CreateAutomaticResult();
    }
}
