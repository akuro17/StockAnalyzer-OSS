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
    private static readonly SKColor ExpectedBullishClassic = new(0x00, 0xC8, 0x53);
    private static readonly SKColor ExpectedBearishClassic = new(0xD5, 0x00, 0x00);

    private static void AssertColorClose(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.True(Math.Abs((int)expected.Red - (int)actual.Red) <= tolerance,
            $"Red mismatch: expected {expected.Red}, actual {actual.Red}");
        Assert.True(Math.Abs((int)expected.Green - (int)actual.Green) <= tolerance,
            $"Green mismatch: expected {expected.Green}, actual {actual.Green}");
        Assert.True(Math.Abs((int)expected.Blue - (int)actual.Blue) <= tolerance,
            $"Blue mismatch: expected {expected.Blue}, actual {actual.Blue}");
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
    public void GetPaletteColor_DefaultPalette_ReturnsBaseAtLowerBound_AndPeakAtThresholdLimits()
    {
        // Bullish base (+0.5% return is Level 1 with default 0.05 threshold, 10 steps)
        var bullishBase = UniformHeatmapControl.GetPaletteColor(0.005, HeatmapPalette.Default);
        AssertColorClose(ExpectedBullishClassic, bullishBase);

        // Bearish base (-0.5% return is Level 1)
        var bearishBase = UniformHeatmapControl.GetPaletteColor(-0.005, HeatmapPalette.Default);
        AssertColorClose(ExpectedBearishClassic, bearishBase);

        // Bullish peak (+5% or higher reaches auto-derived luminous peak)
        var expectedBullishPeak = UniformHeatmapControl.DerivePeakColor(ExpectedBullishClassic);
        var bullishPeak = UniformHeatmapControl.GetPaletteColor(0.05, HeatmapPalette.Default);
        AssertColorClose(expectedBullishPeak, bullishPeak);

        // Bearish peak (-5% or lower reaches auto-derived luminous peak)
        var expectedBearishPeak = UniformHeatmapControl.DerivePeakColor(ExpectedBearishClassic);
        var bearishPeak = UniformHeatmapControl.GetPaletteColor(-0.05, HeatmapPalette.Default);
        AssertColorClose(expectedBearishPeak, bearishPeak);
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

        var upBase = UniformHeatmapControl.GetPaletteColor(0.005, customPalette);
        AssertColorClose(new SKColor(customUp.R, customUp.G, customUp.B), upBase);

        var downBase = UniformHeatmapControl.GetPaletteColor(-0.005, customPalette);
        AssertColorClose(new SKColor(customDown.R, customDown.G, customDown.B), downBase);

        var expectedUpPeak = UniformHeatmapControl.DerivePeakColor(new SKColor(customUp.R, customUp.G, customUp.B));
        var upPeak = UniformHeatmapControl.GetPaletteColor(0.05, customPalette);
        AssertColorClose(expectedUpPeak, upPeak);

        var expectedDownPeak = UniformHeatmapControl.DerivePeakColor(new SKColor(customDown.R, customDown.G, customDown.B));
        var downPeak = UniformHeatmapControl.GetPaletteColor(-0.05, customPalette);
        AssertColorClose(expectedDownPeak, downPeak);
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
