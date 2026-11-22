using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public sealed class UniformHeatmapColormapTests
{
    private static readonly SKColor ExpectedNoDataColor = new(0x1E, 0x20, 0x24);
    private static readonly SKColor ExpectedNeutralColor = new(0x2B, 0x2F, 0x3A);
    private const double MinimumDichromatSeparation = 0.08;
    private static readonly SKColor ExpectedBullishPeak = new(0x00, 0x70, 0x4A);   // Default Up (peak at +threshold)
    private static readonly SKColor ExpectedBearishPeak = new(0xB2, 0x35, 0x10);   // Default Down (peak at -threshold)

    // Vienot, Brettel & Mollon (1999) dichromat simulation matrices in linear sRGB (row-major).
    private static readonly double[] ProtanopiaMatrix =
    {
        0.152286, 1.052583, -0.204868,
        0.114503, 0.786281, 0.099216,
        -0.003882, -0.048116, 1.051998
    };

    private static readonly double[] DeuteranopiaMatrix =
    {
        0.367322, 0.860646, -0.227968,
        0.280085, 0.672501, 0.047413,
        -0.011820, 0.042940, 0.968881
    };

    private static void AssertColorClose(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.True(Math.Abs((int)expected.Red - (int)actual.Red) <= tolerance,
            $"Red mismatch: expected {expected.Red}, actual {actual.Red}");
        Assert.True(Math.Abs((int)expected.Green - (int)actual.Green) <= tolerance,
            $"Green mismatch: expected {expected.Green}, actual {actual.Green}");
        Assert.True(Math.Abs((int)expected.Blue - (int)actual.Blue) <= tolerance,
            $"Blue mismatch: expected {expected.Blue}, actual {actual.Blue}");
    }

    private static IndicatorColor ToIndicator(SKColor c) => new(255, c.Red, c.Green, c.Blue);

    private static OklabColor Lab(SKColor c) => OklabColor.FromRgb(c.Red, c.Green, c.Blue);

    private static SKColor ToSk(IndicatorColor c) => new(c.R, c.G, c.B);

    private static OklabColor Simulate(SKColor c, double[] m)
    {
        static double ToLinear(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        static byte ToByte(double linear)
        {
            double x = Math.Clamp(linear, 0.0, 1.0);
            double e = x <= 0.0031308 ? x * 12.92 : (1.055 * Math.Pow(x, 1.0 / 2.4)) - 0.055;
            return (byte)Math.Clamp((int)Math.Round(e * 255.0), 0, 255);
        }

        double r = ToLinear(c.Red);
        double g = ToLinear(c.Green);
        double b = ToLinear(c.Blue);
        return OklabColor.FromRgb(
            ToByte((m[0] * r) + (m[1] * g) + (m[2] * b)),
            ToByte((m[3] * r) + (m[4] * g) + (m[5] * b)),
            ToByte((m[6] * r) + (m[7] * g) + (m[8] * b)));
    }

    [Fact]
    public void GetPaletteColor_NanReturn_ReturnsNoDataColor()
    {
        var color = UniformHeatmapControl.GetPaletteColor(double.NaN, HeatmapPalette.Default);
        Assert.Equal(ExpectedNoDataColor, color);
    }

    [Fact]
    public void GetPaletteColor_ZeroReturn_ReturnsNeutralColor()
    {
        var color = UniformHeatmapControl.GetPaletteColor(0.0, HeatmapPalette.Default);
        AssertColorClose(ExpectedNeutralColor, color);
    }

    [Fact]
    public void GetPaletteColor_DefaultPalette_StartsNearNeutral_AndReachesExactPeakAtThreshold()
    {
        var palette = HeatmapPalette.Default;
        var neutralLab = Lab(ExpectedNeutralColor);

        // Level 1 (+0.5% with 5% threshold, 10 steps) sits at t = OnsetAnchorFraction on the Neutral -> Peak ramp.
        var expectedUpLevel1 = OklabColor.Lerp(neutralLab, Lab(ExpectedBullishPeak), HeatmapColorRamp.OnsetAnchorFraction).ToIndicatorColorInGamut();
        var upLevel1 = UniformHeatmapControl.GetPaletteColor(0.005, palette);
        AssertColorClose(ToSk(expectedUpLevel1), upLevel1);

        var expectedDownLevel1 = OklabColor.Lerp(neutralLab, Lab(ExpectedBearishPeak), HeatmapColorRamp.OnsetAnchorFraction).ToIndicatorColorInGamut();
        var downLevel1 = UniformHeatmapControl.GetPaletteColor(-0.005, palette);
        AssertColorClose(ToSk(expectedDownLevel1), downLevel1);

        // The first coloured level is a bounded step away from Neutral (no lightness jump).
        Assert.InRange(OklabColor.DeltaE(neutralLab, Lab(upLevel1)), 0.03, 0.08);
        Assert.InRange(OklabColor.DeltaE(neutralLab, Lab(downLevel1)), 0.03, 0.08);

        // Peak: exactly the configured Up / Down colour at +/- threshold and beyond.
        AssertColorClose(ExpectedBullishPeak, UniformHeatmapControl.GetPaletteColor(0.05, palette));
        AssertColorClose(ExpectedBearishPeak, UniformHeatmapControl.GetPaletteColor(-0.05, palette));
        AssertColorClose(ExpectedBullishPeak, UniformHeatmapControl.GetPaletteColor(0.20, palette));
    }

    [Fact]
    public void GetPaletteColor_SingleStep_RendersFullPeakColor()
    {
        var palette = HeatmapPalette.Default with { StepCount = 1 };

        AssertColorClose(ExpectedBullishPeak, UniformHeatmapControl.GetPaletteColor(0.0001, palette));
        AssertColorClose(ExpectedBullishPeak, UniformHeatmapControl.GetPaletteColor(0.04, palette));
        AssertColorClose(ExpectedBearishPeak, UniformHeatmapControl.GetPaletteColor(-0.0001, palette));
        AssertColorClose(ExpectedBearishPeak, UniformHeatmapControl.GetPaletteColor(-0.04, palette));
        AssertColorClose(ExpectedNeutralColor, UniformHeatmapControl.GetPaletteColor(0.0, palette));
    }

    [Fact]
    public void GetPaletteColor_DefaultPeaks_RemainSeparableUnderProtanopiaAndDeuteranopia()
    {
        var up = UniformHeatmapControl.GetPaletteColor(0.05, HeatmapPalette.Default);
        var down = UniformHeatmapControl.GetPaletteColor(-0.05, HeatmapPalette.Default);

        // Floor pins the separation measured for the owner-selected green Up / red Down defaults.
        Assert.True(OklabColor.DeltaE(Simulate(up, ProtanopiaMatrix), Simulate(down, ProtanopiaMatrix)) >= MinimumDichromatSeparation);
        Assert.True(OklabColor.DeltaE(Simulate(up, DeuteranopiaMatrix), Simulate(down, DeuteranopiaMatrix)) >= MinimumDichromatSeparation);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0xFF)]
    [InlineData(0x00, 0xFF, 0xFF)]
    [InlineData(0xFF, 0x00, 0x00)]
    [InlineData(0x00, 0xFF, 0x00)]
    public void GetPaletteColor_SaturatedCustomColors_PreserveLightnessInsteadOfClipping(byte r, byte g, byte b)
    {
        var customUp = new IndicatorColor(255, r, g, b);
        var neutral = new IndicatorColor(255, 0x2B, 0x2F, 0x3A);
        var palette = new HeatmapPalette(customUp, neutral, customUp, 0.05, 0);

        var neutralLab = OklabColor.FromIndicatorColor(neutral);
        var peakLab = OklabColor.FromIndicatorColor(customUp);

        for (double ret = 0.001; ret <= 0.05001; ret += 0.001)
        {
            var actual = UniformHeatmapControl.GetPaletteColor(ret, palette);
            double factor = Math.Min(ret / 0.05, 1.0);
            double t = HeatmapColorRamp.OnsetAnchorFraction + (factor * (1.0 - HeatmapColorRamp.OnsetAnchorFraction));
            double expectedL = OklabColor.Lerp(neutralLab, peakLab, t).L;

            Assert.True(Math.Abs(Lab(actual).L - expectedL) <= 0.02, $"Lightness drifted at ret={ret}: expected {expectedL}, actual {Lab(actual).L}");
        }
    }

    [Fact]
    public void GetPaletteRenderColors_DefaultPalette_EverySampleMeetsWcagAaWithLightInkAndShadow()
    {
        var palette = HeatmapPalette.Default;
        for (double ret = -0.07; ret <= 0.07001; ret += 0.0005)
        {
            var (fill, text, drawShadow) = UniformHeatmapControl.GetPaletteRenderColors(ret, palette);
            Assert.True(WcagColorContrast.ContrastRatio(ToIndicator(fill), ToIndicator(text)) >= WcagColorContrast.MinimumAaNormalTextRatio, $"ret={ret}");
            Assert.True(drawShadow, $"Light ink on a darker tile keeps its shadow (ret={ret})");
        }

        var (_, noDataText, noDataShadow) = UniformHeatmapControl.GetPaletteRenderColors(double.NaN, palette);
        Assert.Equal(new SKColor(palette.TextColor.R, palette.TextColor.G, palette.TextColor.B, palette.TextColor.A), noDataText);
        Assert.True(noDataShadow);
    }

    [Fact]
    public void GetPaletteRenderColors_BrightCustomTile_UsesDarkInkWithoutShadow()
    {
        var bright = new IndicatorColor(255, 0xFF, 0xFF, 0x00);
        var neutral = new IndicatorColor(255, 0x2B, 0x2F, 0x3A);
        var palette = new HeatmapPalette(bright, neutral, bright, 0.05, 10);

        var (fill, text, drawShadow) = UniformHeatmapControl.GetPaletteRenderColors(0.05, palette);

        AssertColorClose(new SKColor(0xFF, 0xFF, 0x00), fill);
        Assert.False(drawShadow);
        Assert.True(WcagColorContrast.ContrastRatio(ToIndicator(fill), ToIndicator(text)) >= WcagColorContrast.MinimumAaNormalTextRatio);
        Assert.True(WcagColorContrast.RelativeLuminance(ToIndicator(text)) < WcagColorContrast.RelativeLuminance(ToIndicator(fill)));
    }

    [Fact]
    public void GetPaletteRenderColors_DarkPreferredTextOnNoDataTile_FallsBackToLightInk()
    {
        var palette = HeatmapPalette.Default with { TextColor = new IndicatorColor(255, 0x10, 0x10, 0x10) };

        var (fill, text, drawShadow) = UniformHeatmapControl.GetPaletteRenderColors(double.NaN, palette);

        Assert.Equal(ExpectedNoDataColor, fill);
        Assert.True(drawShadow);
        Assert.True(WcagColorContrast.ContrastRatio(ToIndicator(fill), ToIndicator(text)) >= WcagColorContrast.MinimumAaNormalTextRatio);
    }

    [Fact]
    public void GetPaletteColor_ExtremeAndDegeneratePalette_DoesNotThrow()
    {
        var zeroT = HeatmapPalette.Default with { ThresholdRatio = 0.0 };
        var c1 = UniformHeatmapControl.GetPaletteColor(0.02, zeroT);
        Assert.NotEqual(default, c1);

        var negT = HeatmapPalette.Default with { ThresholdRatio = -0.05 };
        var c2 = UniformHeatmapControl.GetPaletteColor(-0.02, negT);
        Assert.NotEqual(default, c2);

        var nanT = HeatmapPalette.Default with { ThresholdRatio = double.NaN };
        var c3 = UniformHeatmapControl.GetPaletteColor(0.02, nanT);
        Assert.NotEqual(default, c3);

        var c4 = UniformHeatmapControl.GetPaletteColor(double.PositiveInfinity, HeatmapPalette.Default);
        Assert.NotEqual(default, c4);
    }

    [Fact]
    public void GetPaletteColor_ContinuousProgression_HasNoSuddenDiscontinuities()
    {
        var continuousPalette = HeatmapPalette.Default with { StepCount = 0 };

        // Positive ramp continuity: smooth progression within active ramp
        SKColor prevUpColor = UniformHeatmapControl.GetPaletteColor(0.001, continuousPalette);
        for (double r = 0.005; r <= 0.05; r += 0.005)
        {
            var curColor = UniformHeatmapControl.GetPaletteColor(r, continuousPalette);
            int deltaR = Math.Abs(curColor.Red - prevUpColor.Red);
            int deltaG = Math.Abs(curColor.Green - prevUpColor.Green);
            int deltaB = Math.Abs(curColor.Blue - prevUpColor.Blue);

            Assert.True(deltaR < 40, $"Red jump too large: {deltaR} at r = {r}");
            Assert.True(deltaG < 40, $"Green jump too large: {deltaG} at r = {r}");
            Assert.True(deltaB < 40, $"Blue jump too large: {deltaB} at r = {r}");
            prevUpColor = curColor;
        }

        // Negative ramp continuity: smooth progression within active ramp
        SKColor prevDownColor = UniformHeatmapControl.GetPaletteColor(-0.05, continuousPalette);
        for (double r = -0.045; r <= -0.001; r += 0.005)
        {
            var curColor = UniformHeatmapControl.GetPaletteColor(r, continuousPalette);
            int deltaR = Math.Abs(curColor.Red - prevDownColor.Red);
            int deltaG = Math.Abs(curColor.Green - prevDownColor.Green);
            int deltaB = Math.Abs(curColor.Blue - prevDownColor.Blue);

            Assert.True(deltaR < 40, $"Red jump too large: {deltaR} at r = {r}");
            Assert.True(deltaG < 40, $"Green jump too large: {deltaG} at r = {r}");
            Assert.True(deltaB < 40, $"Blue jump too large: {deltaB} at r = {r}");
            prevDownColor = curColor;
        }
    }

    [Fact]
    public void GetPaletteColor_SteppedOutput_HasExactly2NPlus1DistinctColors()
    {
        int n = 10;
        var palette = HeatmapPalette.Default with { StepCount = n };
        var distinctColors = new HashSet<SKColor>();

        for (double r = -0.10; r <= 0.10; r += 0.0001)
        {
            distinctColors.Add(UniformHeatmapControl.GetPaletteColor(r, palette));
        }

        // Exactly 2N + 1 distinct colors: N up levels, N down levels, and 1 neutral
        Assert.Equal((2 * n) + 1, distinctColors.Count);
    }

    [Fact]
    public void GetPaletteColor_RampsAreMonotoneInDistanceFromNeutral()
    {
        var palette = HeatmapPalette.Default;
        var neutral = UniformHeatmapControl.GetPaletteColor(0.0, palette);

        static double Dist(SKColor c1, SKColor c2)
        {
            double dr = c1.Red - c2.Red;
            double dg = c1.Green - c2.Green;
            double db = c1.Blue - c2.Blue;
            return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
        }

        double prevUpDist = 0.0;
        double prevDownDist = 0.0;

        for (double r = 0.001; r <= 0.06; r += 0.005)
        {
            var upColor = UniformHeatmapControl.GetPaletteColor(r, palette);
            var downColor = UniformHeatmapControl.GetPaletteColor(-r, palette);

            double upDist = Dist(upColor, neutral);
            double downDist = Dist(downColor, neutral);

            Assert.True(upDist >= prevUpDist - 1.0, $"Up ramp not monotone: r={r}, current={upDist}, prev={prevUpDist}");
            Assert.True(downDist >= prevDownDist - 1.0, $"Down ramp not monotone: r={r}, current={downDist}, prev={prevDownDist}");

            prevUpDist = upDist;
            prevDownDist = downDist;
        }
    }

    [Fact]
    public void GetPaletteColor_CustomPaletteColorsAreHonoured()
    {
        var customUp = new IndicatorColor(255, 30, 144, 255);    // Dodger Blue
        var customNeutral = new IndicatorColor(255, 50, 50, 50); // Dark Gray
        var customDown = new IndicatorColor(255, 255, 140, 0);   // Dark Orange

        var customPalette = new HeatmapPalette(customUp, customNeutral, customDown, 0.05, 10);

        var neutralResult = UniformHeatmapControl.GetPaletteColor(0.0, customPalette);
        AssertColorClose(new SKColor(customNeutral.R, customNeutral.G, customNeutral.B), neutralResult);

        var neutralLab = OklabColor.FromIndicatorColor(customNeutral);
        var upLevel1 = OklabColor.Lerp(neutralLab, OklabColor.FromIndicatorColor(customUp), HeatmapColorRamp.OnsetAnchorFraction).ToIndicatorColorInGamut();
        AssertColorClose(ToSk(upLevel1), UniformHeatmapControl.GetPaletteColor(0.005, customPalette));

        var downLevel1 = OklabColor.Lerp(neutralLab, OklabColor.FromIndicatorColor(customDown), HeatmapColorRamp.OnsetAnchorFraction).ToIndicatorColorInGamut();
        AssertColorClose(ToSk(downLevel1), UniformHeatmapControl.GetPaletteColor(-0.005, customPalette));

        // Peak is exactly the configured colour at +/- threshold.
        AssertColorClose(ToSk(customUp), UniformHeatmapControl.GetPaletteColor(0.05, customPalette));
        AssertColorClose(ToSk(customDown), UniformHeatmapControl.GetPaletteColor(-0.05, customPalette));
    }

    [Fact]
    public void HeatmapPalette_Defaults_IncludeFontAndTextSettings()
    {
        var palette = HeatmapPalette.Default;
        Assert.Equal(16.0, palette.FontSize);
        Assert.Equal(StockAnalyzer.Core.Models.Analysis.HeatmapTextAlignment.Center, palette.TextAlignment);
        Assert.Equal(new IndicatorColor(255, 0xE0, 0xE0, 0xE0), palette.TextColor);
    }
}
