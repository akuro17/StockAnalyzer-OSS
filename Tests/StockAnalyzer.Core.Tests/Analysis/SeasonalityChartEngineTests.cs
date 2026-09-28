using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SeasonalityChartEngineTests
{
    [Fact]
    public void Analyze_January1_MapsToTwelveOClockApex()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m, 101m) },
            new SeasonalityChartParameters(5));

        SeasonalitySample first = result.Traces.Single().Samples[0];
        Assert.Equal(0d, first.YearFraction, 12);
        Assert.Equal(Math.PI / 2d, first.PlotAngleRadians, 12);
        Assert.Equal(0, first.CanonicalDayIndex);
    }

    [Fact]
    public void Analyze_PlotAngle_DecreasesClockwiseAsTheYearAdvances()
    {
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 4, 1), 110m),
            (new DateTime(2023, 7, 1), 120m),
            (new DateTime(2023, 10, 1), 130m));

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(5));

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].PlotAngleRadians < samples[i - 1].PlotAngleRadians,
                $"angle at index {i} should be smaller (clockwise) than index {i - 1}");
        }

        // 1 April 2023 (non-leap) is canonical index 91 of 366 (day 90, shifted +1 from March on).
        Assert.Equal((Math.PI / 2d) - (2d * Math.PI * (91d / 366d)), samples[1].PlotAngleRadians, 12);
    }

    [Theory]
    [InlineData(2024)] // leap
    [InlineData(2023)] // non-leap: 31 December still lands on canonical slot 365
    public void Analyze_Dec31_MapsToCanonicalIndex365RegardlessOfLeapYear(int year)
    {
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(year, 1, 1), 100m),
            (new DateTime(year, 12, 31), 150m));

        SeasonalitySample yearEnd = SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5))
            .Traces.Single().Samples[1];

        Assert.Equal(365, yearEnd.CanonicalDayIndex);
        Assert.Equal(365d / 366d, yearEnd.YearFraction, 12);
        Assert.True(yearEnd.YearFraction < 1d);
    }

    [Fact]
    public void Analyze_SameCalendarDate_InLeapAndNonLeapYears_ShareTheSamePlotAngle()
    {
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 3, 15), 110m),
            (new DateTime(2024, 1, 1), 100m),
            (new DateTime(2024, 3, 15), 120m));

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(5));

        SeasonalitySample nonLeapMar15 = result.Traces.Single(t => t.CalendarYear == 2023).Samples[1];
        SeasonalitySample leapMar15 = result.Traces.Single(t => t.CalendarYear == 2024).Samples[1];

        Assert.Equal(nonLeapMar15.CanonicalDayIndex, leapMar15.CanonicalDayIndex);
        Assert.Equal(nonLeapMar15.PlotAngleRadians, leapMar15.PlotAngleRadians, 12);
    }

    [Fact]
    public void Analyze_SplitsOneSeriesIntoOneTracePerCalendarYear()
    {
        SeasonalitySeriesInput series = SeriesAt(
            7,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2021, 1, 4), 100m),
            (new DateTime(2021, 6, 1), 110m),
            (new DateTime(2022, 1, 3), 200m),
            (new DateTime(2022, 6, 1), 190m),
            (new DateTime(2023, 1, 2), 300m),
            (new DateTime(2023, 6, 1), 330m));

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(10));

        Assert.Equal(new[] { 2021, 2022, 2023 }, result.CalendarYears.ToArray());
        Assert.Equal(3, result.Traces.Count);
        Assert.All(result.Traces, trace => Assert.Equal(7, trace.SeriesId));
        Assert.Equal(new[] { 2021, 2022, 2023 }, result.Traces.Select(t => t.CalendarYear).ToArray());
    }

    [Fact]
    public void Analyze_YearsToOverlay_KeepsOnlyTheMostRecentYears()
    {
        var points = new List<(DateTime, decimal?)>();
        for (int year = 2019; year <= 2023; year++)
        {
            points.Add((new DateTime(year, 1, 2), 100m));
            points.Add((new DateTime(year, 7, 1), 105m));
        }

        SeasonalitySeriesInput series = SeriesAt(1, SeasonalityRadiusMode.PercentVsYearStart, points.ToArray());

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(2));

        Assert.Equal(new[] { 2022, 2023 }, result.CalendarYears.ToArray());
        Assert.Equal(new[] { 2022, 2023 }, result.Traces.Select(t => t.CalendarYear).ToArray());
    }

    [Fact]
    public void Analyze_PercentVsYearStart_ComputesRateOfChangeFromFirstPositiveValue()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m, 110m, 90m) },
            new SeasonalityChartParameters(5));

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.Equal(0m, samples[0].RateOfChange);
        Assert.Equal(0.1m, samples[1].RateOfChange);
        Assert.Equal(-0.1m, samples[2].RateOfChange);
        Assert.All(samples, s => Assert.Equal(SeasonalitySampleStatus.Valid, s.Status));
    }

    [Fact]
    public void Analyze_PercentVsYearStart_ForwardSearchesBaseOverMissingLeadingValues()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, null, 100m, 120m) },
            new SeasonalityChartParameters(5));

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.Equal(SeasonalitySampleStatus.InsufficientData, samples[0].Status);
        Assert.Null(samples[0].RateOfChange);
        Assert.Equal(0m, samples[1].RateOfChange);
        Assert.Equal(0.2m, samples[2].RateOfChange);
    }

    [Fact]
    public void Analyze_PercentVsYearStart_NoPositiveValueInYear_MarksWholeTraceNonPositiveBase()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 0m, -1m) },
            new SeasonalityChartParameters(5));

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.All(samples, s => Assert.Equal(SeasonalitySampleStatus.NonPositiveBase, s.Status));
        Assert.All(samples, s => Assert.Null(s.RateOfChange));
        Assert.All(samples, s => Assert.True(double.IsNaN(s.Radius)));
    }

    [Fact]
    public void Analyze_SignedUnitFromBounds_MapsMidpointToZeroAndEdgesToPlusMinusOne()
    {
        var series = new SeasonalitySeriesInput(
            1,
            "RSI",
            SeasonalityRadiusMode.SignedUnitFromBounds,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 0m),
                new SeasonalityPoint(new DateTime(2023, 1, 3), 50m),
                new SeasonalityPoint(new DateTime(2023, 1, 4), 100m),
            },
            boundsLow: 0m,
            boundsHigh: 100m);

        IReadOnlyList<SeasonalitySample> samples = SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5))
            .Traces.Single().Samples;

        Assert.Equal(-1m, samples[0].RateOfChange);
        Assert.Equal(0m, samples[1].RateOfChange);
        Assert.Equal(1m, samples[2].RateOfChange);
    }

    [Fact]
    public void Analyze_SignedUnitFromBounds_WithoutValidBounds_Throws()
    {
        var series = new SeasonalitySeriesInput(
            1,
            "bad",
            SeasonalityRadiusMode.SignedUnitFromBounds,
            new[] { new SeasonalityPoint(new DateTime(2023, 1, 2), 10m) });

        Assert.Throws<ArgumentException>(() =>
            SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(5)));
    }

    [Fact]
    public void Analyze_ZScoreWithinYear_StandardizesAgainstYearMeanAndStdev()
    {
        var series = new SeasonalitySeriesInput(
            1,
            "z",
            SeasonalityRadiusMode.ZScoreWithinYear,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 10m),
                new SeasonalityPoint(new DateTime(2023, 1, 3), 20m),
                new SeasonalityPoint(new DateTime(2023, 1, 4), 30m),
            });

        IReadOnlyList<SeasonalitySample> samples = SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5))
            .Traces.Single().Samples;

        double expectedStdev = Math.Sqrt(200d / 3d);
        Assert.Equal(-10d / expectedStdev, (double)samples[0].RateOfChange!.Value, 9);
        Assert.Equal(0d, (double)samples[1].RateOfChange!.Value, 9);
        Assert.Equal(10d / expectedStdev, (double)samples[2].RateOfChange!.Value, 9);
    }

    [Fact]
    public void Analyze_ZScoreWithinYear_ConstantYear_MarksZeroVariance()
    {
        var series = new SeasonalitySeriesInput(
            1,
            "flat",
            SeasonalityRadiusMode.ZScoreWithinYear,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 5m),
                new SeasonalityPoint(new DateTime(2023, 1, 3), 5m),
                new SeasonalityPoint(new DateTime(2023, 1, 4), 5m),
            });

        IReadOnlyList<SeasonalitySample> samples = SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5))
            .Traces.Single().Samples;

        Assert.All(samples, s => Assert.Equal(SeasonalitySampleStatus.ZeroVariance, s.Status));
    }

    [Fact]
    public void Analyze_ZScoreWithinYear_TooFewValidValues_MarksInsufficientSamples()
    {
        var series = new SeasonalitySeriesInput(
            1,
            "sparse",
            SeasonalityRadiusMode.ZScoreWithinYear,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 5m),
                new SeasonalityPoint(new DateTime(2023, 1, 3), null),
                new SeasonalityPoint(new DateTime(2023, 1, 4), null),
            });

        IReadOnlyList<SeasonalitySample> samples = SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5))
            .Traces.Single().Samples;

        // One valid value < MinValidSamplesForZScore -> the whole year is InsufficientSamples, not
        // ZeroVariance and not the missing-value InsufficientData.
        Assert.Equal(SeasonalitySampleStatus.InsufficientSamples, samples[0].Status);
        Assert.Equal(SeasonalitySampleStatus.InsufficientData, samples[1].Status);
        Assert.Equal(SeasonalitySampleStatus.InsufficientData, samples[2].Status);
    }

    [Fact]
    public void Analyze_AutoScale_MapsGlobalRhoRangeIntoFixedRadialBand()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m, 110m, 90m) },
            new SeasonalityChartParameters(5));

        // rho in [-0.1, 0.1] -> span 0.2 -> K = 100 / 0.2 = 500, R0 = 10 - 500 * (-0.1) = 60.
        Assert.Equal(500d, result.ScaleFactor, 9);
        Assert.Equal(60d, result.BaseRadius, 9);
        Assert.Equal(10d, result.MinRadius, 9);
        Assert.Equal(110d, result.MaxRadius, 9);

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.Equal(60d, samples[0].Radius, 9);
        Assert.Equal(110d, samples[1].Radius, 9);
        Assert.Equal(10d, samples[2].Radius, 9);
    }

    [Fact]
    public void Analyze_ManualScaleAndBaseRadius_AreUsedVerbatim()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m, 110m, 90m) },
            new SeasonalityChartParameters(5, ManualBaseRadius: 100m, ManualScaleFactor: 1000m));

        Assert.Equal(100d, result.BaseRadius, 9);
        Assert.Equal(1000d, result.ScaleFactor, 9);

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.Equal(100d, samples[0].Radius, 9);
        Assert.Equal(200d, samples[1].Radius, 9);
        Assert.Equal(0d, samples[2].Radius, 9);
    }

    [Fact]
    public void Analyze_ManualParameters_NegativeMappedRadius_MarksRadiusUnderflow()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m, 90m) },
            new SeasonalityChartParameters(5, ManualBaseRadius: 0m, ManualScaleFactor: 1000m));

        IReadOnlyList<SeasonalitySample> samples = result.Traces.Single().Samples;
        Assert.Equal(SeasonalitySampleStatus.Valid, samples[0].Status);
        Assert.Equal(0d, samples[0].Radius, 9);
        Assert.Equal(SeasonalitySampleStatus.RadiusUnderflow, samples[1].Status);
        Assert.True(double.IsNaN(samples[1].Radius));
        Assert.Equal(-0.1m, samples[1].RateOfChange);
    }

    [Fact]
    public void Analyze_SharesOneAutoScaleAcrossEverySeriesAndYear()
    {
        SeasonalitySeriesInput price = PriceSeriesFromYearStart(1, 2023, 100m, 120m);
        var oscillator = new SeasonalitySeriesInput(
            2,
            "osc",
            SeasonalityRadiusMode.SignedUnitFromBounds,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 5), 50m),
                new SeasonalityPoint(new DateTime(2023, 2, 5), 50m),
            },
            boundsLow: 0m,
            boundsHigh: 100m);

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { price, oscillator },
            new SeasonalityChartParameters(5));

        // rho range is [0, 0.2] -> span 0.2 -> K = 500, R0 = 10 - 500 * 0 = 10.
        Assert.Equal(500d, result.ScaleFactor, 9);
        Assert.Equal(10d, result.BaseRadius, 9);

        SeasonalityYearTrace oscillatorTrace = result.Traces.Single(t => t.SeriesId == 2);
        Assert.All(oscillatorTrace.Samples, s => Assert.Equal(10d, s.Radius, 9));
    }

    [Fact]
    public void Analyze_OrdersTracesBySeriesIdThenCalendarYear()
    {
        SeasonalitySeriesInput seriesB = SeriesAt(
            2,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2022, 1, 3), 10m),
            (new DateTime(2023, 1, 3), 20m));
        SeasonalitySeriesInput seriesA = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2022, 1, 3), 10m),
            (new DateTime(2023, 1, 3), 20m));

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { seriesB, seriesA },
            new SeasonalityChartParameters(10));

        Assert.Equal(
            new[] { (1, 2022), (1, 2023), (2, 2022), (2, 2023) },
            result.Traces.Select(t => (t.SeriesId, t.CalendarYear)).ToArray());
    }

    [Fact]
    public void Analyze_EmptySeriesList_ReturnsEmptyResult()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            Array.Empty<SeasonalitySeriesInput>(),
            new SeasonalityChartParameters(5));

        Assert.Empty(result.Traces);
        Assert.Empty(result.CalendarYears);
        Assert.Equal(0d, result.MinRadius);
        Assert.Equal(0d, result.MaxRadius);
    }

    [Fact]
    public void Analyze_NullSeriesList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SeasonalityChartEngine.Analyze(null!, new SeasonalityChartParameters(5)));
    }

    [Fact]
    public void Analyze_NonChronologicalPoints_Throws()
    {
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2023, 1, 5), 100m),
            (new DateTime(2023, 1, 3), 90m));

        Assert.Throws<ArgumentException>(() =>
            SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(5)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(101u)]
    public void Analyze_YearsToOverlayOutOfRange_Throws(uint years)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SeasonalityChartEngine.Analyze(
                new[] { PriceSeriesFromYearStart(1, 2023, 100m, 101m) },
                new SeasonalityChartParameters(years)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Analyze_NonPositiveManualScaleFactor_Throws(int scaleFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SeasonalityChartEngine.Analyze(
                new[] { PriceSeriesFromYearStart(1, 2023, 100m, 101m) },
                new SeasonalityChartParameters(5, ManualScaleFactor: scaleFactor)));
    }

    [Fact]
    public void Analyze_NegativeManualBaseRadius_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SeasonalityChartEngine.Analyze(
                new[] { PriceSeriesFromYearStart(1, 2023, 100m, 101m) },
                new SeasonalityChartParameters(5, ManualBaseRadius: -1m)));
    }

    [Fact]
    public void Analyze_SingleBarYear_ProducesOneSampleTrace()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { PriceSeriesFromYearStart(1, 2023, 100m) },
            new SeasonalityChartParameters(5));

        SeasonalityYearTrace trace = result.Traces.Single();
        SeasonalitySample only = Assert.Single(trace.Samples);
        Assert.Equal(0m, only.RateOfChange);
        Assert.Equal(SeasonalitySampleStatus.Valid, only.Status);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_DefaultsOffAndProducesNoMeanPaths()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { TwoYearRhoSeries() },
            new SeasonalityChartParameters(5));

        Assert.False(result.Parameters.IncludeMeanPath);
        Assert.Empty(result.MeanPaths);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_ProducesOneAngleOrderedMeanPathPerSeries()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { TwoYearRhoSeries() },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        SeasonalityMeanPath meanPath = Assert.Single(result.MeanPaths);
        Assert.Equal(1, meanPath.SeriesId);
        Assert.Equal(SeasonalityRadiusMode.PercentVsYearStart, meanPath.RadiusMode);
        Assert.True(meanPath.Samples.Count >= 2);
        for (int i = 1; i < meanPath.Samples.Count; i++)
        {
            Assert.True(meanPath.Samples[i].CanonicalDayIndex > meanPath.Samples[i - 1].CanonicalDayIndex);
            Assert.True(meanPath.Samples[i].PlotAngleRadians < meanPath.Samples[i - 1].PlotAngleRadians);
        }
    }

    [Fact]
    public void Analyze_IncludeMeanPath_BinValueIsTheMeanOfEachYearsRateOfChange()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { TwoYearRhoSeries() },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        SeasonalityMeanPathSample januarySecond = result.MeanPaths.Single().Samples.Single(s => s.CanonicalDayIndex == 1);

        // 2 Jan rho is +0.10 in 2022 and +0.20 in 2023 -> mean 0.15.
        Assert.Equal(0.15m, januarySecond.RateOfChange);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_MapsTheMeanWithTheSameScaleAndOffsetAsTheTraces()
    {
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { TwoYearRhoSeries() },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        // Global rho range [0, 0.2] -> K = 500, R0 = 10; mean bin at 2 Jan is 0.15 -> r = 10 + 500*0.15 = 85.
        Assert.Equal(500d, result.ScaleFactor, 9);
        Assert.Equal(10d, result.BaseRadius, 9);
        SeasonalityMeanPathSample januarySecond = result.MeanPaths.Single().Samples.Single(s => s.CanonicalDayIndex == 1);
        Assert.Equal(85d, januarySecond.Radius, 9);
        Assert.Equal(SeasonalitySampleStatus.Valid, januarySecond.Status);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_WithASingleOverlaidYear_ProducesNoMeanPath()
    {
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 1, 2), 110m));

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { series },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        Assert.Empty(result.MeanPaths);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_Dec31Bin_DoesNotFoldOntoTheJanuary1Apex()
    {
        // Two leap years so the 31 December canonical slot (365) is populated by >= MinYearsForMeanPath.
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2020, 1, 1), 100m),
            (new DateTime(2020, 12, 31), 150m),
            (new DateTime(2024, 1, 1), 100m),
            (new DateTime(2024, 12, 31), 120m));

        SeasonalityMeanPath meanPath = Assert.Single(SeasonalityChartEngine
            .Analyze(new[] { series }, new SeasonalityChartParameters(5, IncludeMeanPath: true))
            .MeanPaths);

        SeasonalityMeanPathSample dec31 = meanPath.Samples.Single(s => s.CanonicalDayIndex == 365);
        double apex = Math.PI / 2d;

        // Exactly pi/2 - 2*pi*(365/366): neither the 12 o'clock apex (the fold-onto-Jan-1 bug) nor
        // pi/2 - 2*pi (the old bin/365 basis bug).
        Assert.Equal(apex - (2d * Math.PI * (365d / 366d)), dec31.PlotAngleRadians, 12);
        Assert.True(Math.Abs(dec31.PlotAngleRadians - apex) > 1e-3);
        Assert.True(Math.Abs(dec31.PlotAngleRadians - (apex - (2d * Math.PI))) > 1e-3);
    }

    [Fact]
    public void Analyze_IncludeMeanPath_SampleCount_MatchesOverlaidYearsPerBin()
    {
        // 2022 and 2023 both carry a 2 January point -> that bin is averaged from 2 years.
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { TwoYearRhoSeries() },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        SeasonalityMeanPath meanPath = result.MeanPaths.Single();
        SeasonalityMeanPathSample januarySecond = meanPath.Samples.Single(s => s.CanonicalDayIndex == 1);
        Assert.Equal(2, januarySecond.SampleCount);

        // Every emitted bin is averaged from at least MinYearsForMeanPath years by construction.
        Assert.All(
            meanPath.Samples,
            s => Assert.True(s.SampleCount >= SeasonalityChartConstants.MinYearsForMeanPath));
    }

    [Fact]
    public void Analyze_IncludeMeanPath_ForwardFillsWeekendGaps_ProducingContinuousMeanPath()
    {
        // 2022 trades on Jan 1, Jan 2 (bin 1), and Jan 5 (bin 4). Bins 2 and 3 are weekend.
        // 2023 trades on Jan 1, Jan 3 (bin 2), and Jan 5 (bin 4). Bins 1 and 3 are weekend.
        SeasonalitySeriesInput series = SeriesAt(
            1,
            SeasonalityRadiusMode.PercentVsYearStart,
            (new DateTime(2022, 1, 1), 100m),
            (new DateTime(2022, 1, 2), 110m), // rho = +0.10
            (new DateTime(2022, 1, 5), 120m), // rho = +0.20
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 1, 3), 105m), // rho = +0.05
            (new DateTime(2023, 1, 5), 115m)); // rho = +0.15

        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { series },
            new SeasonalityChartParameters(5, IncludeMeanPath: true));

        SeasonalityMeanPath meanPath = Assert.Single(result.MeanPaths);

        // Every bin 0..4 must be populated with Count == 2 through forward-fill.
        Assert.Equal(5, meanPath.Samples.Count);
        for (int b = 0; b < 5; b++)
        {
            Assert.Equal(b, meanPath.Samples[b].CanonicalDayIndex);
            Assert.Equal(2, meanPath.Samples[b].SampleCount);
        }

        // Bin 0 (Jan 1): (0 + 0) / 2 = 0
        Assert.Equal(0m, meanPath.Samples[0].RateOfChange);
        // Bin 1 (Jan 2): (0.10 + 0 [2023 forward-filled]) / 2 = 0.05
        Assert.Equal(0.05m, meanPath.Samples[1].RateOfChange);
        // Bin 2 (Jan 3): (0.10 [2022 forward-filled] + 0.05) / 2 = 0.075
        Assert.Equal(0.075m, meanPath.Samples[2].RateOfChange);
        // Bin 3 (Jan 4): (0.10 [2022 forward-filled] + 0.05 [2023 forward-filled]) / 2 = 0.075
        Assert.Equal(0.075m, meanPath.Samples[3].RateOfChange);
        // Bin 4 (Jan 5): (0.20 + 0.15) / 2 = 0.175
        Assert.Equal(0.175m, meanPath.Samples[4].RateOfChange);
    }

    private static SeasonalitySeriesInput TwoYearRhoSeries() => SeriesAt(
        1,
        SeasonalityRadiusMode.PercentVsYearStart,
        (new DateTime(2022, 1, 1), 100m),
        (new DateTime(2022, 1, 2), 110m),
        (new DateTime(2023, 1, 1), 100m),
        (new DateTime(2023, 1, 2), 120m));

    private static SeasonalitySeriesInput PriceSeriesFromYearStart(int seriesId, int year, params decimal?[] values)
    {
        var points = new SeasonalityPoint[values.Length];
        DateTime start = new(year, 1, 1);
        for (int i = 0; i < values.Length; i++)
        {
            points[i] = new SeasonalityPoint(start.AddDays(i), values[i]);
        }

        return new SeasonalitySeriesInput(seriesId, $"S{seriesId}", SeasonalityRadiusMode.PercentVsYearStart, points);
    }

    private static SeasonalitySeriesInput SeriesAt(
        int seriesId,
        SeasonalityRadiusMode mode,
        params (DateTime Timestamp, decimal? Value)[] points)
    {
        var mapped = new SeasonalityPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            mapped[i] = new SeasonalityPoint(points[i].Timestamp, points[i].Value);
        }

        return new SeasonalitySeriesInput(seriesId, $"S{seriesId}", mode, mapped);
    }
}
