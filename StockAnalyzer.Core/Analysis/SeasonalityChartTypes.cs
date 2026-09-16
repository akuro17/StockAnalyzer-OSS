using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Radius normalization strategy for one seasonality series. Forced by the series characteristic
/// (decision D4): a series is assigned exactly one mode, never a silent fallback.
/// </summary>
public enum SeasonalityRadiusMode
{
    /// <summary>rho = (V - V_yearBase) / V_yearBase. For price and price-like series.</summary>
    PercentVsYearStart,

    /// <summary>rho = (V - (lo + hi) / 2) / ((hi - lo) / 2), in [-1, 1]. For bounded oscillators (e.g. RSI 0..100).</summary>
    SignedUnitFromBounds,

    /// <summary>rho = (V - mean_year) / stdev_year. For unbounded indicators. Year-internal statistic (non-causal within a completed year).</summary>
    ZScoreWithinYear
}

/// <summary>Per-sample outcome of the seasonality projection.</summary>
public enum SeasonalitySampleStatus
{
    /// <summary>rho and radius are both valid; the sample participates in the polyline.</summary>
    Valid,

    /// <summary>PercentVsYearStart could not resolve a positive year base value; the whole year trace is unusable.</summary>
    NonPositiveBase,

    /// <summary>rho was valid but the mapped radius was negative or non-finite; the polyline breaks here.</summary>
    RadiusUnderflow,

    /// <summary>The source value was missing, or the mode is not applicable to this sample.</summary>
    InsufficientData,

    /// <summary><see cref="SeasonalityRadiusMode.ZScoreWithinYear"/>: fewer than <see cref="SeasonalityChartConstants.MinValidSamplesForZScore"/> valid values in the (series, year).</summary>
    InsufficientSamples,

    /// <summary><see cref="SeasonalityRadiusMode.ZScoreWithinYear"/>: enough values but the year variance is at or below epsilon (a constant year); the z-score is undefined.</summary>
    ZeroVariance
}

/// <summary>
/// How the "Seasonality Chart" tab renders the (single) base-symbol result. The engine output is
/// identical for every mode — only the View-layer renderer changes, and switching never re-runs the
/// analysis. Exactly one mode is shown at a time.
/// </summary>
public enum SeasonalityDisplayMode
{
    /// <summary>The clockwise polar clock (Jan at 12 o'clock). The default; preserves the existing look.</summary>
    PolarClock,

    /// <summary>A left-to-right line overlay with the left edge pinned to the year start (u = 0).</summary>
    LinearYearOverlay,

    /// <summary>The fixed-size month-by-year returns table (no plot).</summary>
    MonthlyReturnsTable
}

/// <summary>
/// Matrix orientation for the monthly returns table (Mode "Monthly").
/// </summary>
public enum SeasonalityMonthlyTableOrientation
{
    /// <summary>X-axis (columns) = Years, Y-axis (rows) = Months (Jan..Dec, Annual). The default layout.</summary>
    MonthsAsRows,

    /// <summary>X-axis (columns) = Months (Jan..Dec, Annual), Y-axis (rows) = Years ('21..'25, Avg). Transposed layout.</summary>
    MonthsAsColumns
}

/// <summary>One chronological observation of a source series (price close, indicator value, other-ticker close).</summary>
public readonly record struct SeasonalityPoint(DateTime Timestamp, decimal? Value);

/// <summary>
/// One source series fed to <see cref="SeasonalityChartEngine.Analyze"/>. The engine never fetches
/// data; callers (the Avalonia data source) build these from daily candles / indicator results.
/// </summary>
public sealed class SeasonalitySeriesInput
{
    public SeasonalitySeriesInput(
        int seriesId,
        string label,
        SeasonalityRadiusMode radiusMode,
        IReadOnlyList<SeasonalityPoint> points,
        decimal? boundsLow = null,
        decimal? boundsHigh = null)
    {
        SeriesId = seriesId;
        Label = label ?? string.Empty;
        RadiusMode = radiusMode;
        Points = points ?? throw new ArgumentNullException(nameof(points));
        BoundsLow = boundsLow;
        BoundsHigh = boundsHigh;
    }

