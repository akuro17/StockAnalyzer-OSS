using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// One legend row for a seasonality plot: a single overlaid calendar year. Rows are ordered
/// newest-first, so a row's index equals that year's recency rank and doubles as its colour-palette
/// slot. Shared verbatim by the polar clock and the linear year-overlay controls (confirmation Q4 —
/// the legend is unified to one row per year). <see cref="AnnualReturnLabel"/> is the pre-formatted
/// signed-percent year change ("+12.3%"), or empty when the year has no drawable base-symbol trace;
/// <see cref="Sign"/> is that value's up/down/flat bucket (<see cref="SeasonalityMonthlyReturnSign.Neutral"/>
/// when the label is empty) so the renderer can colour it the same as the Data-tab table.
/// </summary>
internal readonly record struct SeasonalityLegendEntry(
    int CalendarYear,
    string Label,
    string AnnualReturnLabel = "",
    SeasonalityMonthlyReturnSign Sign = SeasonalityMonthlyReturnSign.Neutral);

/// <summary>
/// The three Data-tab sign-bucket colours (up / down / flat) snapshotted on the UI thread for one
/// legend paint, so the render thread never reads a <c>StyledProperty</c>. <see cref="IsUnset"/> is
/// true in the design-time / preview path where no colours were supplied; the legend then draws the
/// annual return in the plain axis text colour, exactly as before this feature.
/// </summary>
internal readonly record struct SeasonalityLegendSignColors(SKColor Positive, SKColor Negative, SKColor Neutral)
{
    internal bool IsUnset => Positive.Alpha == 0 && Negative.Alpha == 0 && Neutral.Alpha == 0;

    internal SKColor ForSign(SeasonalityMonthlyReturnSign sign) => sign switch
    {
        SeasonalityMonthlyReturnSign.Positive => Positive,
        SeasonalityMonthlyReturnSign.Negative => Negative,
        _ => Neutral,
    };
}

/// <summary>
/// Per-calendar-year draw state handed from <c>SeasonalityChartViewModel</c> to the two overlay plot
/// controls: whether that year's polyline is drawn (decision D6 — an OFF year only removes its trace,
/// nothing else) and the resolved stroke width for it (the common
/// <see cref="StockAnalyzer.Core.Models.Settings.GlobalChartSettings.SeasonalityLineThickness"/> or a
/// session-only per-year override, decisions D2 / D3). Keyed by <see cref="CalendarYear"/> so a
/// re-analysis that changes the overlaid set still matches state to the right year.
/// </summary>
public readonly record struct SeasonalityYearDrawState(int CalendarYear, bool IsVisible, float EffectiveLineThickness);

/// <summary>
/// Single source of truth for the axis/label/palette helpers shared by
/// <see cref="SeasonalityPolarPlotControl"/> and <see cref="SeasonalityLinearPlotControl"/>: month
/// labels and their year-progress positions, the 1/2/5 "nice step" grid tick generator, the signed
/// percent label format, the per-year colour palette and the recency rank. Kept in one place so the
/// two renderers cannot drift apart.
/// </summary>
internal static class SeasonalitySharedAxisFormatting
{
    /// <summary>Calendar months in a year; the fixed grid division for both plots.</summary>
    internal const int MonthsPerYear = 12;

    /// <summary>Reference year used only to resolve each month's first-day canonical day-of-year offset.</summary>
    internal const int MonthOffsetReferenceYear = 2001;

    /// <summary>Divisor that turns the rho span into a target nice-step candidate.</summary>
    internal const double GridStepDivisor = 5d;

    /// <summary>Upper bound on grid ticks (concentric circles / horizontal gridlines).</summary>
    internal const int MaximumGridCircleCount = 8;

    /// <summary>Distinct hues available before the palette wraps.</summary>
    internal const int SeriesPaletteSize = 8;

    // Derived-shade coefficients for overlay years past the configurable palette (decision D1).
    // The base hue and alpha are kept; only value/saturation move, alternating lighten (odd cycle)
    // and darken (even cycle). Tunable in one place after a colour-swatch review.
    private const float ShadeStepFraction = 0.18f;
    private const float ShadeDesaturateFraction = 0.12f;
    private const float ShadeSaturateFraction = 0.10f;
    private const float ShadeMinValuePercent = 30f;
    private const float ShadeMinSaturationPercent = 20f;

