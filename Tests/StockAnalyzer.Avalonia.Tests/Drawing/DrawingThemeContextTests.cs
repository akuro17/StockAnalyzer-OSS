using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// DrawingThemeContext.Initialize() is guarded to run only once per process (mirrors how it's
/// called exactly once at app startup in App.axaml.cs), so these tests exercise the live-update
/// path (PropertyChanged subscriptions) against whichever IThemeManager/IFontSettingsManager
/// instance first initialized it, rather than re-verifying construction-time defaults.
/// </summary>
[Collection("DrawingThemeContext State")]
public class DrawingThemeContextTests
{
    private static readonly ThemeManager SharedThemeManager = new();
    private static readonly FontSettingsManager SharedFontSettingsManager = new();

    public DrawingThemeContextTests()
    {
        DrawingThemeContext.Initialize(SharedThemeManager, SharedFontSettingsManager);
    }

    [Fact]
    public void TextColor_TracksThemeManagerAxisTextColor_OnThemeChange()
    {
        var customTheme = SharedThemeManager.CurrentTheme with
        {
            AxisText = StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFF123456)
        };

        SharedThemeManager.ChangeTheme(customTheme);

        Assert.Equal(customTheme.AxisText.ToSkColor(), DrawingThemeContext.TextColor);
    }

    [Fact]
    public void FontSize_TracksFontSettingsManagerDetailFontSize_OnSizeChange()
    {
        SharedFontSettingsManager.SetDetailFontSize(18.0);

        Assert.Equal(18.0f, DrawingThemeContext.FontSize);

        SharedFontSettingsManager.SetDetailFontSize(FontDefaults.Detail); // restore default for other tests
    }

    [Fact]
    public void DatePriceRangeObject_TracksThemeShellTextAndDetailFontSize()
    {
        var originalTheme = SharedThemeManager.CurrentTheme;
        double originalDetailSize = SharedFontSettingsManager.DetailFontSize;

        static byte[] RenderPng(DatePriceRangeObject obj)
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 300));
            surface.Canvas.Clear(SKColors.Black);
            obj.Render(surface.Canvas, new PixelCoordinateTransform());
            using var snapshot = surface.Snapshot();
            return snapshot.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }

        try
        {
            using var obj = new DatePriceRangeObject(
                PixelCoordinateTransform.Chart(100, 200),
                PixelCoordinateTransform.Chart(300, 100));

            SharedFontSettingsManager.SetDetailFontSize(12.0);
            SharedThemeManager.ChangeTheme(originalTheme with
            {
                ShellText = StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFFFFFFFF),
                AxisText = StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFF112233)
            });
            byte[] baselinePng = RenderPng(obj);

            // Changing AxisText should not alter DatePriceRange label color (it tracks ShellText).
            SharedThemeManager.ChangeTheme(SharedThemeManager.CurrentTheme with
            {
                AxisText = StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFFFF0000)
            });
            Assert.Equal(baselinePng, RenderPng(obj));

            // Changing ShellText changes DatePriceRange label color.
            SharedThemeManager.ChangeTheme(SharedThemeManager.CurrentTheme with
            {
                ShellText = StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFF00FF00)
            });
            byte[] recoloredPng = RenderPng(obj);
            Assert.NotEqual(baselinePng, recoloredPng);

            // Changing DetailFontSize changes DatePriceRange label size.
            SharedFontSettingsManager.SetDetailFontSize(20.0);
            byte[] resizedPng = RenderPng(obj);
            Assert.NotEqual(recoloredPng, resizedPng);
        }
        finally
        {
            SharedThemeManager.ChangeTheme(originalTheme);
            SharedFontSettingsManager.SetDetailFontSize(originalDetailSize);
        }
    }
}