    public int SeriesId { get; }

    public string Label { get; }

    public SeasonalityRadiusMode RadiusMode { get; }

    /// <summary>Chronological, strictly ascending by <see cref="SeasonalityPoint.Timestamp"/>.</summary>
    public IReadOnlyList<SeasonalityPoint> Points { get; }

    /// <summary>Lower bound for <see cref="SeasonalityRadiusMode.SignedUnitFromBounds"/>; ignored otherwise.</summary>
    public decimal? BoundsLow { get; }

    /// <summary>Upper bound for <see cref="SeasonalityRadiusMode.SignedUnitFromBounds"/>; ignored otherwise.</summary>
    public decimal? BoundsHigh { get; }
}

/// <summary>Manual configuration for one seasonality polar chart computation.</summary>
/// <param name="YearsToOverlay">Number of most-recent calendar years to draw (decision D6).</param>
/// <param name="ManualBaseRadius">Explicit R0; auto-derived from the global rho range when null (decision D5).</param>
/// <param name="ManualScaleFactor">Explicit K; auto-derived from the global rho range when null (decision D5).</param>
/// <param name="IncludeMeanPath">
/// When true the engine also produces <see cref="SeasonalityChartResult.MeanPaths"/> — one averaged
/// seasonal trajectory per series. Off by default: the mean path is a retrospective, year-internal
/// aggregate and must be opted into explicitly (decision D7).
/// </param>
/// <param name="StartMonth">
/// The starting month (1..12) for the 1-year analysis cycle: 1 = January (calendar year), 4 = April
/// (Japanese corporate fiscal year). Positioned at 12 o'clock in Polar Clock mode and pinned to the
/// left edge (u = 0) in Linear Year Overlay mode.
/// </param>
public readonly record struct SeasonalityChartParameters(
    uint YearsToOverlay,
    decimal? ManualBaseRadius = null,
    decimal? ManualScaleFactor = null,
    bool IncludeMeanPath = false,
    uint StartMonth = 1)
{
    public void Validate()
    {
        if (YearsToOverlay is < SeasonalityChartConstants.MinYearsToOverlay or > SeasonalityChartConstants.MaxYearsToOverlay)
        {
            throw new ArgumentOutOfRangeException(nameof(YearsToOverlay));
        }

        if (ManualBaseRadius is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(ManualBaseRadius));
        }

        if (ManualScaleFactor is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(ManualScaleFactor));
        }

        if (StartMonth is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(StartMonth));
        }
    }
}

/// <summary>One chronological sample and its polar coordinates within a single year trace.</summary>
/// <param name="Timestamp">Original bar timestamp.</param>
/// <param name="CanonicalDayIndex"><see cref="SeasonalityCalendar.CanonicalDayIndex"/> for <see cref="Timestamp"/>, in [0, 365].</param>
/// <param name="YearFraction">u = CanonicalDayIndex / <see cref="SeasonalityCalendar.CanonicalDaysPerYear"/>, in [0, 1).</param>
/// <param name="PlotAngleRadians">theta = pi/2 - 2*pi*u (12 o'clock = Jan 1, clockwise; decision D8).</param>
/// <param name="Status">Sample outcome.</param>
/// <param name="Value">Source value at this bar (may be null).</param>
/// <param name="RateOfChange">rho in the decimal domain; null when not computable.</param>
/// <param name="Radius">r = (double)(BaseRadius + ScaleFactor * rho); <see cref="double.NaN"/> when the sample is not drawable.</param>
public readonly record struct SeasonalitySample(
    DateTime Timestamp,
    int CanonicalDayIndex,
    double YearFraction,
    double PlotAngleRadians,
    SeasonalitySampleStatus Status,
    decimal? Value,
    decimal? RateOfChange,
    double Radius);

