using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public class SeasonalitySharedAxisFormattingTests
{
    [Fact]
    public void ResolveYearColor_WithOverridePalette_ReturnsTheSlotColour()
    {
        var palette = new[] { new SKColor(10, 20, 30), new SKColor(40, 50, 60), new SKColor(70, 80, 90) };

        Assert.Equal(palette[0], SeasonalitySharedAxisFormatting.ResolveYearColor(0, palette, ThemeColors.Dark));
        Assert.Equal(palette[2], SeasonalitySharedAxisFormatting.ResolveYearColor(2, palette, ThemeColors.Dark));
    }

    [Fact]
    public void ResolveYearColor_IndexBeyondPalette_KeepsTheSlotHueButShiftsTheShade()
    {
        var palette = new[] { new SKColor(0x2E, 0x7D, 0x32), new SKColor(0x15, 0x65, 0xC0) };

        // Rank 2 -> slot 0, cycle 1 (lighten): same base hue, different colour from the raw slot.
        SKColor rank2 = SeasonalitySharedAxisFormatting.ResolveYearColor(2, palette, ThemeColors.Dark);
        Assert.NotEqual(palette[0], rank2);
        Assert.Equal(SeasonalitySharedAxisFormatting.DeriveYearShade(palette[0], 1), rank2);

        palette[0].ToHsv(out float baseHue, out _, out _);
        rank2.ToHsv(out float shadedHue, out _, out _);
        // Hue is held; the ~1 degree slack is 8-bit RGB requantisation, not a hue shift.
        Assert.True(System.Math.Abs(baseHue - shadedHue) < 2f, $"hue drifted {baseHue}->{shadedHue}");

        // Rank 5 -> slot 1, cycle 2 (darken).
        SKColor rank5 = SeasonalitySharedAxisFormatting.ResolveYearColor(5, palette, ThemeColors.Dark);
        Assert.Equal(SeasonalitySharedAxisFormatting.DeriveYearShade(palette[1], 2), rank5);
    }

    [Fact]
    public void DeriveYearShade_CycleZero_ReturnsTheBaseColourUnchanged()
    {
        var baseColor = new SKColor(0x2E, 0x7D, 0x32, 0xC0);

        Assert.Equal(baseColor, SeasonalitySharedAxisFormatting.DeriveYearShade(baseColor, 0));
    }

    [Fact]
    public void DeriveYearShade_PreservesHueAndAlpha_AndAlternatesLightenDarken()
    {
        var baseColor = new SKColor(0x2E, 0x7D, 0x32, 0x99);
        baseColor.ToHsv(out float baseHue, out _, out float baseValue);

        SKColor lightened = SeasonalitySharedAxisFormatting.DeriveYearShade(baseColor, 1); // odd -> lighten
        SKColor darkened = SeasonalitySharedAxisFormatting.DeriveYearShade(baseColor, 2);  // even -> darken

        lightened.ToHsv(out float lightHue, out _, out float lightValue);
        darkened.ToHsv(out float darkHue, out _, out float darkValue);

        // Hue held to within 8-bit RGB requantisation slack.
        Assert.True(System.Math.Abs(baseHue - lightHue) < 2f, $"lighten hue drift {baseHue}->{lightHue}");
        Assert.True(System.Math.Abs(baseHue - darkHue) < 2f, $"darken hue drift {baseHue}->{darkHue}");
        Assert.Equal(0x99, lightened.Alpha);
        Assert.Equal(0x99, darkened.Alpha);
        Assert.True(lightValue > baseValue);
        Assert.True(darkValue < baseValue);
    }

    [Fact]
    public void ResolveYearColor_NullOrEmptyOverride_FallsBackToTheThemePalette()
    {
        SKColor themeSlot0 = SeasonalitySharedAxisFormatting.SeriesPaletteColor(0, ThemeColors.Dark);

        Assert.Equal(themeSlot0, SeasonalitySharedAxisFormatting.ResolveYearColor(0, null, ThemeColors.Dark));
        Assert.Equal(themeSlot0, SeasonalitySharedAxisFormatting.ResolveYearColor(0, new List<SKColor>(), ThemeColors.Dark));
    }

    [Theory]
    // Fully-supported segment (count == overlay depth) stays opaque: the legacy flat look for full bins.
    [InlineData(10, 10, 255)]
    // Fewest supporting years -> the floor.
    [InlineData(2, 10, SeasonalitySharedAxisFormatting.MeanPathMinAlpha)]
    // Midpoint support -> halfway up the 140..255 ramp (140 + 0.5 * 115 = 197.5 -> 198).
    [InlineData(6, 10, 198)]
    // depth == count is always fully opaque regardless of the depth value.
    [InlineData(3, 3, 255)]
    // Out-of-range low / high are clamped into [floor, 255].
    [InlineData(1, 10, SeasonalitySharedAxisFormatting.MeanPathMinAlpha)]
    [InlineData(99, 10, 255)]
    public void MeanPathConfidenceAlpha_RampsLinearlyFromFloorToOpaque(int sampleCount, int overlayDepth, int expected)
    {
        Assert.Equal((byte)expected, SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(sampleCount, overlayDepth));
    }

    [Theory]
    // Overlay too shallow for SampleCount to vary (<= MinYearsForMeanPath = 2): keep the pre-P3-3 flat opacity.
    [InlineData(2, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 0)]
    public void MeanPathConfidenceAlpha_ShallowOverlay_StaysFullyOpaque(int sampleCount, int overlayDepth)
    {
        Assert.Equal((byte)255, SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(sampleCount, overlayDepth));
    }

    [Fact]
    public void MeanPathConfidenceAlpha_IsMonotonicInSupportingYears()
    {
        byte previous = 0;
        for (int count = 2; count <= 20; count++)
        {
            byte alpha = SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(count, 20);
            Assert.True(alpha >= previous, $"alpha dropped at count {count}: {previous} -> {alpha}");
            previous = alpha;
        }

        Assert.Equal((byte)SeasonalitySharedAxisFormatting.MeanPathMinAlpha, SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(2, 20));
        Assert.Equal((byte)255, SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(20, 20));
    }

    [Fact]
    public void BuildRhoTicks_DefaultAutoStep_AlwaysReachesRhoMaxWithinTheGridCircleCap()
    {
        // CalculateNiceStep only ever rounds UP, so the auto step is never smaller than
        // (rhoMax - rhoMin) / GridStepDivisor: covering the range then needs at most
        // GridStepDivisor + 1 ticks, comfortably under MaximumGridCircleCount. Confirms the
        // guarantee holds by construction for every caller that does not pass a manual step.
        (double[] values, string[] labels) = SeasonalitySharedAxisFormatting.BuildRhoTicks(-0.083d, 0.517d, manualStep: null);

        Assert.NotEmpty(values);
        Assert.True(values.Length <= SeasonalitySharedAxisFormatting.MaximumGridCircleCount);
        Assert.True(values[^1] >= 0.517d - 1e-9);
        Assert.Equal(values.Length, labels.Length);
    }

    [Fact]
    public void BuildRhoTicks_ManualStepTooFineForTheGridCircleCap_LastTickIsForcedToRhoMax()
    {
        // A manual step this fine against this range would need 100 ticks to reach rhoMax on the
        // "nice step" ladder alone -- far past MaximumGridCircleCount (8). Regression test for the
        // rho-tick-undershoot gap on the manualStep path found during the sa_constraint_check audit:
        // the outermost tick must still reach rhoMax even when the grid-circle cap binds first.
        (double[] values, string[] labels) = SeasonalitySharedAxisFormatting.BuildRhoTicks(0d, 1.0d, manualStep: 0.01m);

        Assert.Equal(SeasonalitySharedAxisFormatting.MaximumGridCircleCount, values.Length);
        for (int index = 1; index < values.Length; index++)
        {
            Assert.True(values[index] > values[index - 1], $"tick {index} did not strictly ascend: {values[index - 1]} -> {values[index]}");
        }

        Assert.Equal(1.0d, values[^1], 9);
        Assert.Equal(labels.Length, values.Length);
    }
}
