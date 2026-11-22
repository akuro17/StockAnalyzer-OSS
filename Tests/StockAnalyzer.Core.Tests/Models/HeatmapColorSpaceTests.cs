using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class HeatmapColorSpaceTests
{
    private static readonly IndicatorColor DefaultUp = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapUpColor).ToIndicatorColor();
    private static readonly IndicatorColor DefaultNeutral = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor).ToIndicatorColor();
    private static readonly IndicatorColor DefaultDown = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapDownColor).ToIndicatorColor();
    private static readonly IndicatorColor DefaultText = HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultScatterHeatmapTextColor).ToIndicatorColor();

    private const double MinimumDichromatSeparation = 0.08;

    // Vienot, Brettel & Mollon (1999) dichromat simulation matrices in linear sRGB (row-major).
    private static readonly double[] Protanopia =
    {
        0.152286, 1.052583, -0.204868,
        0.114503, 0.786281, 0.099216,
        -0.003882, -0.048116, 1.051998
    };

    private static readonly double[] Deuteranopia =
    {
        0.367322, 0.860646, -0.227968,
        0.280085, 0.672501, 0.047413,
        -0.011820, 0.042940, 0.968881
    };

    private static double ToLinear(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte ToByte(double linear)
    {
        double v = Math.Clamp(linear, 0.0, 1.0);
        double e = v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(e * 255.0), 0, 255);
    }

    private static OklabColor Simulate(IndicatorColor c, double[] m)
    {
        double r = ToLinear(c.R);
        double g = ToLinear(c.G);
        double b = ToLinear(c.B);
        return OklabColor.FromRgb(
            ToByte((m[0] * r) + (m[1] * g) + (m[2] * b)),
            ToByte((m[3] * r) + (m[4] * g) + (m[5] * b)),
            ToByte((m[6] * r) + (m[7] * g) + (m[8] * b)));
    }

    private static double Delta(IndicatorColor a, IndicatorColor b) =>
        OklabColor.DeltaE(OklabColor.FromIndicatorColor(a), OklabColor.FromIndicatorColor(b));

    [Theory]
    [InlineData(0x00, 0x00, 0x00)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x2B, 0x2F, 0x3A)]
    [InlineData(0x00, 0x62, 0xA8)]
    [InlineData(0xB2, 0x35, 0x10)]
    [InlineData(0xFF, 0x00, 0x00)]
    [InlineData(0x00, 0xFF, 0xFF)]
    [InlineData(0x80, 0x80, 0x80)]
    public void Oklab_RoundTrip_ReproducesBytes(byte r, byte g, byte b)
    {
        var back = OklabColor.FromRgb(r, g, b).ToIndicatorColorInGamut();
        Assert.Equal(r, back.R);
        Assert.Equal(g, back.G);
        Assert.Equal(b, back.B);
        Assert.Equal(255, back.A);
    }

    [Fact]
    public void Oklab_OutOfGamut_PreservesLightnessAndHueInsteadOfClipping()
    {
        var blue = OklabColor.FromRgb(0x00, 0x00, 0xFF);
        var outside = new OklabColor(blue.L, blue.A * 1.4, blue.B * 1.4);

        var result = outside.ToIndicatorColorInGamut();
        var resultLab = OklabColor.FromIndicatorColor(result);

        Assert.InRange(Math.Abs(resultLab.L - outside.L), 0.0, 0.03);
        double hueIn = Math.Atan2(outside.B, outside.A);
        double hueOut = Math.Atan2(resultLab.B, resultLab.A);
        Assert.InRange(Math.Abs(hueIn - hueOut), 0.0, 0.1);
    }

    [Fact]
    public void Oklab_EdgeGuards_ReturnBlackOrWhite()
    {
        Assert.Equal(new IndicatorColor(200, 0, 0, 0), new OklabColor(0.0, 0.1, 0.1).ToIndicatorColorInGamut(200));
        Assert.Equal(new IndicatorColor(255, 0, 0, 0), new OklabColor(-0.5, 0.0, 0.0).ToIndicatorColorInGamut());
        Assert.Equal(new IndicatorColor(255, 255, 255, 255), new OklabColor(1.0, 0.0, 0.0).ToIndicatorColorInGamut());
        Assert.Equal(new IndicatorColor(255, 255, 255, 255), new OklabColor(2.0, 0.3, 0.3).ToIndicatorColorInGamut());
        Assert.Equal(new IndicatorColor(255, 0, 0, 0), new OklabColor(double.NaN, 0.0, 0.0).ToIndicatorColorInGamut());
        Assert.Equal(new IndicatorColor(255, 0, 0, 0), new OklabColor(0.5, double.PositiveInfinity, 0.0).ToIndicatorColorInGamut());
        Assert.Equal(new IndicatorColor(255, 0, 0, 0), new OklabColor(0.5, 0.0, double.NegativeInfinity).ToIndicatorColorInGamut());
    }

    [Fact]
    public void Defaults_CvdSeparation_IsRetainedForProtanopiaAndDeuteranopia()
    {
        double dp = OklabColor.DeltaE(Simulate(DefaultUp, Protanopia), Simulate(DefaultDown, Protanopia));
        double dd = OklabColor.DeltaE(Simulate(DefaultUp, Deuteranopia), Simulate(DefaultDown, Deuteranopia));

        // Green Up / red Down is the owner-selected default; the floor pins the separation measured for it
        // (a pure red/green pair such as #00C853 / #D50000 collapses to about 0.05 under deuteranopia).
        Assert.True(dp >= MinimumDichromatSeparation, $"Protanopia dE too small: {dp}");
        Assert.True(dd >= MinimumDichromatSeparation, $"Deuteranopia dE too small: {dd}");
    }

    [Fact]
    public void Ramp_FromNeutralToPeak_IsMonotoneAndFirstStepIsBounded()
    {
        var lut = new IndicatorColor[HeatmapColorRamp.LutEntryCount];
        HeatmapColorRamp.BuildLut(DefaultNeutral, DefaultUp, lut);

        double level1 = Delta(DefaultNeutral, lut[0]);
        Assert.InRange(level1, 0.03, 0.08);

        double prev = level1;
        for (int i = 1; i < lut.Length; i++)
        {
            double d = Delta(DefaultNeutral, lut[i]);
            Assert.True(d >= prev - 0.002, $"Distance from Neutral decreased at slot {i}: {d} < {prev}");
            prev = d;
        }

        Assert.Equal(DefaultUp, lut[^1]);
    }

    [Fact]
    public void BuildLut_PeakEndsMatchInterpolateStep()
    {
        var lut = new IndicatorColor[HeatmapColorRamp.LutEntryCount];
        HeatmapColorRamp.BuildLut(DefaultNeutral, DefaultDown, lut);

        Assert.Equal(HeatmapColorRamp.InterpolateStep(DefaultNeutral, DefaultDown, 0.0), lut[0]);
        Assert.Equal(HeatmapColorRamp.InterpolateStep(DefaultNeutral, DefaultDown, 1.0), lut[^1]);
        Assert.Equal(DefaultDown, lut[^1]);
        Assert.Equal(HeatmapColorRamp.InterpolateStep(DefaultNeutral, DefaultDown, double.NaN), lut[0]);
        Assert.Equal(HeatmapColorRamp.InterpolateStep(DefaultNeutral, DefaultDown, 5.0), lut[^1]);
    }

    [Fact]
    public void BuildLut_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            HeatmapColorRamp.BuildLut(DefaultNeutral, DefaultUp, new IndicatorColor[HeatmapColorRamp.LutEntryCount - 1]));
    }

    [Fact]
    public void DefaultPalette_AllLutSlotsAndNeutral_MeetWcagAaWithDefaultText()
    {
        var up = new IndicatorColor[HeatmapColorRamp.LutEntryCount];
        var down = new IndicatorColor[HeatmapColorRamp.LutEntryCount];
        HeatmapColorRamp.BuildLut(DefaultNeutral, DefaultUp, up);
        HeatmapColorRamp.BuildLut(DefaultNeutral, DefaultDown, down);

        Assert.True(WcagColorContrast.ContrastRatio(DefaultNeutral, DefaultText) >= WcagColorContrast.MinimumAaNormalTextRatio);
        for (int i = 0; i < up.Length; i++)
        {
            Assert.True(WcagColorContrast.ContrastRatio(up[i], DefaultText) >= WcagColorContrast.MinimumAaNormalTextRatio, $"Up slot {i}");
            Assert.True(WcagColorContrast.ContrastRatio(down[i], DefaultText) >= WcagColorContrast.MinimumAaNormalTextRatio, $"Down slot {i}");
        }
    }

    [Fact]
    public void Wcag_ContrastRatio_MatchesReferenceValues()
    {
        var black = IndicatorColor.FromRgb(0, 0, 0);
        var white = IndicatorColor.FromRgb(255, 255, 255);
        Assert.Equal(21.0, WcagColorContrast.ContrastRatio(black, white), precision: 6);
        Assert.Equal(1.0, WcagColorContrast.ContrastRatio(white, white), precision: 6);
        Assert.Equal(1.0, WcagColorContrast.RelativeLuminance(white), precision: 6);
    }

    [Fact]
    public void ResolveAccessibleTextColor_KeepsPreferredWhenItPasses()
    {
        var (text, lightOnDark) = WcagColorContrast.ResolveAccessibleTextColor(DefaultNeutral, DefaultText);
        Assert.Equal(DefaultText, text);
        Assert.True(lightOnDark);
    }

    [Fact]
    public void ResolveAccessibleTextColor_BrightBackground_FallsBackToDarkInk()
    {
        var bright = IndicatorColor.FromRgb(0xFF, 0xFF, 0x00);
        var (text, lightOnDark) = WcagColorContrast.ResolveAccessibleTextColor(bright, DefaultText);
        Assert.Equal(WcagColorContrast.DarkContrastFallback, text);
        Assert.False(lightOnDark);
        Assert.True(WcagColorContrast.ContrastRatio(bright, text) >= WcagColorContrast.MinimumAaNormalTextRatio);
    }

    [Fact]
    public void ResolveAccessibleTextColor_DarkBackgroundWithDarkPreferred_FallsBackToLightInk()
    {
        var dark = IndicatorColor.FromRgb(0x10, 0x10, 0x40);
        var darkPreferred = IndicatorColor.FromRgb(0x20, 0x20, 0x20);
        var (text, lightOnDark) = WcagColorContrast.ResolveAccessibleTextColor(dark, darkPreferred);
        Assert.Equal(WcagColorContrast.LightContrastFallback, text);
        Assert.True(lightOnDark);
    }

    [Fact]
    public void ResolveAccessibleTextColor_EveryGreyBackground_ReachesAaWithFallbackPair()
    {
        for (int v = 0; v <= 255; v++)
        {
            var bg = IndicatorColor.FromRgb((byte)v, (byte)v, (byte)v);
            var (text, _) = WcagColorContrast.ResolveAccessibleTextColor(bg, bg);
            Assert.True(WcagColorContrast.ContrastRatio(bg, text) >= WcagColorContrast.MinimumAaNormalTextRatio, $"grey {v}");
        }
    }
}
