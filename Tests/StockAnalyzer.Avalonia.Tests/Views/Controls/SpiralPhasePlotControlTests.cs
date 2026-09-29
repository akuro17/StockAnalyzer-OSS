using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SpiralPhasePlotControlTests
{
    [Fact]
    public void Layout_UsesNicePriceGridAndOptionalModelRange()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        var samples = new[]
        {
            new SpiralAnalysisSample(0, default, SpiralAnalysisSampleStatus.Valid, 100m, 0d, 100d, 500d, 0d, null, null),
            new SpiralAnalysisSample(1, default, SpiralAnalysisSampleStatus.Valid, 120m, 1d, 120d, 600d, 0d, null, null)
        };
        var result = new SpiralAnalysisResult(parameters, samples, false, default);

        SpiralPhasePlotLayout actualOnly = SpiralPhasePlotLayoutBuilder.Build(result, false);
        SpiralPhasePlotLayout allSeries = SpiralPhasePlotLayoutBuilder.Build(result, true);

        Assert.Equal(120d, actualOnly.MaximumRadius);
        Assert.Equal(600d, allSeries.MaximumRadius);
        Assert.InRange(actualOnly.GridRadii.Length, 1, SpiralPhasePlotLayoutBuilder.MaximumGridCircleCount);
        Assert.Equal(0, actualOnly.StartSampleIndex);
        Assert.Equal(1, actualOnly.EndSampleIndex);
    }

    [Fact]
    public void Layout_UsesManualPriceStepAndCachesPriceLabels()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        var samples = new[]
        {
            new SpiralAnalysisSample(0, default, SpiralAnalysisSampleStatus.Valid, 100m, 0d, 100d, 100d, 0d, null, null),
            new SpiralAnalysisSample(1, default, SpiralAnalysisSampleStatus.Valid, 250m, 1d, 250d, 250d, 0d, null, null)
        };
        var result = new SpiralAnalysisResult(parameters, samples, false, default);

        SpiralPhasePlotLayout layout = SpiralPhasePlotLayoutBuilder.Build(result, false, 50m);

        Assert.Equal(new[] { 50d, 100d, 150d, 200d, 250d }, layout.GridRadii);
        Assert.Equal(new[] { "50", "100", "150", "200", "250" }, layout.GridLabels);
        Assert.Equal("0", layout.CenterLabel);
    }

    [Fact]
    public void Layout_AppendsLocalizedPriceUnitOutsideRenderPath()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        var samples = new[]
        {
            new SpiralAnalysisSample(0, default, SpiralAnalysisSampleStatus.Valid, 100m, 0d, 100d, 100d, 0d, null, null)
        };
        var result = new SpiralAnalysisResult(parameters, samples, false, default);

        SpiralPhasePlotLayout layout = SpiralPhasePlotLayoutBuilder.Build(result, false, 50m, "price");

        Assert.Equal(new[] { "50 price", "100 price" }, layout.GridLabels);
        Assert.Equal("0 price", layout.CenterLabel);
    }

    [Theory]
    [InlineData(20u, "0° / 0 bars", "45° / 2.5 bars", "315° / 17.5 bars")]
    [InlineData(21u, "0° / 0 bars", "45° / 2.625 bars", "315° / 18.375 bars")]
    [InlineData(8u, "0° / 0 bars", "45° / 1 bars", "315° / 7 bars")]
    public void AngleLabels_ShowExactPhaseWithinOneTurn(uint barsPerTurn, string first, string second, string last)
    {
        string[] labels = SpiralPhasePlotLayoutBuilder.BuildAngleLabels(barsPerTurn, "bars");

        Assert.Equal(SpiralPhasePlotLayoutBuilder.RadialLineCount, labels.Length);
        Assert.Equal(first, labels[0]);
        Assert.Equal(second, labels[1]);
        Assert.Equal(last, labels[^1]);
    }

    [Fact]
    public void AngleLabels_ClockwiseFollowDisplayedDirection()
    {
        string[] labels = SpiralPhasePlotLayoutBuilder.BuildAngleLabels(8, "bars", isClockwise: true);

        Assert.Contains("6 bars", labels[2]);
        Assert.Contains("2 bars", labels[6]);
    }

    [Theory]
    [InlineData(0.7d, 1d)]
    [InlineData(3d, 5d)]
    [InlineData(12d, 20d)]
    public void CalculateNiceStep_UsesOneTwoFive(double input, double expected)
    {
        Assert.Equal(expected, SpiralPhasePlotLayoutBuilder.CalculateNiceStep(input), 10);
    }

    [Fact]
    public void TryProjectPoint_RejectsCoordinatesOutsideSingleRange()
    {
        bool projected = SpiralPhasePlotControl.TryProjectPoint(
            radius: 1e100,
            angleRadians: 0d,
            centerX: 200f,
            centerY: 200f,
            scale: 1d,
            out SKPoint point);

        Assert.False(projected);
        Assert.Equal(default, point);
    }

    [Fact]
    public void TryProjectPoint_ReturnsFinitePointForNormalRadius()
    {
        bool projected = SpiralPhasePlotControl.TryProjectPoint(
            radius: 100d,
            angleRadians: 0d,
            centerX: 200f,
            centerY: 200f,
            scale: 1d,
            out SKPoint point);

        Assert.True(projected);
        Assert.Equal(300f, point.X);
        Assert.Equal(200f, point.Y);
    }

    [Fact]
    public void TryProjectPoint_SubtractsDisplayBaseWithoutChangingPriceValue()
    {
        bool projected = SpiralPhasePlotControl.TryProjectPoint(
            radius: 100d,
            angleRadians: 0d,
            centerX: 200f,
            centerY: 200f,
            scale: 2d,
            displayBaseRadius: 90d,
            out SKPoint point);

        Assert.True(projected);
        Assert.Equal(220f, point.X);
        Assert.Equal(200f, point.Y);
    }

    [Fact]
    public void TryProjectPoint_RejectsRadiusBelowDisplayBase()
    {
        Assert.False(SpiralPhasePlotControl.TryProjectPoint(89d, 0d, 200f, 200f, 2d, 90d, out _));
    }

    [Fact]
    public void InterpolateRadius_UsesGeometricMidpointForLogarithmicModel()
    {
        double radius = SpiralPhasePlotControl.InterpolateRadius(100d, 400d, 0.5d, SpiralPriceModelKind.Logarithmic, isModelSeries: true);

        Assert.Equal(200d, radius, precision: 10);
    }

    [Fact]
    public void InterpolateRadius_UsesLinearMidpointForArchimedeanModel()
    {
        double radius = SpiralPhasePlotControl.InterpolateRadius(100d, 400d, 0.5d, SpiralPriceModelKind.Archimedean, isModelSeries: true);

        Assert.Equal(250d, radius, precision: 10);
    }

    [Theory]
    [InlineData(-1d, (int)SpiralResidualSign.Negative)]
    [InlineData(0d, (int)SpiralResidualSign.Zero)]
    [InlineData(1d, (int)SpiralResidualSign.Positive)]
    [InlineData(double.NaN, (int)SpiralResidualSign.Unavailable)]
    [InlineData(double.PositiveInfinity, (int)SpiralResidualSign.Unavailable)]
    public void ClassifyResidual_UsesExactSign(double residual, int expected)
    {
        Assert.Equal((SpiralResidualSign)expected, SpiralPhasePlotControl.ClassifyResidual(residual));
    }

    [Fact]
    public void ClassifyResidual_TreatsMissingValueAsUnavailable()
    {
        Assert.Equal(SpiralResidualSign.Unavailable, SpiralPhasePlotControl.ClassifyResidual(null));
    }

    [Theory]
    [InlineData(-10d, 10d, 0.5d)]
    [InlineData(-30d, 10d, 0.75d)]
    [InlineData(double.MaxValue, -double.MaxValue, 0.5d)]
    public void ResidualCrossing_UsesOverflowSafeLinearFraction(double start, double end, double expected)
    {
        bool found = SpiralPhasePlotControl.TryGetResidualCrossingFraction(start, end, out double fraction);

        Assert.True(found);
        Assert.Equal(expected, fraction, precision: 12);
    }

    [Theory]
    [InlineData(1d, 2d)]
    [InlineData(-2d, -1d)]
    [InlineData(0d, 1d)]
    [InlineData(double.NaN, 1d)]
    public void ResidualCrossing_RejectsNonCrossingValues(double start, double end)
    {
        Assert.False(SpiralPhasePlotControl.TryGetResidualCrossingFraction(start, end, out _));
    }


    [Fact]
    public void Layout_VisibleRangeOriginUsesDrawableModelMinimumAndAbsoluteTicks()
    {
        var parameters = new SpiralAnalysisParameters(20, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        var samples = new[]
        {
            new SpiralAnalysisSample(0, default, SpiralAnalysisSampleStatus.Valid, 100m, 0d, 100d, 80d, 20d, null, null),
            new SpiralAnalysisSample(1, default, SpiralAnalysisSampleStatus.Valid, 120m, 1d, 120d, 150d, -30d, null, null)
        };
        var result = new SpiralAnalysisResult(parameters, samples, false, default);

        SpiralPhasePlotLayout layout = SpiralPhasePlotLayoutBuilder.Build(result, false, 10m, "price", "bars", true, "Display base");

        Assert.Equal(120d, layout.MaximumRadius);
        Assert.Equal(72d, layout.DisplayBaseRadius, precision: 10);
        Assert.Equal(48d, layout.RadialSpan, precision: 10);
        Assert.Equal(new[] { 80d, 90d, 100d, 110d, 120d }, layout.GridRadii);
        Assert.Equal("Display base: 72 price", layout.CenterLabel);
    }

    [Fact]
    public void Layout_DefaultOriginPreservesZeroBaseAndMaximumSpan()
    {
        var parameters = new SpiralAnalysisParameters(20, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        var samples = new[]
        {
            new SpiralAnalysisSample(0, default, SpiralAnalysisSampleStatus.Valid, 100m, 0d, 100d, 80d, 20d, null, null),
            new SpiralAnalysisSample(1, default, SpiralAnalysisSampleStatus.Valid, 120m, 1d, 120d, 150d, -30d, null, null)
        };
        var result = new SpiralAnalysisResult(parameters, samples, false, default);

        SpiralPhasePlotLayout layout = SpiralPhasePlotLayoutBuilder.Build(result, false, 20m, "price", "bars");

        Assert.Equal(0d, layout.DisplayBaseRadius);
        Assert.Equal(layout.MaximumRadius, layout.RadialSpan);
        Assert.Equal("0 price", layout.CenterLabel);
    }
}
