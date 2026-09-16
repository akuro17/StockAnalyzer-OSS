using System;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Per-bar polar-coordinate decomposition metrics produced by
/// <see cref="PolarCoordinateDecompositionEngine"/>.
///
/// Every field except <see cref="PhaseDegrees"/>, <see cref="CycleAngularFrequency"/>,
/// <see cref="PhaseAngularVelocity"/>,
/// <see cref="PhaseFrequencyError"/>, <see cref="IsPhaseValid"/>, <see cref="IsAmplitudeValid"/>,
/// <see cref="IsFrequencyValid"/> and <see cref="IsCycleFrequencyValid"/> is passed through
/// unchanged from the corresponding <see cref="HilbertSampleResult"/>; the engine only adds those
/// polar-specific quantities on top (<see cref="IsValid"/> is a computed alias of
/// <see cref="IsPhaseValid"/>). <see cref="PhaseAngularVelocity"/> is a verbatim unit conversion of
/// <see cref="HilbertSampleResult.PhaseDeltaDeg"/> and <see cref="PhaseFrequencyError"/> is
/// derived from it and <see cref="CycleAngularFrequency"/>. The split validity flags are derived
/// from the pass-through <see cref="IsWarmup"/> / <see cref="Amplitude"/> /
/// <see cref="InstantaneousPeriod"/>, the Homodyne trend-mode state and the
/// <c>HilbertDecompositionParameters.MinimumCyclePeriod</c> floor. Price-native carriers stay
/// <see langword="decimal"/>; trig-derived quantities are <see langword="double"/> (no
/// convenience conversion of financial values).
///
/// Domain note: <see cref="InPhase"/>, <see cref="Quadrature"/>, <see cref="Amplitude"/> and
/// <see cref="InstantaneousPeriod"/> are <see langword="decimal"/>, so
/// <c>NaN</c> / <c>Infinity</c> cannot occur on them and their guards are value guards
/// (<c>&gt; 0</c>, <c>&gt;= threshold</c>), not finiteness guards. <see langword="double"/>
/// <c>NaN</c> appears only in the trig-derived <see cref="CycleAngularFrequency"/> /
/// <see cref="PhaseAngularVelocity"/> / <see cref="PhaseFrequencyError"/>.
/// </summary>
/// <param name="InPhase">EMA-smoothed in-phase component I (price-derived).</param>
/// <param name="Quadrature">EMA-smoothed quadrature component Q (price-derived).</param>
/// <param name="Amplitude">Instantaneous amplitude (envelope) r = sqrt(I^2 + Q^2), price-native.</param>
/// <param name="Power">r^2.</param>
/// <param name="NormalizedInPhase">I / r, clamped to [-1, 1] (0 when r is below the micro-amplitude threshold).</param>
/// <param name="NormalizedQuadrature">Q / r, clamped to [-1, 1] (0 when r is below the micro-amplitude threshold).</param>
/// <param name="PhaseRadians">
/// Instantaneous phase (symbol phi_t / theta_t) = arg(z_t) with the analytic signal z_t = I_t - jQ_t,
/// i.e. atan2(-Q, I), principal value in the range (-pi, pi]. The -Q follows Ehlers'
/// convention: the Canonical Homodyne Discriminator treats the analytic signal as z = I - jQ so the
/// phase advances (increases with time), matching the sign of the engine's Homodyne frequency
/// extraction. This is the opposite rotation sense to the textbook z = I + jQ / atan2(Q, I). The
/// value is inherited verbatim from <see cref="HilbertDecompositionEngine"/> (Math.Atan2(-(double)q2,
/// (double)i2)); this engine does not recompute it -- it only adds the [0, 360) mapping.
/// </param>
/// <param name="PhaseDegrees">
/// Instantaneous phase (symbol theta_deg_t) mapped to the range [0, 360):
/// <c>((PhaseRadians * 180/pi) mod 360 + 360) mod 360</c> (floor-modulo, so no negative values).
/// </param>
/// <param name="UnwrappedPhaseDegrees">
/// Cumulative shortest-angular-path unwrapped phase in degrees (symbol Phi_deg_t). Each bar the
/// principal-value step is unwrapped by the +/-360 rule
/// (<c>dPhi = phi_t - phi_{t-1}; if dPhi &gt; pi: offset -= 2pi; else if dPhi &lt; -pi: offset += 2pi;
/// Phi_deg_t = (phi_t + offset) * 180/pi</c>), then accumulated. Warm-up initial state is
/// <c>offset = 0</c>. Inherited verbatim from <see cref="HilbertSampleResult.UnwrappedPhaseDeg"/>;
/// this engine does not recompute it.
/// </param>
/// <param name="CycleAngularFrequency">
/// Cycle angular frequency (symbol omega_t) = <c>2*pi / InstantaneousPeriod</c> (rad/bar), derived
/// from the Homodyne-discriminator cycle-period estimate. This is a cycle/period-derived rate, NOT
/// the bar-to-bar phase-difference angular velocity (the time derivative of the unwrapped phase);
/// the two coincide only while the cycle is stationary. Being period-derived, it has no arctan
/// phase-difference wrap-around. <c>double.NaN</c> when <see cref="InstantaneousPeriod"/> is not
/// positive.
/// </param>
/// <param name="PhaseAngularVelocity">
/// Bar-to-bar phase-difference angular velocity (symbol omega_phi_t) d(unwrapped phase)/dt in
/// rad/bar = <c>DeltaPhi_t * pi / 180</c>, where
/// <c>DeltaPhi_t = UnwrappedPhaseDegrees_t - UnwrappedPhaseDegrees_{t-1}</c> and
/// <c>DeltaPhi_0 = 0</c> on the first bar. The degree delta is
/// <see cref="HilbertSampleResult.PhaseDeltaDeg"/>, inherited verbatim -- this engine only converts
/// deg -&gt; rad/bar and does not recompute it. The sign follows the Ehlers phase-advance convention
/// (positive while the cycle advances). Unlike <see cref="CycleAngularFrequency"/> this IS the
/// arctan phase-difference rate, so it carries that rate's bar-to-bar noise and can spike or go
/// negative during phase noise.
/// </param>
/// <param name="PhaseFrequencyError">
/// |<see cref="CycleAngularFrequency"/> - <see cref="PhaseAngularVelocity"/>| in rad/bar (symbol
/// epsilon_omega_t): how far the period-derived cycle rate disagrees with the measured phase
/// progression. Near zero while the dominant cycle is stationary; grows with regime shifts, low
/// amplitude, in-phase/quadrature noise, the Hilbert warm-up transient, and period-estimate jitter.
/// <c>double.NaN</c> when either rate is non-finite. Diagnostic statistic, not a validity flag.
/// </param>
/// <param name="InstantaneousPeriod">
/// Rate-limited raw Homodyne cycle-period estimate in bars (symbol P_t): clamped to
/// [MinPeriod, MaxPeriod] with a per-bar DeltaLimit step cap. Distinct from
/// <see cref="DominantCyclePeriod"/> (which is the smoothed form) -- the two are not duplicates.
/// </param>
/// <param name="DominantCyclePeriod">
/// Two-stage exponentially smoothed dominant cycle in bars (symbol L_dom_t): the smoothed
/// counterpart of the raw <see cref="InstantaneousPeriod"/>.
/// </param>
/// <param name="PhaseStability">
/// Population standard deviation of the trailing <c>StabilityWindow</c> phase deltas (degrees);
/// symbol sigma_phi_t. Larger values indicate a less stationary cycle.
/// </param>
/// <param name="IsWarmup">True while the underlying Hilbert filter chain is still warming up.</param>
/// <param name="IsPhaseValid">
/// True when the instantaneous phase is trustworthy: out of warm-up AND the amplitude is at/above
/// the micro-amplitude threshold (below it the phase is latched to the previous bar by the
/// atan2(0, 0) guard). This is the retained meaning of <see cref="IsValid"/> -- the two are
/// definitionally equal (both are <c>!IsWarmup AND Amplitude &gt;= MicroAmplitudeThreshold</c>);
/// the flag is provided so callers can name the phase-specific check explicitly.
/// </param>
/// <param name="IsAmplitudeValid">
/// True once out of warm-up. The amplitude / power reading is always meaningful thereafter; an
/// amplitude below the micro-amplitude threshold is a valid low-energy ("no cycle") observation,
/// not an invalid one.
/// </param>
/// <param name="IsFrequencyValid">
/// True when a genuine dominant cycle is present AND resolvable above the effective Nyquist floor:
/// <see cref="IsCycleFrequencyValid"/> AND the underlying Homodyne discriminator is not in trend
/// mode (period not pinned at/near MaxPeriod) AND <see cref="InstantaneousPeriod"/> is at least
/// <c>HilbertDecompositionParameters.MinimumCyclePeriod</c> bars. Equivalent to
/// <c>IsCycleFrequencyValid AND !TrendMode AND InstantaneousPeriod &gt;= MinimumCyclePeriod</c>.
/// </param>
/// <param name="IsCycleFrequencyValid">
/// Trend-mode-agnostic counterpart of <see cref="IsFrequencyValid"/>: <see cref="IsPhaseValid"/> AND
/// a positive <see cref="InstantaneousPeriod"/> (which is exactly when
/// <see cref="CycleAngularFrequency"/> is finite). Use this when trend-mode periods -- the cycle
/// estimate pinned near MaxPeriod -- should still be surfaced rather than gated out. Does NOT apply
/// the <c>MinimumCyclePeriod</c> Nyquist floor.
/// </param>
public readonly record struct PolarSampleResult(
    decimal InPhase,
    decimal Quadrature,
    decimal Amplitude,
    decimal Power,
    decimal NormalizedInPhase,
    decimal NormalizedQuadrature,
    double PhaseRadians,
    double PhaseDegrees,
    double UnwrappedPhaseDegrees,
    double CycleAngularFrequency,
    double PhaseAngularVelocity,
    double PhaseFrequencyError,
    decimal InstantaneousPeriod,
    decimal DominantCyclePeriod,
    double PhaseStability,
    bool IsWarmup,
    bool IsPhaseValid,
    bool IsAmplitudeValid,
    bool IsFrequencyValid,
    bool IsCycleFrequencyValid)
{
    /// <summary>
    /// Retained alias for <see cref="IsPhaseValid"/>. Kept for backward compatibility; the two are
    /// definitionally identical, so this is a computed property rather than a stored field.
    /// </summary>
    public bool IsValid => IsPhaseValid;
}

/// <summary>
/// Complete time-series result of a polar-coordinate transform decomposition.
/// Mirrors <see cref="HilbertDecompositionResult"/>.
/// </summary>
public sealed record PolarDecompositionResult(
    PolarSampleResult[] Samples,
    int WarmupBars,
    HilbertDecompositionParameters Parameters)
{
    public ReadOnlySpan<PolarSampleResult> AsSpan() => Samples.AsSpan();

    public PolarSampleResult this[int index] => Samples[index];

    public int Count => Samples.Length;
}
