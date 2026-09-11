using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Wraps the linear calendar-time axis of one or more chronological series onto a clock face and
/// produces one polyline per (series, calendar year) for overlaid multi-year seasonality display.
///
/// Mapping (locked decisions, SEASONALITY_CHART_SPEC.md v1.3.0):
/// - Angle: every date is folded onto one 366-slot canonical ring by
///   <see cref="SeasonalityCalendar.CanonicalDayIndex"/> and divided by
///   <see cref="SeasonalityCalendar.CanonicalDaysPerYear"/>, so the same calendar date lands at the
///   same angle in every overlaid year (leap or not); 1 January is the 12 o'clock apex and the angle
///   advances clockwise: <c>theta = pi/2 - 2*pi*u</c> (decisions D1, D3, D8).
/// - Radius: the year-start-relative change <c>rho</c> is normalized per series characteristic
///   (<see cref="SeasonalityRadiusMode"/>, decision D4); the year base value is the first valid
///   (non-missing, and positive for <see cref="SeasonalityRadiusMode.PercentVsYearStart"/>) value of
///   the year, found by forward search (decision D2).
/// - Scale/offset: <see cref="SeasonalityChartResult.ScaleFactor"/> (K) and
///   <see cref="SeasonalityChartResult.BaseRadius"/> (R0) are derived from the global rho min/max so
///   the full range fills a fixed radial band, unless supplied manually (decision D5).
///
/// Financial values stay <see cref="decimal"/> for the entire rho computation; the only conversion
/// to <see cref="double"/> is the final radius <c>r = (double)(R0 + K*rho)</c> and the trig-derived
/// angle, mirroring the decimal boundary rule of <see cref="SpiralPriceModelEngine"/>.
///
/// This is a descriptive multi-year overlay, not a forward-looking signal. Per-sample rho depends
/// only on that year's base value (known since January), so each point is causal;
/// <see cref="SeasonalityRadiusMode.ZScoreWithinYear"/> and any cross-year aggregate a caller builds
/// on top are year-internal / retrospective and must not be treated as predictive.
/// </summary>
public static class SeasonalityChartEngine
{
    public static SeasonalityChartResult Analyze(
        IReadOnlyList<SeasonalitySeriesInput> series,
        SeasonalityChartParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(series);
        parameters.Validate();

        // Pass 1: split every series into (year) groups and compute rho in the decimal domain.
        var pendingTraces = new List<PendingTrace>();
        var yearSet = new SortedSet<int>();
        foreach (SeasonalitySeriesInput input in series)
        {
            if (input is null)
            {
                throw new ArgumentException("Series input entries must not be null.", nameof(series));
            }

            ValidateChronological(input);
            ResolveBounds(input, out decimal? boundsMid, out decimal? boundsHalf);

            var pointsByYear = new Dictionary<int, List<SeasonalityPoint>>();
            foreach (SeasonalityPoint point in input.Points)
            {
                int year = parameters.StartMonth <= 1 || point.Timestamp.Month >= parameters.StartMonth
                    ? point.Timestamp.Year
                    : point.Timestamp.Year - 1;
                if (!pointsByYear.TryGetValue(year, out List<SeasonalityPoint>? bucket))
                {
                    bucket = new List<SeasonalityPoint>();
                    pointsByYear[year] = bucket;
                }

                bucket.Add(point);
                yearSet.Add(year);
            }

            foreach (KeyValuePair<int, List<SeasonalityPoint>> yearBucket in pointsByYear)
            {
                pendingTraces.Add(BuildPendingTrace(input, yearBucket.Key, yearBucket.Value, boundsMid, boundsHalf, parameters.StartMonth));
            }
        }

        int[] selectedYears = SelectRecentYears(yearSet, parameters.YearsToOverlay);
        var selectedYearLookup = new HashSet<int>(selectedYears);

        // Pass 2: global rho min/max over the selected years -> auto K and R0 -> mapped radii.
        (decimal rhoMin, decimal rhoMax) = FindRhoRange(pendingTraces, selectedYearLookup);
        decimal span = rhoMax - rhoMin;
        if (span < SeasonalityChartConstants.RateOfChangeEpsilon)
        {
            span = SeasonalityChartConstants.RateOfChangeEpsilon;
        }

        decimal scaleFactor = parameters.ManualScaleFactor ?? (SeasonalityChartConstants.TargetRadialSpan / span);
        decimal baseRadius = parameters.ManualBaseRadius ?? (SeasonalityChartConstants.InnerMarginRadius - (scaleFactor * rhoMin));

        double minRadius = double.PositiveInfinity;
        double maxRadius = double.NegativeInfinity;
        var traces = new List<SeasonalityYearTrace>(pendingTraces.Count);
        foreach (PendingTrace pending in pendingTraces)
        {
            if (!selectedYearLookup.Contains(pending.CalendarYear))
            {
                continue;
            }

            var samples = new SeasonalitySample[pending.Samples.Count];
            for (int i = 0; i < pending.Samples.Count; i++)
            {
                PendingSample source = pending.Samples[i];
                SeasonalitySampleStatus status = source.Status;
                double radius = double.NaN;

                if (status == SeasonalitySampleStatus.Valid && source.RateOfChange is { } rho)
                {
                    if (TryMapRadius(baseRadius, scaleFactor, rho, out double mapped))
                    {
                        radius = mapped;
                        if (mapped < minRadius)
                        {
                            minRadius = mapped;
                        }

                        if (mapped > maxRadius)
                        {
                            maxRadius = mapped;
                        }
                    }
                    else
                    {
                        status = SeasonalitySampleStatus.RadiusUnderflow;
                    }
                }

                samples[i] = new SeasonalitySample(
                    source.Timestamp,
                    source.CanonicalDayIndex,
                    source.YearFraction,
                    source.PlotAngleRadians,
                    status,
                    source.Value,
                    source.RateOfChange,
                    radius);
            }

            traces.Add(new SeasonalityYearTrace(pending.SeriesId, pending.SeriesLabel, pending.CalendarYear, pending.RadiusMode, samples));
        }

        if (!double.IsFinite(minRadius))
        {
            minRadius = 0d;
        }

        if (!double.IsFinite(maxRadius))
        {
            maxRadius = 0d;
        }

        traces.Sort(static (left, right) =>
        {
            int bySeries = left.SeriesId.CompareTo(right.SeriesId);
            return bySeries != 0 ? bySeries : left.CalendarYear.CompareTo(right.CalendarYear);
        });

        SeasonalityMeanPath[] meanPaths = parameters.IncludeMeanPath
            ? BuildMeanPaths(traces, selectedYears.Length, baseRadius, scaleFactor)
            : Array.Empty<SeasonalityMeanPath>();

        return new SeasonalityChartResult(
            parameters,
            traces.ToArray(),
            (double)baseRadius,
            (double)scaleFactor,
            minRadius,
            maxRadius,
            selectedYears,
            meanPaths);
    }