/// <summary>
/// One canonical day-of-year bin of a <see cref="SeasonalityMeanPath"/>: the mean rho of that day
/// across the overlaid years, its polar coordinates, and how many years fed the mean. Distinct from
/// <see cref="SeasonalitySample"/> — a mean-path bin has no single source bar, so it carries neither a
/// <c>Timestamp</c> nor a source <c>Value</c>, and it adds <see cref="SampleCount"/>.
/// </summary>
/// <param name="CanonicalDayIndex"><see cref="SeasonalityCalendar.CanonicalDayIndex"/> of the bin, in [0, 365].</param>
/// <param name="YearFraction">u = CanonicalDayIndex / <see cref="SeasonalityCalendar.CanonicalDaysPerYear"/>, in [0, 1).</param>
/// <param name="PlotAngleRadians">theta = pi/2 - 2*pi*u (12 o'clock = Jan 1, clockwise; decision D8).</param>
/// <param name="Status">Bin outcome (<see cref="SeasonalitySampleStatus.Valid"/> or <see cref="SeasonalitySampleStatus.RadiusUnderflow"/>).</param>
/// <param name="RateOfChange">Mean rho over the overlaid years, in the decimal domain.</param>
/// <param name="Radius">r = (double)(BaseRadius + ScaleFactor * mean rho); <see cref="double.NaN"/> when not drawable.</param>
/// <param name="SampleCount">
/// Number of overlaid years averaged into this bin. Always &gt;= <see cref="SeasonalityChartConstants.MinYearsForMeanPath"/>
/// (bins below that are not emitted). Not consumed by the renderer yet — retained for a future
/// confidence / opacity treatment so the engine need not change when that is added.
/// </param>
public readonly record struct SeasonalityMeanPathSample(
    int CanonicalDayIndex,
    double YearFraction,
    double PlotAngleRadians,
    SeasonalitySampleStatus Status,
    decimal? RateOfChange,
    double Radius,
    int SampleCount);

/// <summary>One polyline: a single series over a single calendar year.</summary>
public sealed class SeasonalityYearTrace
{
    public SeasonalityYearTrace(
        int seriesId,
        string seriesLabel,
        int calendarYear,
        SeasonalityRadiusMode radiusMode,
        SeasonalitySample[] samples)
    {
        SeriesId = seriesId;
        SeriesLabel = seriesLabel ?? string.Empty;
        CalendarYear = calendarYear;
        RadiusMode = radiusMode;
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
    }

    public int SeriesId { get; }

    public string SeriesLabel { get; }

    public int CalendarYear { get; }

    public SeasonalityRadiusMode RadiusMode { get; }

    public IReadOnlyList<SeasonalitySample> Samples { get; }
}

/// <summary>
/// One series' average seasonal trajectory across the overlaid calendar years. For each canonical
/// day-of-year bin it holds the mean of that day's <see cref="SeasonalitySample.RateOfChange"/> over every
/// selected year, mapped to a radius with the same scale/offset (K, R0) as the year traces. This is a
/// retrospective, year-internal aggregate — it consumes each overlaid year in full, including the
/// incomplete current year — and must never be read as predictive (decision D7, NFR-07).
/// </summary>
public sealed class SeasonalityMeanPath
{
    public SeasonalityMeanPath(
        int seriesId,
        string seriesLabel,
        SeasonalityRadiusMode radiusMode,
        SeasonalityMeanPathSample[] samples)
    {
        SeriesId = seriesId;
        SeriesLabel = seriesLabel ?? string.Empty;
        RadiusMode = radiusMode;
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
    }

    public int SeriesId { get; }

    public string SeriesLabel { get; }

    public SeasonalityRadiusMode RadiusMode { get; }

    /// <summary>Ascending by <see cref="SeasonalityMeanPathSample.CanonicalDayIndex"/> (an across-years aggregate, not source bars).</summary>
    public IReadOnlyList<SeasonalityMeanPathSample> Samples { get; }
}

