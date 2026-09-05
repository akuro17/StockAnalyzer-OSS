using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// IFFT Instantaneous Phase (Analytic Signal) indicator.
/// For each bar it builds the analytic signal of the trailing window via
/// <see cref="FftAnalyticSignal"/> (forward FFT, negative frequencies zeroed / positive
/// frequencies doubled, then inverse FFT) and reports the instantaneous phase of the most
/// recent bar. Pure C# (no Python dependency); causal (never repaints).
/// The instantaneous amplitude line lives in its own overlay indicator
/// (<c>IFFTInstantaneousAmplitude</c>).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.IFFTInstantaneousPhase)]
public class CoreIfftInstantaneousPhaseIndicator : CoreIndicatorBase
{
    private const double RadToDeg = 180.0 / Math.PI;
    private const double DegToRad = Math.PI / 180.0;
    private const double FullTurnDeg = 360.0;

    public int WindowSize { get; set; } = IndicatorDefaultConstants.IfftInstantaneousPhaseDefaultWindowSize;

    /// <summary>Phase-lead angle for <see cref="LeadSine"/> relative to <see cref="SineWave"/>. Default 45 deg (prior hardcoded behavior).</summary>
    public double LeadSineShiftDegrees { get; set; } = IndicatorDefaultConstants.IfftInstantaneousPhaseDefaultLeadSineShiftDegrees;

    /// <summary>
    /// Friendly Screener output name for the base "Main" series (instantaneous phase, 0-360 deg).
    /// Referenced by <c>ScreenerCatalogProvider.GetOutputSeriesNames</c> so both sites stay in
    /// sync; see the remark below for why this is injected there rather than exposed as a public
    /// alias property on this class.
    /// </summary>
    public const string ScreenerPhaseAngleOutputName = "PhaseAngle";

    public override string Name => $"IFFT Instantaneous Phase ({WindowSize})";
    public override bool IsOverlay => false;

    // Median (High+Low)/2 price, matching CoreFFTCycleIndicator's frequency-extraction input.
    public override PriceType PriceSource { get; set; } = PriceType.Median;

    // The base Values/"Main" series (instantaneous phase, 0-360 deg) intentionally has no public
    // alias property here (unlike CoreMacdIndicator's MacdLine => _values): any property literally
    // named something other than "Main" would create a SECOND, distinctly-named series in
    // CreateAutomaticResult's output that the IndicatorRenderer's Main-only chart-suppression guard
    // would not catch, reintroducing the 0-360 axis-pollution bug. Instead, its friendly Screener
    // display name ("PhaseAngle") is injected directly in ScreenerCatalogProvider.GetOutputSeriesNames,
    // which the value extractor resolves back to the actual "Main" series via its HasSeries fallback
    // (the same mechanism single-series indicators like SMA already rely on for their type-name entry).

    /// <summary>sin(phase), a bounded [-1, 1] oscillator tracking cycle position.</summary>
    public List<decimal?> SineWave { get; } = new();

    /// <summary>sin(phase + LeadSineShiftDegrees); crossings with <see cref="SineWave"/> mark cycle turns.</summary>
    public List<decimal?> LeadSine { get; } = new();

    /// <summary>
    /// Bar-to-bar instantaneous phase change (degrees), wrapped to the shortest angular
    /// difference in (-180, 180]. Null for the first bar and whenever either neighboring raw
    /// phase is unavailable (warmup).
    /// </summary>
    public List<decimal?> PhaseDelta { get; } = new();

    /// <summary>
    /// Estimated local cycle period (bars), <c>360 / PhaseDelta</c>. Null whenever
    /// <see cref="PhaseDelta"/> is null or non-positive (a non-positive step does not correspond
    /// to a forward-rotating cycle, so no meaningful period can be derived).
    /// </summary>
    public List<decimal?> LocalPeriod { get; } = new();

    /// <summary>
    /// Population standard deviation (degrees) of the trailing <see cref="WindowSize"/>
    /// <see cref="PhaseDelta"/> values. Lower values indicate a more stable (more likely
    /// genuinely cyclical) phase rotation; higher values indicate an unstable/trending regime.
    /// Null until <see cref="WindowSize"/> consecutive non-null PhaseDelta values are available.
    /// This is a diagnostic statistic, not a pre-thresholded validity flag -- apply a Screener
    /// condition against it to define what counts as "stable" for a given use case.
    /// </summary>
    public List<decimal?> PhaseStability { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreIfftInstantaneousPhaseParameter p)
        {
            WindowSize = p.WindowSize;
            LeadSineShiftDegrees = p.LeadSineShiftDegrees;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        SineWave.Clear();
        LeadSine.Clear();
        PhaseDelta.Clear();
        LocalPeriod.Clear();
        PhaseStability.Clear();

        var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceSource);
        int n = priceSeries.Count;

        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = (double)priceSeries[i];
        }

        var phase = new double[n];
        var envelope = new double[n];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, WindowSize, phase, envelope);

        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(phase[i]))
            {
                _values.Add(null);
                SineWave.Add(null);
                LeadSine.Add(null);
                continue;
            }

            double deg = (phase[i] * RadToDeg) % FullTurnDeg;
            if (deg < 0.0)
            {
                deg += FullTurnDeg;
            }

            _values.Add((decimal)deg);
            SineWave.Add((decimal)Math.Sin(phase[i]));
            LeadSine.Add((decimal)Math.Sin(phase[i] + LeadSineShiftDegrees * DegToRad));
        }

        var stabilityWindow = new Queue<double>();
        double stabilitySum = 0.0;
        double stabilitySumSq = 0.0;

        for (int i = 0; i < n; i++)
        {
            double? deltaDeg = null;
            if (i > 0 && !double.IsNaN(phase[i]) && !double.IsNaN(phase[i - 1]))
            {
                double d = phase[i] - phase[i - 1];
                while (d <= -Math.PI) d += 2.0 * Math.PI;
                while (d > Math.PI) d -= 2.0 * Math.PI;
                deltaDeg = d * RadToDeg;
            }

            PhaseDelta.Add(deltaDeg.HasValue ? (decimal)deltaDeg.Value : (decimal?)null);
            LocalPeriod.Add(deltaDeg.HasValue && deltaDeg.Value > 0.0
                ? (decimal)(FullTurnDeg / deltaDeg.Value)
                : (decimal?)null);

            if (deltaDeg.HasValue)
            {
                stabilityWindow.Enqueue(deltaDeg.Value);
                stabilitySum += deltaDeg.Value;
                stabilitySumSq += deltaDeg.Value * deltaDeg.Value;
                if (stabilityWindow.Count > WindowSize)
                {
                    double removed = stabilityWindow.Dequeue();
                    stabilitySum -= removed;
                    stabilitySumSq -= removed * removed;
                }
            }
            else
            {
                stabilityWindow.Clear();
                stabilitySum = 0.0;
                stabilitySumSq = 0.0;
            }

            if (stabilityWindow.Count == WindowSize)
            {
                double mean = stabilitySum / WindowSize;
                double variance = Math.Max(0.0, (stabilitySumSq / WindowSize) - (mean * mean));
                PhaseStability.Add((decimal)Math.Sqrt(variance));
            }
            else
            {
                PhaseStability.Add(null);
            }
        }

        return CreateAutomaticResult();
    }
}
