using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SeasonalityPolarPlotLayoutTests
{
    [Fact]
    public void Build_MonthSpokes_StartAtJanuaryApexAndRunClockwise()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult());

        Assert.Equal(SeasonalitySharedAxisFormatting.MonthsPerYear, layout.MonthAnglesRadians.Length);

        // January -> 12 o'clock: cos(theta) ~ 0, sin(theta) ~ 1.
        Assert.Equal(0d, Math.Cos(layout.MonthAnglesRadians[0]), 12);
        Assert.Equal(1d, Math.Sin(layout.MonthAnglesRadians[0]), 12);

        for (int index = 1; index < layout.MonthAnglesRadians.Length; index++)
        {
            Assert.True(
                layout.MonthAnglesRadians[index] < layout.MonthAnglesRadians[index - 1],
                $"month spoke {index} should be clockwise from spoke {index - 1}");
        }
    }

    [Fact]
    public void Build_MonthSpokeAngles_MatchCanonicalDayIndexOverThreeSixtySix()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult());

        // 1 April is canonical index 91 of 366 (day 90, shifted +1 from March on).
        double expectedApril = (Math.PI / 2d) - (2d * Math.PI * (91d / 366d));
        Assert.Equal(expectedApril, layout.MonthAnglesRadians[3], 12);
    }

    [Fact]
    public void Build_MonthLabels_ComeFromCurrentCultureAbbreviations()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult());

        string[] abbreviated = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames;
        Assert.Equal(SeasonalitySharedAxisFormatting.MonthsPerYear, layout.MonthLabels.Length);
        Assert.All(layout.MonthLabels, label => Assert.False(string.IsNullOrEmpty(label)));
        Assert.Equal(abbreviated[0], layout.MonthLabels[0]);
        Assert.Equal(abbreviated[11], layout.MonthLabels[11]);
    }

    [Fact]
    public void Build_RhoGrid_IsAscendingBoundedAndCarriesPercentLabels()
    {
        SeasonalityChartResult result = BuildResult();

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result);

        Assert.NotEmpty(layout.GridRadii);
        Assert.Equal(layout.GridRadii.Length, layout.GridLabels.Length);
        Assert.True(layout.GridRadii.Length <= SeasonalitySharedAxisFormatting.MaximumGridCircleCount);
        for (int index = 1; index < layout.GridRadii.Length; index++)
        {
            Assert.True(layout.GridRadii[index] > layout.GridRadii[index - 1]);
        }

        // The outermost tick is guaranteed to reach or pass MaxRadius (the outermost tick may legitimately
        // sit up to just under one nice-step past it — this is the fix for traces/spokes appearing to
        // overflow the last labelled ring), and the innermost tick never sits below MinRadius.
        Assert.True(layout.GridRadii[^1] >= result.MaxRadius - 1e-6);
        Assert.All(layout.GridRadii, radius => Assert.True(radius >= result.MinRadius - 1e-6));
        Assert.All(layout.GridLabels, label => Assert.EndsWith("%", label));
    }

    [Fact]
    public void Build_RhoGrid_HonoursManualStep()
    {
        SeasonalityChartResult result = BuildResult();

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, manualRhoStep: 0.05m);

        double stepInRadius = 0.05d * result.ScaleFactor;
        for (int index = 1; index < layout.GridRadii.Length; index++)
        {
            Assert.Equal(stepInRadius, layout.GridRadii[index] - layout.GridRadii[index - 1], 6);
        }
    }

    [Fact]
    public void Build_UseVisibleRangeOrigin_False_KeepsCentreOrigin()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult(), useVisibleRangeOrigin: false);

        Assert.Equal(0d, layout.DisplayBaseRadius);
    }

    [Fact]
    public void Build_UseVisibleRangeOrigin_RaisesDisplayBaseIntoTheDataRing()
    {
        SeasonalityChartResult result = BuildResult();

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, useVisibleRangeOrigin: true);

        Assert.True(result.MinRadius > 0d);
        Assert.True(layout.DisplayBaseRadius > 0d);
        Assert.True(layout.DisplayBaseRadius < result.MaxRadius);
        Assert.Equal(
            result.MinRadius * (1d - SeasonalityPolarPlotLayoutConstants.DisplayOriginPaddingFraction),
            layout.DisplayBaseRadius,
            9);
        Assert.Equal(result.MaxRadius - layout.DisplayBaseRadius, layout.RadialSpan, 9);
    }

    [Fact]
    public void Build_EmptyResult_YieldsZeroSpanAndNoGrid()
    {
        var empty = new SeasonalityChartResult(
            new SeasonalityChartParameters(5),
            Array.Empty<SeasonalityYearTrace>(),
            baseRadius: 0d,
            scaleFactor: 0d,
            minRadius: 0d,
            maxRadius: 0d,
            calendarYears: Array.Empty<int>());

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(empty);

        Assert.Equal(0d, layout.RadialSpan);
        Assert.Empty(layout.GridRadii);
        Assert.Empty(layout.GridLabels);
        Assert.Equal(SeasonalitySharedAxisFormatting.MonthsPerYear, layout.MonthLabels.Length);
    }

    [Theory]
    [InlineData(0.7d, 1d)]
    [InlineData(3d, 5d)]
    [InlineData(12d, 20d)]
    public void CalculateNiceStep_UsesOneTwoFive(double input, double expected)
    {
        Assert.Equal(expected, SeasonalitySharedAxisFormatting.CalculateNiceStep(input), 10);
    }

    [Fact]
    public void RecencyRank_CountsFromMostRecentYear()
    {
        int[] years = { 2021, 2022, 2023 };

        Assert.Equal(0, SeasonalitySharedAxisFormatting.RecencyRank(years, 2023, years.Length));
        Assert.Equal(1, SeasonalitySharedAxisFormatting.RecencyRank(years, 2022, years.Length));
        Assert.Equal(2, SeasonalitySharedAxisFormatting.RecencyRank(years, 2021, years.Length));
    }

    [Fact]
    public void Build_Legend_HasOneRowPerCalendarYearNewestFirst()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult());

        Assert.Equal(new[] { 2023, 2022, 2021 }, layout.LegendEntries.Select(e => e.CalendarYear).ToArray());
        Assert.Equal(new[] { "2023", "2022", "2021" }, layout.LegendEntries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Build_Legend_CarriesTheAnnualReturnLabelForTheBaseSeries()
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "BASE",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 100m),
                new SeasonalityPoint(new DateTime(2023, 12, 29), 112m),
            });

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(
            SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3)));

        SeasonalityLegendEntry entry = Assert.Single(layout.LegendEntries);
        Assert.Equal("+12%", entry.AnnualReturnLabel);
    }

    [Fact]
    public void Build_Legend_AnnualReturnLabelIsEmptyWhenThereIsNoBaseSeriesTrace()
    {
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(BuildResult());

        Assert.All(layout.LegendEntries, e => Assert.Equal(string.Empty, e.AnnualReturnLabel));
        // No drawable annual value -> the neutral bucket (improvement C-2).
        Assert.All(layout.LegendEntries, e => Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, e.Sign));
    }

    [Theory]
    [InlineData(112, SeasonalityMonthlyReturnSign.Positive)]
    [InlineData(88, SeasonalityMonthlyReturnSign.Negative)]
    [InlineData(100, SeasonalityMonthlyReturnSign.Neutral)]
    public void Build_Legend_TagsAnnualReturnSign(int yearEndValue, SeasonalityMonthlyReturnSign expectedSign)
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "BASE",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 100m),
                new SeasonalityPoint(new DateTime(2023, 12, 29), yearEndValue),
            });

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(
            SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3)));

        SeasonalityLegendEntry entry = Assert.Single(layout.LegendEntries);
        Assert.Equal(expectedSign, entry.Sign);
    }

    [Fact]
    public void SeasonalityLegendSignColors_IsUnset_OnlyWhenEveryBucketIsTransparent()
    {
        Assert.True(default(SeasonalityLegendSignColors).IsUnset);

        var colors = new SeasonalityLegendSignColors(
            new SKColor(0x2E, 0x7D, 0x32), new SKColor(0xC6, 0x28, 0x28), new SKColor(0x75, 0x75, 0x75));
        Assert.False(colors.IsUnset);
        Assert.Equal(colors.Positive, colors.ForSign(SeasonalityMonthlyReturnSign.Positive));
        Assert.Equal(colors.Negative, colors.ForSign(SeasonalityMonthlyReturnSign.Negative));
        Assert.Equal(colors.Neutral, colors.ForSign(SeasonalityMonthlyReturnSign.Neutral));
    }

    [Fact]
    public void Build_Legend_SingleYearYieldsSingleRow()
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 100m),
                new SeasonalityPoint(new DateTime(2023, 7, 1), 110m),
            });

        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(
            SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3)));

        SeasonalityLegendEntry entry = Assert.Single(layout.LegendEntries);
        Assert.Equal(2023, entry.CalendarYear);
        Assert.Equal("2023", entry.Label);
    }

    [Fact]
    public void LegendIndex_ReturnsZeroBasedRowPositionForACalendarYear()
    {
        SeasonalityLegendEntry[] entries = { new(2023, "2023"), new(2022, "2022") };

        Assert.Equal(0, SeasonalityPolarPlotLayoutBuilder.LegendIndex(entries, 2023));
        Assert.Equal(1, SeasonalityPolarPlotLayoutBuilder.LegendIndex(entries, 2022));
        Assert.Equal(0, SeasonalityPolarPlotLayoutBuilder.LegendIndex(entries, 1999));
    }

    [Fact]
    public void SeriesPaletteColor_GivesDistinctHuesPerSlotAndWrapsAfterEight()
    {
        ThemeColors theme = ThemeColors.Dark;

        SKColor first = SeasonalitySharedAxisFormatting.SeriesPaletteColor(0, theme);
        SKColor second = SeasonalitySharedAxisFormatting.SeriesPaletteColor(1, theme);

        Assert.NotEqual(first, second);
        Assert.Equal(first, SeasonalitySharedAxisFormatting.SeriesPaletteColor(SeasonalitySharedAxisFormatting.SeriesPaletteSize, theme));
    }

    [Fact]
    public void TryProjectPoint_JanuaryApexProjectsStraightUp()
    {
        bool projected = SeasonalityPolarPlotControl.TryProjectPoint(
            radius: 100d,
            angleRadians: Math.PI / 2d,
            centerX: 200f,
            centerY: 200f,
            scale: 1d,
            displayBaseRadius: 0d,
            out SKPoint point);

        Assert.True(projected);
        Assert.Equal(200f, point.X, 3);
        Assert.Equal(100f, point.Y, 3);
    }

    [Fact]
    public void TryProjectPoint_RejectsNanRadiusAndRadiusBelowDisplayBase()
    {
        Assert.False(SeasonalityPolarPlotControl.TryProjectPoint(double.NaN, 0d, 0f, 0f, 1d, 0d, out _));
        Assert.False(SeasonalityPolarPlotControl.TryProjectPoint(5d, 0d, 0f, 0f, 1d, 10d, out _));
    }

    [Theory]
    [InlineData(600d)]
    [InlineData(400d)]
    [InlineData(220d)]
    [InlineData(120d)]
    public void ResolvePolarOuterRadius_LeavesTheMonthLabelReserveInsideTheViewport(double shorterEdge)
    {
        const double reserve = 24d;

        double outerRadius = SeasonalityPolarPlotControl.ResolvePolarOuterRadius(shorterEdge, reserve);

        Assert.True(outerRadius > 0d);
        // The ring plus the reserved label band never reaches the viewport edge (decision C3).
        Assert.True(outerRadius + reserve <= (shorterEdge / 2d) + 1e-9);
        // Never grows past the historic fill-fraction radius.
        Assert.True(outerRadius <= (shorterEdge * SeasonalityPolarPlotLayoutConstants.RadialFillFraction) + 1e-9);
    }

    [Fact]
    public void ResolvePolarOuterRadius_GoesNonPositiveWhenTheViewportCannotFitTheLabels()
    {
        // 40px shorter edge: half of it (20) is below a 24px label reserve, so the frame is skipped.
        Assert.True(SeasonalityPolarPlotControl.ResolvePolarOuterRadius(40d, 24d) <= 0d);
    }

    [Fact]
    public void ResolvePolarRingReserve_DoesNotDependOnTheAxisFontSize()
    {
        // No rho tick past rhoMax (maxGridDisplayRadius == radialSpan): the only reservation past the
        // ring is the fixed month-label gap, whatever the axis font size. The ring size is therefore
        // independent of the axis font (the Polar circle must not shrink as Axis Font Size grows).
        double reserve = SeasonalityPolarPlotControl.ResolvePolarRingReserve(
            shorterViewportEdge: 500d, radialSpan: 10d, maxGridDisplayRadius: 10d);

        Assert.Equal(4d, reserve, 6);
    }

    [Fact]
    public void ResolvePolarRingReserve_AddsOnlyTheFontIndependentTickOverhangPastRadialSpan()
    {
        // A rho tick sitting 20% past rhoMax: the reservation grows by that fraction of the fill-radius
        // (still purely geometric, no font term) plus the fixed month-label gap.
        double reserve = SeasonalityPolarPlotControl.ResolvePolarRingReserve(
            shorterViewportEdge: 500d, radialSpan: 10d, maxGridDisplayRadius: 12d);

        double expectedOverhang = 500d * SeasonalityPolarPlotLayoutConstants.RadialFillFraction * 0.2d;
        Assert.Equal(expectedOverhang + 4d, reserve, 6);
    }

    [Fact]
    public void MeasureRadialMonthLabelRect_AnchorsCardinalMonthLabelsJustOutsideTheOuterRing()
    {
        // The spokes now stop at the outer ring; a month label is anchored MonthLabelGap beyond it.
        const float outerRadius = 150f;
        const float monthLabelGap = 4f;
        const float labelRadius = outerRadius + monthLabelGap;
        const float centerX = 200f;
        const float centerY = 200f;
        const float ascent = -14f;
        const float descent = 4f;
        const float width = 26f;
        const float fullHeight = descent - ascent;

        // January spoke points straight up: the whole rectangle sits above the centre, out near the ring.
        SKRect january = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(
            0f, 1f, labelRadius, centerX, centerY, width, ascent, descent);
        Assert.True(january.Bottom <= centerY - (labelRadius - fullHeight) + 0.01f,
            $"January bottom {january.Bottom} drifted back inside the ring");
        Assert.True(january.Top >= centerY - labelRadius - fullHeight - 0.01f,
            $"January top {january.Top} overshot the ring");

        // July spoke points straight down: the whole rectangle sits below the centre, out near the ring.
        SKRect july = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(
            0f, -1f, labelRadius, centerX, centerY, width, ascent, descent);
        Assert.True(july.Top >= centerY + (labelRadius - fullHeight) - 0.01f,
            $"July top {july.Top} drifted back inside the ring");
        Assert.True(july.Bottom <= centerY + labelRadius + fullHeight + 0.01f,
            $"July bottom {july.Bottom} overshot the ring");
    }

    [Fact]
    public void MeasureRadialMonthLabelRect_DropsAprilAndOctoberBelowTheHorizontalLine()
    {
        const float centerX = 200f;
        const float centerY = 150f;
        const float ascent = -12f;
        const float descent = 4f;
        const float radius = 90f;
        const float width = 30f;

        // April: spoke points right (cos ~ +1, sin ~ 0).
        SKRect april = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(1f, 0f, radius, centerX, centerY, width, ascent, descent);
        Assert.True(april.Top > centerY, $"April top {april.Top} should sit below the centre line {centerY}");

        // October: spoke points left (cos ~ -1, sin ~ 0).
        SKRect october = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(-1f, 0f, radius, centerX, centerY, width, ascent, descent);
        Assert.True(october.Top > centerY, $"October top {october.Top} should sit below the centre line {centerY}");

        // January (vertical spoke, sin ~ +1) is unaffected: it still sits above the centre.
        SKRect january = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(0f, 1f, radius, centerX, centerY, width, ascent, descent);
        Assert.True(january.Bottom < centerY, $"January bottom {january.Bottom} should stay above the centre line {centerY}");
    }

    [Theory]
    [InlineData(1u, 300d)]
    [InlineData(4u, 300d)]
    [InlineData(7u, 300d)]
    [InlineData(10u, 300d)]
    [InlineData(1u, 400d)]
    [InlineData(4u, 400d)]
    [InlineData(7u, 400d)]
    [InlineData(10u, 400d)]
    [InlineData(4u, 500d)]
    public void Layout_AcrossStartMonthsAndDimensions_LabelsRemainWithinViewportBounds(uint startMonth, double edge)
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 1,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: MonthlyPoints());
        var parameters = new SeasonalityChartParameters(YearsToOverlay: 3, StartMonth: startMonth);
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, parameters);
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result);

        double maxGridDisplayRadius = 0d;
        for (int i = 0; i < layout.GridRadii.Length; i++)
        {
            maxGridDisplayRadius = Math.Max(maxGridDisplayRadius, layout.GridRadii[i] - layout.DisplayBaseRadius);
        }

        double ringReserve = SeasonalityPolarPlotControl.ResolvePolarRingReserve(edge, layout.RadialSpan, maxGridDisplayRadius);
        double outerRadius = SeasonalityPolarPlotControl.ResolvePolarOuterRadius(edge, ringReserve);

        double gridOverhangFactor = layout.RadialSpan > 0d ? Math.Max(1d, maxGridDisplayRadius / layout.RadialSpan) : 1d;
        const float axisTextSize = 11f;
        outerRadius = SeasonalityPolarPlotControl.ResolveFontConstrainedOuterRadius(outerRadius, edge, gridOverhangFactor, axisTextSize);
        if (outerRadius <= 0d)
        {
            return;
        }

        double scale = outerRadius / layout.RadialSpan;
        float spokeOuter = (float)(Math.Max(layout.RadialSpan, maxGridDisplayRadius) * scale);
        float labelRadius = spokeOuter + SeasonalityPolarPlotControl.MonthLabelGap;
        float centerX = (float)(edge / 2d);
        float centerY = (float)(edge / 2d);
        const float ascent = -11.5f;
        const float descent = 3.5f;

        for (int index = 0; index < layout.MonthAnglesRadians.Length; index++)
        {
            double angle = layout.MonthAnglesRadians[index];
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            string label = layout.MonthLabels[index];
            float width = label.Length * 7f;

            SKRect rect = SeasonalityPolarPlotControl.MeasureRadialMonthLabelRect(
                cos, sin, labelRadius, centerX, centerY, width, ascent, descent);

            Assert.True(rect.Top >= 0f, $"Month {label} (StartMonth={startMonth}) top {rect.Top} is clipped above 0 at edge {edge}");
            Assert.True(rect.Bottom <= edge, $"Month {label} (StartMonth={startMonth}) bottom {rect.Bottom} is clipped below {edge}");
        }
    }

    // Regression coverage for the reported "Start Month causes the reference frame to overflow the
    // circular boundary" defect: mirrors RenderSkia's outerRadius/spokeOuter derivation verbatim and
    // asserts the outermost grid ring/spoke radius itself (not just the month label rectangle) stays
    // inside the viewport, across every StartMonth and a small-viewport/large-font combination that
    // previously defeated the font-based cap's now-removed conditional guard.
    [Theory]
    [InlineData(1u, 400d, 11f)]
    [InlineData(2u, 400d, 11f)]
    [InlineData(3u, 400d, 11f)]
    [InlineData(4u, 400d, 11f)]
    [InlineData(5u, 400d, 11f)]
    [InlineData(6u, 400d, 11f)]
    [InlineData(7u, 400d, 11f)]
    [InlineData(8u, 400d, 11f)]
    [InlineData(9u, 400d, 11f)]
    [InlineData(10u, 400d, 11f)]
    [InlineData(11u, 400d, 11f)]
    [InlineData(12u, 400d, 11f)]
    [InlineData(1u, 80d, 24f)]
    [InlineData(4u, 80d, 24f)]
    [InlineData(7u, 80d, 24f)]
    [InlineData(10u, 80d, 24f)]
    [InlineData(1u, 40d, 24f)]
    [InlineData(4u, 40d, 24f)]
    public void RenderSkia_ReferenceFrame_NeverOverflowsTheViewportAcrossStartMonthsAndFontSizes(
        uint startMonth, double edge, float axisTextSize)
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 1,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: SkewedYearlyPoints());
        var parameters = new SeasonalityChartParameters(YearsToOverlay: 3, StartMonth: startMonth);
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, parameters);
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result);

        double maxGridDisplayRadius = 0d;
        for (int i = 0; i < layout.GridRadii.Length; i++)
        {
            maxGridDisplayRadius = Math.Max(maxGridDisplayRadius, layout.GridRadii[i] - layout.DisplayBaseRadius);
        }

        double ringReserve = SeasonalityPolarPlotControl.ResolvePolarRingReserve(edge, layout.RadialSpan, maxGridDisplayRadius);
        double outerRadius = SeasonalityPolarPlotControl.ResolvePolarOuterRadius(edge, ringReserve);
        if (outerRadius <= 0d)
        {
            return; // RenderSkia skips the frame entirely rather than draw a degenerate circle.
        }

        // Calls the production SSoT (SeasonalityPolarPlotControl.ResolveFontConstrainedOuterRadius)
        // rather than re-deriving the font-based-cap formula by hand: an earlier hand-copied version of
        // this formula in this test drifted out of sync with production once this session and reported
        // a false failure, which this call eliminates as a possibility going forward.
        double gridOverhangFactor = layout.RadialSpan > 0d ? Math.Max(1d, maxGridDisplayRadius / layout.RadialSpan) : 1d;
        outerRadius = SeasonalityPolarPlotControl.ResolveFontConstrainedOuterRadius(outerRadius, edge, gridOverhangFactor, axisTextSize);

        if (outerRadius <= 0d)
        {
            return;
        }

        // References the same production constants ResolveFontConstrainedOuterRadius uses (rather than
        // hardcoding a mirror) so this test's own pass/fail geometric claim below cannot drift out of
        // sync if those constants ever change.
        float requiredLabelMargin = SeasonalityPolarPlotControl.MonthLabelGap
            + (axisTextSize * SeasonalityPolarPlotControl.FontLineHeightMultiplier)
            + SeasonalityPolarPlotControl.MonthLabelSafetyPadding;

        double scale = outerRadius / layout.RadialSpan;
        float spokeOuter = (float)(Math.Max(layout.RadialSpan, maxGridDisplayRadius) * scale);

        // The reference frame's outer ring/spokes plus the font-driven label margin must stay inside
        // the viewport half-edge: this is the geometric statement of "never overflows the circle".
        Assert.True(
            spokeOuter + requiredLabelMargin <= (edge / 2d) + 0.5,
            $"StartMonth={startMonth}, edge={edge}, axisTextSize={axisTextSize}: spokeOuter={spokeOuter} + requiredLabelMargin={requiredLabelMargin} exceeds half-edge={edge / 2d}");
    }

    /// <summary>Three years of a series with a sharp January rally and flat remainder, producing a
    /// heavily skewed rho distribution (large positive tail, near-zero elsewhere) so BuildRhoTicks can
    /// emit a grid tick that overhangs materially past rhoMax for at least some StartMonth rotations.</summary>
    private static IReadOnlyList<SeasonalityPoint> SkewedYearlyPoints()
    {
        var points = new List<SeasonalityPoint>();
        for (int year = 2021; year <= 2023; year++)
        {
            decimal value = 100m;
            for (int month = 1; month <= 12; month++)
            {
                if (month == 1)
                {
                    value *= 1.6m; // sharp rally right at the calendar-year start
                }
                else
                {
                    value *= 1.002m; // near-flat afterwards
                }

                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
            }
        }

        return points;
    }

    private static SeasonalityChartResult BuildResult()
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 1,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: MonthlyPoints());

        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3));
    }

    private static IReadOnlyList<SeasonalityPoint> MonthlyPoints()
    {
        var points = new List<SeasonalityPoint>();
        decimal value = 100m;
        for (int year = 2021; year <= 2023; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
                value += month % 2 == 0 ? 3m : -1m;
            }
        }

        return points;
    }
}
