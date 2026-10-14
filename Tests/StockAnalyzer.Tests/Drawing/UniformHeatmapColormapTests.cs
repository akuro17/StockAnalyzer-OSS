using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public sealed class UniformHeatmapColormapTests
{
    private static readonly SKColor ExpectedNoDataColor = new(0x1E, 0x20, 0x24);
    private static readonly SKColor ExpectedNeutralColor = new(0x2B, 0x2F, 0x3A);
    private static readonly SKColor ExpectedBullishClassic = new(0x00, 0xC8, 0x53);
    private static readonly SKColor ExpectedBearishClassic = new(0xD5, 0x00, 0x00);
    private static readonly SKColor ExpectedBullishColorblind = new(0x1E, 0x88, 0xE5);
    private static readonly SKColor ExpectedBearishColorblind = new(0xFB, 0x8C, 0x00);

    [Fact]
    public void GetDivergingColor_NanReturn_ReturnsNoDataColor()
    {
        var color = UniformHeatmapControl.GetDivergingColor(double.NaN, 0.05, HeatmapColorScheme.Classic);
        Assert.Equal(ExpectedNoDataColor, color);

        var colorblindColor = UniformHeatmapControl.GetDivergingColor(double.NaN, 0.05, HeatmapColorScheme.ColorblindSafe);
        Assert.Equal(ExpectedNoDataColor, colorblindColor);
    }

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
    public void GetDivergingColor_ZeroReturn_ReturnsNeutralColor()
    {
        var classicZero = UniformHeatmapControl.GetDivergingColor(0.0, 0.05, HeatmapColorScheme.Classic);
        AssertColorClose(ExpectedNeutralColor, classicZero);

        var cbZero = UniformHeatmapControl.GetDivergingColor(0.0, 0.05, HeatmapColorScheme.ColorblindSafe);
        AssertColorClose(ExpectedNeutralColor, cbZero);
    }

    [Fact]
    public void GetDivergingColor_ClassicScheme_ReturnsPeaksAtThresholdLimits()
    {
        // Bullish limit (+5% or higher with 0.05 threshold)
        var bullishMax = UniformHeatmapControl.GetDivergingColor(0.05, 0.05, HeatmapColorScheme.Classic);
        AssertColorClose(ExpectedBullishClassic, bullishMax);

        // Bearish limit (-5% or lower with 0.05 threshold)
        var bearishMax = UniformHeatmapControl.GetDivergingColor(-0.05, 0.05, HeatmapColorScheme.Classic);
        AssertColorClose(ExpectedBearishClassic, bearishMax);
    }

    [Fact]
    public void GetDivergingColor_ColorblindScheme_ReturnsPeaksAtThresholdLimits()
    {
        // Bullish limit: Sky blue
        var bullishCb = UniformHeatmapControl.GetDivergingColor(0.05, 0.05, HeatmapColorScheme.ColorblindSafe);
        AssertColorClose(ExpectedBullishColorblind, bullishCb);

        // Bearish limit: Amber/Orange
        var bearishCb = UniformHeatmapControl.GetDivergingColor(-0.05, 0.05, HeatmapColorScheme.ColorblindSafe);
        AssertColorClose(ExpectedBearishColorblind, bearishCb);
    }

    [Fact]
    public void GetDivergingColor_ExtremeAndDegenerateScale_DoesNotThrow()
    {
        // Zero threshold clamped safely
        var c1 = UniformHeatmapControl.GetDivergingColor(0.02, 0.0);
        Assert.NotEqual(default, c1);

        // Negative threshold clamped safely
        var c2 = UniformHeatmapControl.GetDivergingColor(-0.02, -0.05);
        Assert.NotEqual(default, c2);

        // NaN threshold clamped safely
        var c3 = UniformHeatmapControl.GetDivergingColor(0.02, double.NaN);
        Assert.NotEqual(default, c3);

        // Infinity return clamped safely
        var c4 = UniformHeatmapControl.GetDivergingColor(double.PositiveInfinity, 0.05);
        Assert.NotEqual(default, c4);
    }

    [Fact]
    public void GetDivergingColor_OklabContinuousProgression_HasNoSuddenDiscontinuities()
    {
        // Ensure steps across -0.05 to +0.05 transition without abrupt color inversion
        SKColor prevColor = UniformHeatmapControl.GetDivergingColor(-0.05, 0.05, HeatmapColorScheme.Classic);

        for (double r = -0.04; r <= 0.05; r += 0.01)
        {
            var curColor = UniformHeatmapControl.GetDivergingColor(r, 0.05, HeatmapColorScheme.Classic);
            int deltaR = Math.Abs(curColor.Red - prevColor.Red);
            int deltaG = Math.Abs(curColor.Green - prevColor.Green);
            int deltaB = Math.Abs(curColor.Blue - prevColor.Blue);

            // Perceptually uniform LUT ensures adjacent 1% steps never jump > 80 in 8-bit channel
            Assert.True(deltaR < 80, $"Red jump too large: {deltaR} at r = {r}");
            Assert.True(deltaG < 80, $"Green jump too large: {deltaG} at r = {r}");
            Assert.True(deltaB < 80, $"Blue jump too large: {deltaB} at r = {r}");

            prevColor = curColor;
        }
    }
}