    /// <summary>
    /// For each series, averages every overlaid year's <c>rho</c> per day-of-year bin and maps the
    /// mean to a radius with the same K/R0 as the year traces. A day bin averaged from fewer than
    /// <see cref="SeasonalityChartConstants.MinYearsForMeanPath"/> years is skipped (it is not a mean),
    /// and no path is produced at all below that many overlaid years. The result is retrospective and
    /// year-internal (decision D7 / NFR-07); it never widens <see cref="SeasonalityChartResult.MinRadius"/>
    /// or <see cref="SeasonalityChartResult.MaxRadius"/> because a mean of values in [rhoMin, rhoMax]
    /// stays in that band.
    /// </summary>
    private static SeasonalityMeanPath[] BuildMeanPaths(
        List<SeasonalityYearTrace> traces,
        int selectedYearCount,
        decimal baseRadius,
        decimal scaleFactor)
    {
        if (selectedYearCount < SeasonalityChartConstants.MinYearsForMeanPath || traces.Count == 0)
        {
            return Array.Empty<SeasonalityMeanPath>();
        }

        var bySeries = new Dictionary<int, MeanPathAccumulator>();
        var order = new List<int>();
        foreach (SeasonalityYearTrace trace in traces)
        {
            if (!bySeries.TryGetValue(trace.SeriesId, out MeanPathAccumulator? accumulator))
            {
                accumulator = new MeanPathAccumulator(trace.SeriesId, trace.SeriesLabel, trace.RadiusMode);
                bySeries[trace.SeriesId] = accumulator;
                order.Add(trace.SeriesId);
            }

            AccumulateTraceWithForwardFill(accumulator, trace);
        }

        var meanPaths = new List<SeasonalityMeanPath>(order.Count);
        foreach (int seriesId in order)
        {
            MeanPathAccumulator accumulator = bySeries[seriesId];
            var samples = new List<SeasonalityMeanPathSample>(SeasonalityCalendar.CanonicalDaysPerYear);
            for (int bin = 0; bin < SeasonalityCalendar.CanonicalDaysPerYear; bin++)
            {
                if (accumulator.Count[bin] < SeasonalityChartConstants.MinYearsForMeanPath)
                {
                    continue;
                }

                decimal meanRho = accumulator.Sum[bin] / accumulator.Count[bin];
                double yearFraction = bin / (double)SeasonalityCalendar.CanonicalDaysPerYear;
                double plotAngle = SeasonalityCalendar.PlotAngleRadians(yearFraction);

                SeasonalitySampleStatus status = SeasonalitySampleStatus.Valid;
                double radius = double.NaN;
                if (TryMapRadius(baseRadius, scaleFactor, meanRho, out double mapped))
                {
                    radius = mapped;
                }
                else
                {
                    status = SeasonalitySampleStatus.RadiusUnderflow;
                }

                samples.Add(new SeasonalityMeanPathSample(
                    bin,
                    yearFraction,
                    plotAngle,
                    status,
                    meanRho,
                    radius,
                    accumulator.Count[bin]));
            }

            if (samples.Count > 0)
            {
                meanPaths.Add(new SeasonalityMeanPath(
                    accumulator.SeriesId,
                    accumulator.SeriesLabel,
                    accumulator.RadiusMode,
                    samples.ToArray()));
            }
        }

        return meanPaths.ToArray();
    }

