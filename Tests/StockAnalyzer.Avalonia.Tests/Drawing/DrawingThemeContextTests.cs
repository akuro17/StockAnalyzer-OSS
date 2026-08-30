using Avalonia.Media;
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

        SharedFontSettingsManager.SetDetailFontSize(12.0); // restore default for other tests
    }
}
