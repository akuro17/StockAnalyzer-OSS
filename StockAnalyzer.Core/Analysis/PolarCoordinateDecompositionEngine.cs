using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Polar Coordinate Transform Decomposition Engine (single analytic signal).
///
/// Constructs the analytic signal of a price series with the Ehlers Canonical Homodyne
/// Discriminator (<see cref="HilbertDecompositionEngine"/>, reused unchanged) and expresses the
/// Cartesian in-phase / quadrature pair in polar form: instantaneous amplitude (envelope),
/// instantaneous phase in [0, 360) degrees, unwrapped phase,
/// Homodyne-derived cycle angular frequency, the measured bar-to-bar phase-difference angular
/// velocity and its deviation from the cycle rate, and instantaneous / dominant cycle period in bars.
///
/// This is a thin façade: it calls <see cref="HilbertDecompositionEngine.Decompose"/> and only
/// adds <see cref="PolarSampleResult.PhaseDegrees"/>, <see cref="PolarSampleResult.CycleAngularFrequency"/>,
/// <see cref="PolarSampleResult.PhaseAngularVelocity"/>, <see cref="PolarSampleResult.PhaseFrequencyError"/>
/// and the split validity flags
/// (<see cref="PolarSampleResult.IsPhaseValid"/> / <see cref="PolarSampleResult.IsAmplitudeValid"/> /
/// <see cref="PolarSampleResult.IsFrequencyValid"/> / <see cref="PolarSampleResult.IsCycleFrequencyValid"/>)
/// on top of the pass-through fields. The only
/// heap allocation is the returned <see cref="PolarSampleResult"/> array; there is zero per-bar
/// allocation.
///
/// The <c>atan2(-Q, I)</c> Ehlers sign convention (analytic signal z = I - jQ, phase advancing) is
/// inherited unchanged from <see cref="HilbertDecompositionEngine"/> and is never recomputed here;
/// see <see cref="PolarSampleResult.PhaseRadians"/> for the rationale.
/// </summary>
public static class PolarCoordinateDecompositionEngine
{
    /// <summary>
    /// Convenience overload: runs the Ehlers decomposition on <paramref name="prices"/> and then
    /// projects the result into polar form. Equivalent to
    /// <c>Decompose(HilbertDecompositionEngine.Decompose(prices, parameters))</c>. Callers that
    /// already hold a <see cref="HilbertDecompositionResult"/> should use the other overload to avoid
    /// recomputing the analytic signal.
    /// </summary>
    /// <param name="prices">Chronological price series (e.g. Typical or Median price).</param>
    /// <param name="parameters">
    /// Hilbert decomposition configuration. Reused verbatim (no new parameter type is introduced).
    /// </param>
    /// <returns>Structured time-series result of the polar decomposition.</returns>
    public static PolarDecompositionResult Decompose(
        ReadOnlySpan<decimal> prices,
        HilbertDecompositionParameters? parameters = null)
    {
        parameters ??= new HilbertDecompositionParameters();
        parameters.Validate();

        HilbertDecompositionResult hilbert = HilbertDecompositionEngine.Decompose(prices, parameters);
        return Decompose(hilbert);
    }

    /// <summary>
    /// Core path: projects an already-computed <see cref="HilbertDecompositionResult"/> into per-bar
    /// polar-coordinate metrics. The analytic signal is not recomputed; this overload only adds the
    /// <see cref="PolarSampleResult.PhaseDegrees"/>, <see cref="PolarSampleResult.CycleAngularFrequency"/>,
    /// <see cref="PolarSampleResult.PhaseAngularVelocity"/>, <see cref="PolarSampleResult.PhaseFrequencyError"/>
    /// and the split validity flags
    /// (<see cref="PolarSampleResult.IsPhaseValid"/> / <see cref="PolarSampleResult.IsAmplitudeValid"/> /
    /// <see cref="PolarSampleResult.IsFrequencyValid"/> / <see cref="PolarSampleResult.IsCycleFrequencyValid"/>)
    /// quantities. The Hilbert parameters are read
    /// back from <see cref="HilbertDecompositionResult.Parameters"/>.
    /// </summary>
    /// <param name="hilbert">A decomposition produced by <see cref="HilbertDecompositionEngine.Decompose"/>.</param>
    /// <returns>Structured time-series result of the polar decomposition.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hilbert"/> is <see langword="null"/>.</exception>
    public static PolarDecompositionResult Decompose(HilbertDecompositionResult hilbert)
    {
        ArgumentNullException.ThrowIfNull(hilbert);

        HilbertDecompositionParameters parameters = hilbert.Parameters;

        int n = hilbert.Count;
        var samples = new PolarSampleResult[n];

        for (int t = 0; t < n; t++)
        {
            HilbertSampleResult s = hilbert[t];

            // Cartesian -> polar additions (everything else is a verbatim pass-through).
            double phaseDeg = s.PhaseDeg % 360.0;
            if (phaseDeg < 0.0)
            {
                phaseDeg += 360.0;
            }

            double cycleAngularFrequency = s.InstantaneousPeriod > 0m
                ? MathConstants.TwoPi / (double)s.InstantaneousPeriod
                : double.NaN;

            // Measured phase-progression rate: PhaseDeltaDeg (unwrapped-phase bar-to-bar delta,
            // already shortest-angular-path) converted deg -> rad/bar, passed through unchanged.
            double phaseAngularVelocity = s.PhaseDeltaDeg * MathConstants.DegToRad;
            double phaseFrequencyError = double.IsNaN(cycleAngularFrequency) || double.IsNaN(phaseAngularVelocity)
                ? double.NaN
                : Math.Abs(cycleAngularFrequency - phaseAngularVelocity);

            // Split validity: the phase can be latched (stale) below the micro-amplitude threshold
            // while the amplitude is still a valid low-energy reading. isCycleFrequencyValid holds
            // whenever a finite cycle rate is available -- a positive period is exactly the
            // condition for cycleAngularFrequency to be finite, so no separate NaN check is needed.
            // isFrequencyValid additionally excludes trend mode and requires the period to clear the
            // effective Nyquist floor MinimumCyclePeriod.
            bool isAmplitudeValid = !s.IsWarmup;
            bool isPhaseValid = !s.IsWarmup && s.Amplitude >= parameters.MicroAmplitudeThreshold;
            bool isCycleFrequencyValid = isPhaseValid && s.InstantaneousPeriod > 0m;
            bool isFrequencyValid = isCycleFrequencyValid
                && !s.TrendMode
                && s.InstantaneousPeriod >= (decimal)parameters.MinimumCyclePeriod;

            samples[t] = new PolarSampleResult(
                s.InPhase,
                s.Quadrature,
                s.Amplitude,
                s.Power,
                s.NormalizedInPhase,
                s.NormalizedQuadrature,
                s.PhaseRad,
                phaseDeg,
                s.UnwrappedPhaseDeg,
                cycleAngularFrequency,
                phaseAngularVelocity,
                phaseFrequencyError,
                s.InstantaneousPeriod,
                s.DominantCycle,
                s.CycleStability,
                s.IsWarmup,
                isPhaseValid,
                isAmplitudeValid,
                isFrequencyValid,
                isCycleFrequencyValid);
        }

        return new PolarDecompositionResult(samples, hilbert.WarmupBars, parameters);
    }
}
