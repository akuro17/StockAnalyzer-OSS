using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.ClothoidOscillator)]
public class CoreClothoidOscillatorIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = IndicatorDefaultConstants.ClothoidOscillatorDefaultPeriod;
    public double DecayAlpha { get; set; } = IndicatorDefaultConstants.ClothoidOscillatorDefaultDecayAlpha;
    public int SignalPeriod { get; set; } = IndicatorDefaultConstants.ClothoidOscillatorDefaultSignalPeriod;

    public override string Name => $"ClothoidOscillator ({Period}, {DecayAlpha:F1}, {SignalPeriod})";
    public override bool IsOverlay => false;

    public const string MainSeriesName = IndicatorResult.MainSeriesName;
    public const string SignalSeriesName = "Signal";
    public const string HistogramSeriesName = "Histogram";

    public List<decimal?> Signal { get; } = new();
    public List<decimal?> Histogram { get; } = new();

    /// <summary>
    /// Raw arc-length curvature rate derivative (c = d(kappa)/ds) before scaling by 100 and clamping to [-100, 100].
    /// Diagnostic series for quantitative analysis, screener, and DataWindow.
    /// Excluded from chart rendering and auto-range via IndicatorChartSuppression.
    /// </summary>
    public List<decimal?> RawCurvatureRate { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreClothoidOscillatorParameter p)
        {
            Period = p.Period;
            DecayAlpha = p.DecayAlpha;
            SignalPeriod = p.SignalPeriod;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        Signal.Clear();
        Histogram.Clear();
        RawCurvatureRate.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = series.Count;
        int period = Math.Max(4, Period);
        int signalPeriod = Math.Max(1, SignalPeriod);

        // Preallocate output collections
        if (_values.Capacity < count) _values.Capacity = count;
        if (Signal.Capacity < count) Signal.Capacity = count;
        if (Histogram.Capacity < count) Histogram.Capacity = count;
        if (RawCurvatureRate.Capacity < count) RawCurvatureRate.Capacity = count;
        if (BullishSignals.Capacity < count) BullishSignals.Capacity = count;
        if (BearishSignals.Capacity < count) BearishSignals.Capacity = count;

        decimal[]? rentedWindow = null;
        Span<decimal> window = period <= MathBufferLimits.StackAllocThreshold
            ? stackalloc decimal[period]
            : (rentedWindow = ArrayPool<decimal>.Shared.Rent(period)).AsSpan(0, period);

        try
        {
            // Step 1: Calculate raw Curvature Rate Oscillator line
            for (int i = 0; i < count; i++)
            {
                if (i < period - 1)
                {
                    _values.Add(null);
                    RawCurvatureRate.Add(null);
                    continue;
                }

                bool hasNull = false;
                for (int k = 0; k < period; k++)
                {
                    var val = series[i - period + 1 + k];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    window[k] = val.Value;
                }

                if (hasNull)
                {
                    _values.Add(null);
                    RawCurvatureRate.Add(null);
                    continue;
                }

                if (ClothoidMath.SolveCubicWlsKinematics(window, DecayAlpha, out double v, out double a, out double jerk))
                {
                    double c = ClothoidMath.CalculateCurvatureRateFromKinematics(v, a, jerk);
                    RawCurvatureRate.Add((decimal)c);
                    double scaledC = Math.Clamp(c * 100.0, -100.0, 100.0);
                    _values.Add((decimal)scaledC);
                }
                else
                {
                    _values.Add(null);
                    RawCurvatureRate.Add(null);
                }
            }

            // Step 2: Compute Signal Line (EMA of _values) & Histogram
            if (signalPeriod <= 1)
            {
                for (int i = 0; i < count; i++)
                {
                    var val = _values[i];
                    Signal.Add(val);
                    Histogram.Add(val.HasValue ? 0.0m : null);
                    BullishSignals.Add(null);
                    BearishSignals.Add(null);
                }
            }
            else
            {
                decimal signalMultiplier = 2.0m / (signalPeriod + 1);
                decimal? currentSignal = null;
                decimal? prevHist = null;
                var seedBuffer = new List<decimal>(signalPeriod);

                for (int i = 0; i < count; i++)
                {
                    var val = _values[i];
                    if (!val.HasValue)
                    {
                        Signal.Add(null);
                        Histogram.Add(null);
                        BullishSignals.Add(null);
                        BearishSignals.Add(null);
                        seedBuffer.Clear();
                        currentSignal = null;
                        prevHist = null;
                        continue;
                    }

                    if (!currentSignal.HasValue)
                    {
                        seedBuffer.Add(val.Value);
                        if (seedBuffer.Count == signalPeriod)
                        {
                            decimal sum = 0.0m;
                            for (int k = 0; k < signalPeriod; k++) sum += seedBuffer[k];
                            currentSignal = sum / signalPeriod;
                            Signal.Add(currentSignal);
                            decimal hist = val.Value - currentSignal.Value;
                            Histogram.Add(hist);

                            BullishSignals.Add(null);
                            BearishSignals.Add(null);
                            prevHist = hist;
                        }
                        else
                        {
                            Signal.Add(null);
                            Histogram.Add(null);
                            BullishSignals.Add(null);
                            BearishSignals.Add(null);
                        }
                    }
                    else
                    {
                        currentSignal = (val.Value - currentSignal.Value) * signalMultiplier + currentSignal.Value;
                        Signal.Add(currentSignal);
                        decimal hist = val.Value - currentSignal.Value;
                        Histogram.Add(hist);

                        if (prevHist.HasValue)
                        {
                            if (prevHist <= 0 && hist > 0) BullishSignals.Add(1m);
                            else BullishSignals.Add(null);

                            if (prevHist >= 0 && hist < 0) BearishSignals.Add(1m);
                            else BearishSignals.Add(null);
                        }
                        else
                        {
                            BullishSignals.Add(null);
                            BearishSignals.Add(null);
                        }
                        prevHist = hist;
                    }
                }
            }

            return CreateAutomaticResult();
        }
        finally
        {
            if (rentedWindow != null)
            {
                ArrayPool<decimal>.Shared.Return(rentedWindow);
            }
        }
    }
}