    /// <summary>
    /// u in [0, 1) for the first day of each month on the canonical 366-slot basis
    /// (<see cref="SeasonalityCalendar"/>), ordered from <paramref name="startMonth"/> (1..12).
    /// Index 0 is day 1 of <paramref name="startMonth"/> (u = 0).
    /// </summary>
    internal static double[] BuildMonthStartFractions(uint startMonth = 1)
    {
        var fractions = new double[MonthsPerYear];
        for (int i = 0; i < MonthsPerYear; i++)
        {
            int month = (int)(((startMonth - 1 + i) % MonthsPerYear) + 1);
            int canonicalDayIndex = SeasonalityCalendar.CanonicalDayIndex(new DateTime(MonthOffsetReferenceYear, month, 1), startMonth);
            fractions[i] = canonicalDayIndex / (double)SeasonalityCalendar.CanonicalDaysPerYear;
        }

        return fractions;
    }

    /// <summary>Twelve abbreviated month names from the current culture, ordered from <paramref name="startMonth"/> (1..12).</summary>
    internal static string[] BuildMonthLabels(uint startMonth = 1)
    {
        string[] abbreviated = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames;
        var labels = new string[MonthsPerYear];
        for (int i = 0; i < MonthsPerYear; i++)
        {
            int month = (int)(((startMonth - 1 + i) % MonthsPerYear) + 1);
            int index = month - 1;
            labels[i] = index < abbreviated.Length && !string.IsNullOrEmpty(abbreviated[index])
                ? abbreviated[index]
                : month.ToString(CultureInfo.CurrentCulture);
        }

        return labels;
    }

    /// <summary>
    /// One legend row per overlaid calendar year, newest first (row index == recency rank). Each row
    /// carries the year's overall change (last valid sample's rate of change for series 0), pre-formatted
    /// as a signed percent; the returns rule lives in <see cref="SeasonalityMonthlyReturnsEngine"/>.
    /// </summary>
    internal static SeasonalityLegendEntry[] BuildYearLegend(SeasonalityChartResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var baseTraceByYear = new Dictionary<int, SeasonalityYearTrace>(result.CalendarYears.Count);
        foreach (SeasonalityYearTrace trace in result.Traces)
        {
            if (trace.SeriesId == 0)
            {
                baseTraceByYear[trace.CalendarYear] = trace;
            }
        }

        IReadOnlyList<int> years = result.CalendarYears; // ascending
        var entries = new SeasonalityLegendEntry[years.Count];
        for (int index = 0; index < years.Count; index++)
        {
            int year = years[years.Count - 1 - index];
            string annualLabel = string.Empty;
            SeasonalityMonthlyReturnSign sign = SeasonalityMonthlyReturnSign.Neutral;
            if (baseTraceByYear.TryGetValue(year, out SeasonalityYearTrace? baseTrace)
                && SeasonalityMonthlyReturnsEngine.AnnualReturn(baseTrace) is { } annual)
            {
                annualLabel = FormatRhoLabel(annual);
                sign = SeasonalityMonthlyReturnsPresentation.ClassifyReturnSign(annual);
            }

            entries[index] = new SeasonalityLegendEntry(year, year.ToString(CultureInfo.CurrentCulture), annualLabel, sign);
        }

        return entries;
    }

    /// <summary>Signed percent label ("+3%", "-1.5%", "0%").</summary>
    internal static string FormatRhoLabel(double rho)
        => rho.ToString("+0.#%;-0.#%;0%", CultureInfo.CurrentCulture);

    /// <summary>Signed percent label for a value already in the decimal domain (no double round-trip).</summary>
    internal static string FormatRhoLabel(decimal rho)
        => rho.ToString("+0.#%;-0.#%;0%", CultureInfo.CurrentCulture);

    /// <summary>Round <paramref name="value"/> up to the nearest 1/2/5 * 10^n.</summary>
    internal static double CalculateNiceStep(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            return 0d;
        }