    /// <summary>
    /// Accumulates a single year trace into the 366-day canonical bins using forward-fill across
    /// non-trading intervals (weekends, holidays). This prevents high-frequency sawtooth oscillations
    /// caused by calendar weekday shifts alternating which years are present in each day bin.
    /// </summary>
    private static void AccumulateTraceWithForwardFill(
        MeanPathAccumulator accumulator,
        SeasonalityYearTrace trace)
    {
        IReadOnlyList<SeasonalitySample> samples = trace.Samples;
        if (samples.Count == 0)
        {
            return;
        }

        int firstValidIdx = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Status == SeasonalitySampleStatus.Valid && samples[i].RateOfChange.HasValue)
            {
                firstValidIdx = i;
                break;
            }
        }

        if (firstValidIdx < 0)
        {
            return;
        }

        int lastValidIdx = -1;
        for (int i = samples.Count - 1; i >= firstValidIdx; i--)
        {
            if (samples[i].Status == SeasonalitySampleStatus.Valid && samples[i].RateOfChange.HasValue)
            {
                lastValidIdx = i;
                break;
            }
        }

        int firstBin = samples[firstValidIdx].CanonicalDayIndex;
        decimal firstRho = samples[firstValidIdx].RateOfChange!.Value;

        // If the trace starts near the cycle start (bin <= 15), back-fill to bin 0 so non-trading days before the first trading day carry base rho.
        int startFill = firstBin <= 15 ? 0 : firstBin;
        for (int b = startFill; b < firstBin; b++)
        {
            accumulator.Sum[b] += firstRho;
            accumulator.Count[b]++;
        }

        // Forward-fill between consecutive valid samples (e.g. across weekends and holidays)
        int prevBin = firstBin;
        decimal prevRho = firstRho;

        for (int i = firstValidIdx + 1; i <= lastValidIdx; i++)
        {
            SeasonalitySample sample = samples[i];
            if (sample.Status != SeasonalitySampleStatus.Valid || !sample.RateOfChange.HasValue)
            {
                continue;
            }

            int curBin = sample.CanonicalDayIndex;
            decimal curRho = sample.RateOfChange.Value;

            for (int b = prevBin; b < curBin; b++)
            {
                accumulator.Sum[b] += prevRho;
                accumulator.Count[b]++;
            }

            prevBin = curBin;
            prevRho = curRho;
        }

        // Add the last valid sample itself
        accumulator.Sum[prevBin] += prevRho;
        accumulator.Count[prevBin]++;

        // If the trace reaches near the cycle end (bin >= 350), forward-fill through the end of the year cycle (bin 365).
        if (prevBin >= 350)
        {
            for (int b = prevBin + 1; b < SeasonalityCalendar.CanonicalDaysPerYear; b++)
            {
                accumulator.Sum[b] += prevRho;
                accumulator.Count[b]++;
            }
        }
    }

    private static void ResolveBounds(SeasonalitySeriesInput input, out decimal? boundsMid, out decimal? boundsHalf)
    {
        boundsMid = null;
        boundsHalf = null;
        if (input.RadiusMode != SeasonalityRadiusMode.SignedUnitFromBounds)
        {
            return;
        }

        if (!input.BoundsLow.HasValue || !input.BoundsHigh.HasValue ||
            input.BoundsHigh.Value - input.BoundsLow.Value <= SeasonalityChartConstants.RateOfChangeEpsilon)
        {
            throw new ArgumentException("SignedUnitFromBounds requires BoundsLow < BoundsHigh.", nameof(input));
        }

        boundsMid = (input.BoundsLow.Value + input.BoundsHigh.Value) / 2m;
        boundsHalf = (input.BoundsHigh.Value - input.BoundsLow.Value) / 2m;
    }

    private static PendingTrace BuildPendingTrace(
        SeasonalitySeriesInput input,
        int year,
        List<SeasonalityPoint> yearPoints,
        decimal? boundsMid,
        decimal? boundsHalf,
        uint startMonth = 1)
    {
        decimal? baseValue = null;
        if (input.RadiusMode == SeasonalityRadiusMode.PercentVsYearStart)
        {
            foreach (SeasonalityPoint point in yearPoints)
            {
                if (point.Value is { } candidate && candidate > 0m)
                {
                    baseValue = candidate;
                    break;
                }
            }
        }

        decimal zMean = 0m;
        decimal zStdev = 0m;
        ZScoreYearStats zStats = input.RadiusMode == SeasonalityRadiusMode.ZScoreWithinYear
            ? TryComputeYearStats(yearPoints, out zMean, out zStdev)
            : ZScoreYearStats.Ok;

        var samples = new List<PendingSample>(yearPoints.Count);
        foreach (SeasonalityPoint point in yearPoints)
        {
            int canonicalDayIndex = SeasonalityCalendar.CanonicalDayIndex(point.Timestamp, startMonth);
            double yearFraction = canonicalDayIndex / (double)SeasonalityCalendar.CanonicalDaysPerYear;
            double plotAngle = SeasonalityCalendar.PlotAngleRadians(yearFraction);

            SeasonalitySampleStatus status;
            decimal? rho = null;

            if (point.Value is not { } value)
            {
                status = SeasonalitySampleStatus.InsufficientData;
            }
            else
            {
                switch (input.RadiusMode)
                {
                    case SeasonalityRadiusMode.PercentVsYearStart:
                        if (baseValue is not { } resolvedBase)
                        {
                            status = SeasonalitySampleStatus.NonPositiveBase;
                        }
                        else if (value <= 0m)
                        {
                            status = SeasonalitySampleStatus.InsufficientData;
                        }
                        else
                        {
                            rho = (value - resolvedBase) / resolvedBase;
                            status = SeasonalitySampleStatus.Valid;
                        }

                        break;

                    case SeasonalityRadiusMode.SignedUnitFromBounds:
                        rho = (value - boundsMid!.Value) / boundsHalf!.Value;
                        status = SeasonalitySampleStatus.Valid;
                        break;

                    case SeasonalityRadiusMode.ZScoreWithinYear:
                        switch (zStats)
                        {
                            case ZScoreYearStats.Ok:
                                rho = (value - zMean) / zStdev;
                                status = SeasonalitySampleStatus.Valid;
                                break;

                            case ZScoreYearStats.InsufficientSamples:
                                status = SeasonalitySampleStatus.InsufficientSamples;
                                break;

                            case ZScoreYearStats.ZeroVariance:
                                status = SeasonalitySampleStatus.ZeroVariance;
                                break;

                            default:
                                throw new InvalidOperationException($"Unhandled {nameof(ZScoreYearStats)}: {zStats}");
                        }

                        break;

                    default:
                        status = SeasonalitySampleStatus.InsufficientData;
                        break;
                }
            }

            samples.Add(new PendingSample(
                point.Timestamp,
                canonicalDayIndex,
                yearFraction,
                plotAngle,
                status,
                point.Value,
                rho));
        }

        return new PendingTrace(input.SeriesId, input.Label, year, input.RadiusMode, samples);
    }

    private static (decimal Min, decimal Max) FindRhoRange(List<PendingTrace> pendingTraces, HashSet<int> selectedYears)
    {
        decimal min = 0m;
        decimal max = 0m;
        bool seen = false;
        foreach (PendingTrace pending in pendingTraces)
        {
            if (!selectedYears.Contains(pending.CalendarYear))
            {
                continue;
            }

            foreach (PendingSample sample in pending.Samples)
            {
                if (sample.RateOfChange is not { } rho)
                {
                    continue;
                }

                if (!seen)
                {
                    min = rho;
                    max = rho;
                    seen = true;
                }
                else
                {
                    if (rho < min)
                    {
                        min = rho;
                    }

                    if (rho > max)
                    {
                        max = rho;
                    }
                }
            }
        }

        // The rho = 0 reference (the year base itself) is always part of the drawn frame.
        if (min > 0m)
        {
            min = 0m;
        }

        if (max < 0m)
        {
            max = 0m;
        }

        return (min, max);
    }

    private static bool TryMapRadius(decimal baseRadius, decimal scaleFactor, decimal rho, out double radius)
    {
        radius = double.NaN;
        try
        {
            double candidate = (double)(baseRadius + (scaleFactor * rho));
            if (!double.IsFinite(candidate) || candidate < 0d)
            {
                return false;
            }

            radius = candidate;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Why <see cref="TryComputeYearStats"/> could or could not standardize a (series, year) under ZScoreWithinYear.</summary>
    private enum ZScoreYearStats
    {
        /// <summary>Mean and standard deviation are usable.</summary>
        Ok,

        /// <summary>Fewer than <see cref="SeasonalityChartConstants.MinValidSamplesForZScore"/> valid values.</summary>
        InsufficientSamples,

        /// <summary>Enough values, but the population variance is at or below epsilon (a constant year) or the stats overflowed.</summary>
        ZeroVariance
    }

    private static ZScoreYearStats TryComputeYearStats(List<SeasonalityPoint> yearPoints, out decimal mean, out decimal stdev)
    {
        mean = 0m;
        stdev = 0m;

        try
        {
            int count = 0;
            decimal sum = 0m;
            foreach (SeasonalityPoint point in yearPoints)
            {
                if (point.Value is { } value)
                {
                    sum += value;
                    count++;
                }
            }

            if (count < SeasonalityChartConstants.MinValidSamplesForZScore)
            {
                return ZScoreYearStats.InsufficientSamples;
            }

            mean = sum / count;

            decimal squaredDeviationSum = 0m;
            foreach (SeasonalityPoint point in yearPoints)
            {
                if (point.Value is { } value)
                {
                    decimal deviation = value - mean;
                    squaredDeviationSum += deviation * deviation;
                }
            }

            decimal variance = squaredDeviationSum / count;
            if (variance <= SeasonalityChartConstants.RateOfChangeEpsilon)
            {
                return ZScoreYearStats.ZeroVariance;
            }

            stdev = DecimalSquareRoot(variance);
            return stdev > 0m ? ZScoreYearStats.Ok : ZScoreYearStats.ZeroVariance;
        }
        catch (OverflowException)
        {
            mean = 0m;
            stdev = 0m;
            return ZScoreYearStats.ZeroVariance;
        }
    }

    private static void ValidateChronological(SeasonalitySeriesInput input)
    {
        IReadOnlyList<SeasonalityPoint> points = input.Points;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Timestamp <= points[i - 1].Timestamp)
            {
                throw new ArgumentException(
                    $"Series '{input.Label}' points must be strictly ascending by Timestamp.",
                    nameof(input));
            }
        }
    }

    private static int[] SelectRecentYears(SortedSet<int> years, uint yearsToOverlay)
    {
        if (years.Count == 0)
        {
            return Array.Empty<int>();
        }

        int take = (int)Math.Min((uint)years.Count, yearsToOverlay);
        var ascending = new int[years.Count];
        years.CopyTo(ascending);

        var recent = new int[take];
        Array.Copy(ascending, ascending.Length - take, recent, 0, take);
        return recent;
    }

    private static decimal DecimalSquareRoot(decimal value)
    {
        if (value <= 0m)
        {
            return 0m;
        }

        decimal estimate = value >= 1m ? value : 1m;
        decimal convergenceTolerance = SeasonalityChartConstants.RateOfChangeEpsilon;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            decimal next = (estimate + (value / estimate)) / 2m;

            // Stop on the exact fixed point, or once the step has shrunk into the engine's numeric-noise
            // floor -- the latter also breaks the last-digit two-cycle that would otherwise run to 64.
            if (next == estimate || Math.Abs(next - estimate) <= convergenceTolerance)
            {
                return next;
            }

            estimate = next;
        }

        return estimate;
    }

    private sealed class MeanPathAccumulator
    {
        public MeanPathAccumulator(int seriesId, string seriesLabel, SeasonalityRadiusMode radiusMode)
        {
            SeriesId = seriesId;
            SeriesLabel = seriesLabel;
            RadiusMode = radiusMode;
            // One bin per canonical day-of-year (0..365), so the divisor never changes with the year.
            Sum = new decimal[SeasonalityCalendar.CanonicalDaysPerYear];
            Count = new int[SeasonalityCalendar.CanonicalDaysPerYear];
        }

        public int SeriesId { get; }

        public string SeriesLabel { get; }

        public SeasonalityRadiusMode RadiusMode { get; }

        public decimal[] Sum { get; }

        public int[] Count { get; }
    }

    private readonly record struct PendingSample(
        DateTime Timestamp,
        int CanonicalDayIndex,
        double YearFraction,
        double PlotAngleRadians,
        SeasonalitySampleStatus Status,
        decimal? Value,
        decimal? RateOfChange);

    private sealed class PendingTrace
    {
        public PendingTrace(
            int seriesId,
            string seriesLabel,
            int calendarYear,
            SeasonalityRadiusMode radiusMode,
            List<PendingSample> samples)
        {
            SeriesId = seriesId;
            SeriesLabel = seriesLabel;
            CalendarYear = calendarYear;
            RadiusMode = radiusMode;
            Samples = samples;
        }

        public int SeriesId { get; }

        public string SeriesLabel { get; }

        public int CalendarYear { get; }

        public SeasonalityRadiusMode RadiusMode { get; }

        public List<PendingSample> Samples { get; }
    }
}