/// <summary>Immutable result of a seasonality polar chart computation.</summary>
public sealed class SeasonalityChartResult
{
    public SeasonalityChartResult(
        SeasonalityChartParameters parameters,
        SeasonalityYearTrace[] traces,
        double baseRadius,
        double scaleFactor,
        double minRadius,
        double maxRadius,
        int[] calendarYears,
        SeasonalityMeanPath[]? meanPaths = null)
    {
        Parameters = parameters;
        Traces = traces ?? throw new ArgumentNullException(nameof(traces));
        BaseRadius = baseRadius;
        ScaleFactor = scaleFactor;
        MinRadius = minRadius;
        MaxRadius = maxRadius;
        CalendarYears = calendarYears ?? Array.Empty<int>();
        MeanPaths = meanPaths ?? Array.Empty<SeasonalityMeanPath>();
    }

    public SeasonalityChartParameters Parameters { get; }

    /// <summary>Year traces ordered by <see cref="SeasonalityYearTrace.SeriesId"/> then <see cref="SeasonalityYearTrace.CalendarYear"/>.</summary>
    public IReadOnlyList<SeasonalityYearTrace> Traces { get; }

    /// <summary>Data-unit radius of the rho = 0 circle (decision D5 offset).</summary>
    public double BaseRadius { get; }

    /// <summary>Data-unit radius per unit rho (decision D5 scale).</summary>
    public double ScaleFactor { get; }

    /// <summary>Smallest drawable radius across all selected traces (0 when none).</summary>
    public double MinRadius { get; }

    /// <summary>Largest drawable radius across all selected traces (0 when none).</summary>
    public double MaxRadius { get; }

    /// <summary>Calendar years included, ascending (the most recent <c>YearsToOverlay</c>).</summary>
    public IReadOnlyList<int> CalendarYears { get; }

    /// <summary>
    /// One averaged seasonal trajectory per series, ordered by series id. Empty unless
    /// <see cref="SeasonalityChartParameters.IncludeMeanPath"/> was set and at least
    /// <see cref="SeasonalityChartConstants.MinYearsForMeanPath"/> years were overlaid.
    /// </summary>
    public IReadOnlyList<SeasonalityMeanPath> MeanPaths { get; }
}

/// <summary>Single source of truth for seasonality polar chart numeric constants (no magic numbers).</summary>
public static class SeasonalityChartConstants
{
    /// <summary>Minimum selectable overlay depth in calendar years.</summary>
    public const uint MinYearsToOverlay = 1;

    /// <summary>
    /// Maximum selectable overlay depth in calendar years. The overlay depth is independent of the
    /// per-year colour palette: a depth beyond <see cref="SeasonalityBasePaletteSize"/> reuses the base
    /// hues with a derived tint/shade (confirmation S4b), so this cap is not the palette size.
    /// </summary>
    public const uint MaxYearsToOverlay = 100;

    /// <summary>
    /// Number of directly configurable per-year overlay colours (the Seasonality settings palette
    /// slots). Years past this rank keep one of these hues and only shift its value/saturation.
    /// </summary>
    public const int SeasonalityBasePaletteSize = 10;

    /// <summary>Data-unit radius that the smallest drawn point maps to after auto scale/offset (decision D5 floor).</summary>
    public const decimal InnerMarginRadius = 10m;

    /// <summary>Data-unit radial span that the full rho range is mapped onto when auto-scaling (decision D5).</summary>
    public const decimal TargetRadialSpan = 100m;

    /// <summary>Zero-division / degenerate-span floor for the rho range and the bounds width. Not a signal threshold.</summary>
    public const decimal RateOfChangeEpsilon = 0.0000000001m;

    /// <summary>Minimum valid observations per (series, year) group required to standardize under ZScoreWithinYear.</summary>
    public const int MinValidSamplesForZScore = 2;

    /// <summary>Fewest overlaid calendar years, and fewest observations per day bin, for a mean seasonal path to be produced (decision D7).</summary>
    public const int MinYearsForMeanPath = 2;

    /// <summary>
    /// Magnitude at or below which a monthly-returns table cell is treated as flat (neutral colour)
    /// rather than up/down. Zero: any non-zero return takes its sign's colour.
    /// </summary>
    public const decimal MonthlyReturnColorEpsilon = 0m;
}