        double power = Math.Pow(10d, Math.Floor(Math.Log10(value)));
        double normalized = value / power;
        return (normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d) * power;
    }

    /// <summary>
    /// Snapped rho grid ticks across [<paramref name="rhoMin"/>, <paramref name="rhoMax"/>] with a
    /// nice 1/2/5 step (or <paramref name="manualStep"/> when positive), zero snapped in when in range,
    /// capped at <see cref="MaximumGridCircleCount"/>. Both plots share this so their gridlines match;
    /// the polar plot then maps each rho tick to a radius, the linear plot uses rho directly.
    /// </summary>
    internal static (double[] RhoValues, string[] Labels) BuildRhoTicks(double rhoMin, double rhoMax, decimal? manualStep)
    {
        if (!double.IsFinite(rhoMin) || !double.IsFinite(rhoMax) || rhoMax <= rhoMin)
        {
            return (Array.Empty<double>(), Array.Empty<string>());
        }

        double step = manualStep is { } configured && configured > 0m
            ? (double)configured
            : CalculateNiceStep((rhoMax - rhoMin) / GridStepDivisor);
        if (!double.IsFinite(step) || step <= 0d)
        {
            return (Array.Empty<double>(), Array.Empty<string>());
        }

        var values = new List<double>(MaximumGridCircleCount);
        var labels = new List<string>(MaximumGridCircleCount);
        double firstTick = Math.Ceiling(rhoMin / step) * step;

        // The outermost tick must reach or pass rhoMax: stopping short (the historic `tick <= rhoMax +
        // step/2` bound only sometimes reached it) left the true data range extending past the last
        // labelled ring with nothing marking where the real boundary was, reading as traces/spokes
        // overflowing the circle even though the polar plot's clip radius was already sized correctly.
        // With the default auto step this always completes within MaximumGridCircleCount (step is never
        // smaller than (rhoMax - rhoMin) / GridStepDivisor, so at most GridStepDivisor + 1 ticks are
        // ever needed). A small manualStep can still exhaust MaximumGridCircleCount before reaching
        // rhoMax; the final permitted tick is then snapped to rhoMax itself (sacrificing its "nice
        // number" label only in that rare case) so the guarantee holds unconditionally instead of
        // silently reintroducing the same gap for that one axis-configuration path.
        for (double tick = firstTick; values.Count < MaximumGridCircleCount; tick += step)
        {
            bool isLastAllowedTick = values.Count == MaximumGridCircleCount - 1;
            double snapped = isLastAllowedTick && tick < rhoMax
                ? rhoMax
                : (Math.Abs(tick) < step / 2d ? 0d : tick);
            values.Add(snapped);
            labels.Add(FormatRhoLabel(snapped));
            if (tick >= rhoMax || snapped >= rhoMax)
            {
                break;
            }
        }

        return (values.ToArray(), labels.ToArray());
    }

    /// <summary>Hue for a year, keyed by its zero-based recency rank. Wraps after <see cref="SeriesPaletteSize"/>.</summary>
    internal static SKColor SeriesPaletteColor(int paletteIndex, ThemeColors theme)
    {
        int slot = ((paletteIndex % SeriesPaletteSize) + SeriesPaletteSize) % SeriesPaletteSize;
        return slot switch
        {
            0 => theme.RwPhase1.ToSkColor(),
            1 => theme.RwPhase2.ToSkColor(),
            2 => theme.RwPhase3.ToSkColor(),
            3 => theme.RwPhase4.ToSkColor(),
            4 => theme.RwPhase5.ToSkColor(),
            5 => theme.RwPhase6.ToSkColor(),
            6 => theme.RwPhase7.ToSkColor(),
            _ => theme.RwPhase8.ToSkColor(),
        };
    }

    /// <summary>
    /// Hue for a year keyed by its zero-based recency rank, honouring the user-configured Seasonality
    /// settings palette when one is supplied. <paramref name="overridePalette"/> is the settings page's
    /// per-year colours (ten slots); it is authoritative whenever present. When null or empty — only
    /// the manager-less / design-time path — the built-in theme palette (<see cref="SeriesPaletteColor"/>)
    /// is used instead.
    /// <para>
    /// A recency rank past the palette size does not wrap to an identical colour: the rank's base slot
    /// keeps its hue and alpha while its value/saturation shift by the derived-shade rule (decision
    /// D1 / confirmation S4b), so the 11th..20th years read as tints/shades of the 1st..10th.
    /// </para>
    /// </summary>
    internal static SKColor ResolveYearColor(int recencyIndex, IReadOnlyList<SKColor>? overridePalette, ThemeColors theme)
    {
        if (recencyIndex < 0)
        {
            recencyIndex = 0;
        }

        if (overridePalette is { Count: > 0 })
        {
            int paletteSize = overridePalette.Count;
            SKColor baseColor = overridePalette[recencyIndex % paletteSize];
            return DeriveYearShade(baseColor, recencyIndex / paletteSize);
        }

        SKColor themeBase = SeriesPaletteColor(recencyIndex % SeriesPaletteSize, theme);
        return DeriveYearShade(themeBase, recencyIndex / SeriesPaletteSize);
    }

    /// <summary>
    /// Overlay colour for a year whose recency rank is <paramref name="cycle"/> full palette lengths
    /// past its base slot. <paramref name="cycle"/> 0 returns <paramref name="baseColor"/> unchanged;
    /// higher cycles keep the base hue and alpha and alternate lighten (odd cycle) / darken (even
    /// cycle) by a deterministic value/saturation shift (decision D1). Pure function of the inputs.
    /// </summary>
    internal static SKColor DeriveYearShade(SKColor baseColor, int cycle)
    {
        if (cycle <= 0)
        {
            return baseColor;
        }

        int step = (cycle + 1) / 2;      // cycle 1,2 -> 1 ; 3,4 -> 2 ; 5,6 -> 3 ; ...
        bool lighten = (cycle & 1) == 1; // odd cycle lightens, even darkens

        baseColor.ToHsv(out float h, out float s, out float v); // h: 0..360, s/v: 0..100
        float fraction = Math.Min(1f, ShadeStepFraction * step);

        if (lighten)
        {
            v += (100f - v) * fraction;
            s -= s * (ShadeDesaturateFraction * step);
        }
        else
        {
            v -= v * fraction;
            s += (100f - s) * (ShadeSaturateFraction * step);
        }

        v = Math.Clamp(v, ShadeMinValuePercent, 100f);
        s = Math.Clamp(s, ShadeMinSaturationPercent, 100f);
        return SKColor.FromHsv(h, s, v, baseColor.Alpha);
    }

    /// <summary>
    /// Floor opacity for the least-supported mean-path segment. The mean path must stay readable as
    /// the aggregate line, so it floors above the year-trace alpha floor (120) — its stroke is
    /// thicker and dashed, but a very faint dashed line still reads as noise.
    /// </summary>
    internal const byte MeanPathMinAlpha = 140;

    /// <summary>
    /// Opacity for one mean-path segment given how many overlaid years back it
    /// (<paramref name="sampleCount"/>) out of the full overlay depth (<paramref name="overlayDepth"/>,
    /// the largest count any bin can reach). Linear from <see cref="MeanPathMinAlpha"/> at
    /// <see cref="SeasonalityChartConstants.MinYearsForMeanPath"/> supporting years up to fully opaque
    /// (255) at <paramref name="overlayDepth"/>. Returns 255 when the overlay is too shallow for the
    /// count to vary (<paramref name="overlayDepth"/> &lt;= <c>MinYearsForMeanPath</c>), which keeps the
    /// pre-P3-3 flat-opacity look. <paramref name="sampleCount"/> outside
    /// [<c>MinYearsForMeanPath</c>, <paramref name="overlayDepth"/>] is clamped.
    /// </summary>
    internal static byte MeanPathConfidenceAlpha(int sampleCount, int overlayDepth)
    {
        const int fullAlpha = byte.MaxValue;
        int minYears = SeasonalityChartConstants.MinYearsForMeanPath;
        if (overlayDepth <= minYears)
        {
            return fullAlpha;
        }

        int clamped = Math.Clamp(sampleCount, minYears, overlayDepth);
        double confidence = (clamped - minYears) / (double)(overlayDepth - minYears); // 0..1
        return (byte)Math.Round(MeanPathMinAlpha + (confidence * (fullAlpha - MeanPathMinAlpha)));
    }

    /// <summary>
    /// Position within the ascending <paramref name="calendarYears"/> list counted from the most
    /// recent (0 = newest). Ranks trace prominence and picks a palette slot.
    /// </summary>
    internal static int RecencyRank(IReadOnlyList<int> calendarYears, int calendarYear, int yearCount)
    {
        for (int index = 0; index < calendarYears.Count; index++)
        {
            if (calendarYears[index] == calendarYear)
            {
                return (yearCount - 1) - index;
            }
        }

        return 0;
    }
}
