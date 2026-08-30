using StockAnalyzer.Core.Models;
using System;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Defines the set of colors and styles used in the application.
/// Platform-agnostic (uses IndicatorColor).
/// </summary>
public record ThemeColors
{
    public static readonly ThemeColors Light = new ThemeColors
    {
        ChartBackground = IndicatorColor.FromUInt(0xFFF0F0F0),
        GridLine = IndicatorColor.FromUInt(0xFFE0E3EB),
        AxisText = IndicatorColor.FromUInt(0xFF555555),
        Crosshair = IndicatorColor.FromUInt(0xFF555555),
        CrosshairLineAlpha = IndicatorColor.FromArgb(0xA0, 0x55, 0x55, 0x55),
        CrosshairText = IndicatorColor.FromRgb(0x55, 0x55, 0x55),
        ZeroLine = IndicatorColor.FromUInt(0xFFCCCCCC),
        Bullish = IndicatorColor.FromUInt(0xFF388E3C),
        Bearish = IndicatorColor.FromUInt(0xFFC62828),
        BullishWick = IndicatorColor.FromUInt(0xFF388E3C),
        BearishWick = IndicatorColor.FromUInt(0xFFC62828),
        VolumeUp = IndicatorColor.FromUInt(0xFF388E3C),
        VolumeDown = IndicatorColor.FromUInt(0xFFC62828),
        SemanticPlus = IndicatorColor.FromUInt(0xFF388E3C),
        SemanticMinus = IndicatorColor.FromUInt(0xFFC62828),
        SemanticNeutral = IndicatorColor.FromUInt(0xFF1F2328),

        // Shell Colors
        ShellBackground = IndicatorColor.FromUInt(0xFFFFFFFF),
        ShellText = IndicatorColor.FromUInt(0xFF1F2328),
        ShellSecondaryText = IndicatorColor.FromUInt(0xFF656D76),
        ShellAccent = IndicatorColor.FromUInt(0xFF007ACC),
        ShellBorder = IndicatorColor.FromUInt(0xFFD0D7DE),

        // Button Colors
        ButtonBackground = IndicatorColor.FromUInt(0xFF007ACC),
        ButtonText = IndicatorColor.FromUInt(0xFFFFFFFF),
        ButtonHover = IndicatorColor.FromUInt(0xFF1C97EA),
        ButtonPressed = IndicatorColor.FromUInt(0xFF005A9E),

        // Control Surface Colors
        ControlBackground = IndicatorColor.FromUInt(0xFFF3F4F6),
        ControlBackgroundHover = IndicatorColor.FromUInt(0xFFE5E7EB),
        ControlBackgroundPressed = IndicatorColor.FromUInt(0xFFD1D5DB),

        IsDark = false
    };

    public static readonly ThemeColors Dark = new ThemeColors
    {
        ChartBackground = IndicatorColor.FromUInt(0xFF181A20),
        GridLine = IndicatorColor.FromUInt(0xFF2A2E39),
        AxisText = IndicatorColor.FromUInt(0xFF787B86),
        Crosshair = IndicatorColor.FromUInt(0xFF758696),
        CrosshairLineAlpha = IndicatorColor.FromArgb(0xA0, 0x75, 0x86, 0x96),
        CrosshairText = IndicatorColor.FromRgb(0x75, 0x86, 0x96),
        ZeroLine = IndicatorColor.FromUInt(0xFF444444),
        Bullish = IndicatorColor.FromUInt(0xFF4CAF50),
        Bearish = IndicatorColor.FromUInt(0xFFF44336),
        BullishWick = IndicatorColor.FromUInt(0xFF4CAF50),
        BearishWick = IndicatorColor.FromUInt(0xFFF44336),
        VolumeUp = IndicatorColor.FromUInt(0xFF4CAF50),
        VolumeDown = IndicatorColor.FromUInt(0xFFF44336),
        SemanticPlus = IndicatorColor.FromUInt(0xFF4CAF50),
        SemanticMinus = IndicatorColor.FromUInt(0xFFF44336),
        SemanticNeutral = IndicatorColor.FromUInt(0xFFE0E0E0),
        HeaderBackground = IndicatorColor.FromArgb(180, 30, 30, 30),
        VolumeProfileBorder = IndicatorColor.FromArgb(100, 200, 200, 200),
        GeometricLabelText = IndicatorColor.FromArgb(200, 200, 200, 200),

        // Shell Colors
        ShellBackground = IndicatorColor.FromUInt(0xFF181A20),
        ShellText = IndicatorColor.FromUInt(0xFFE0E0E0),
        ShellSecondaryText = IndicatorColor.FromUInt(0xFF8B949E),
        ShellAccent = IndicatorColor.FromUInt(0xFF648CFF),
        ShellBorder = IndicatorColor.FromUInt(0xFF363A45),

        // Button Colors
        ButtonBackground = IndicatorColor.FromUInt(0xFF2D64F0),
        ButtonText = IndicatorColor.FromUInt(0xFFF5F5FF),
        ButtonHover = IndicatorColor.FromUInt(0xFF5082FF),
        ButtonPressed = IndicatorColor.FromUInt(0xFF234BC8),

        // Control Surface Colors
        ControlBackground = IndicatorColor.FromUInt(0xFF252930),
        ControlBackgroundHover = IndicatorColor.FromUInt(0xFF2D333B),
        ControlBackgroundPressed = IndicatorColor.FromUInt(0xFF1C2128),

        IsDark = true
    };

    public bool IsDark { get; init; } = false;

    // Chart Backgrounds
    public IndicatorColor ChartBackground { get; init; } = IndicatorColor.FromUInt(0xFFF8F9FA);

    // Grid & Axes
    public IndicatorColor GridLine { get; init; } = IndicatorColor.FromUInt(0xFFE0E3EB);
    public IndicatorColor AxisText { get; init; } = IndicatorColor.FromUInt(0xFF9598A1);
    public IndicatorColor Crosshair { get; init; } = IndicatorColor.FromUInt(0xFF9598A1);
    public IndicatorColor ZeroLine { get; init; } = IndicatorColor.FromUInt(0xFFCCCCCC);

    // Candle/Chart Colors
    public IndicatorColor Bullish { get; init; } = IndicatorColor.FromUInt(0xFF388E3C);
    public IndicatorColor Bearish { get; init; } = IndicatorColor.FromUInt(0xFFC62828);
    public IndicatorColor BullishWick { get; init; } = IndicatorColor.FromUInt(0xFF388E3C);
    public IndicatorColor BearishWick { get; init; } = IndicatorColor.FromUInt(0xFFC62828);
    public IndicatorColor Neutral { get; init; } = new IndicatorColor(255, 128, 128, 128); // Gray

    // Semantic Colors
    public IndicatorColor SemanticPlus { get; init; } = IndicatorColor.FromUInt(0xFF388E3C);
    public IndicatorColor SemanticMinus { get; init; } = IndicatorColor.FromUInt(0xFFC62828);
    public IndicatorColor SemanticNeutral { get; init; } = IndicatorColor.FromUInt(0xFF787B86);

    // Tools
    public IndicatorColor RulerFill { get; init; } = IndicatorColor.FromArgb(30, 0, 0, 255);
    public IndicatorColor RulerStroke { get; init; } = new IndicatorColor(255, 0, 0, 255); // Blue
    public IndicatorColor RulerBackground { get; init; } = IndicatorColor.FromArgb(160, 0, 0, 0);
    public IndicatorColor RulerArea { get; init; } = IndicatorColor.FromArgb(50, 0, 120, 215);
    public IndicatorColor RulerText { get; init; } = new IndicatorColor(255, 255, 255, 255); // White

    public IndicatorColor CrosshairLineAlpha { get; init; } = IndicatorColor.FromArgb(0xA0, 0x08, 0x12, 0x1F);
    public IndicatorColor CrosshairText { get; init; } = IndicatorColor.FromRgb(0x08, 0x12, 0x1F);

    /// <summary>
    /// Creates a new ThemeColors instance with derived crosshair colors based on a base IndicatorColor.
    /// Used to maintain UI-agnostic ViewModels.
    /// </summary>
    public ThemeColors WithDerivedCrosshair(IndicatorColor crosshairBase)
    {
        return this with
        {
            Crosshair = crosshairBase,
            CrosshairLineAlpha = crosshairBase.WithAlpha(0xA0),
            CrosshairText = crosshairBase.WithAlpha(0xFF)
        };
    }

    // Volume Profile
    public IndicatorColor VolumeProfileFill { get; init; } = IndicatorColor.FromArgb(50, 0, 0, 255);
    public IndicatorColor VolumeProfilePOC { get; init; } = new IndicatorColor(255, 255, 0, 0); // Red
    public IndicatorColor VolumeProfileBorder { get; init; } = IndicatorColor.FromArgb(100, 100, 100, 100);

    // Volume
    public IndicatorColor VolumeUp { get; init; } = IndicatorColor.FromUInt(0xFF388E3C);
    public IndicatorColor VolumeDown { get; init; } = IndicatorColor.FromUInt(0xFFC62828);

    // Header & Info
    public IndicatorColor HeaderBackground { get; init; } = IndicatorColor.FromArgb(180, 255, 255, 255);

    // Shell UI Colors
    public IndicatorColor ShellBackground { get; init; } = IndicatorColor.FromUInt(0xFF181A20);
    public IndicatorColor ShellText { get; init; } = IndicatorColor.FromUInt(0xFFE0E0E0);
    public IndicatorColor ShellSecondaryText { get; init; } = IndicatorColor.FromUInt(0xFF8B949E);
    public IndicatorColor ShellAccent { get; init; } = IndicatorColor.FromUInt(0xFF007ACC);
    public IndicatorColor ShellBorder { get; init; } = IndicatorColor.FromUInt(0xFF363A45);

    // Button Colors
    public IndicatorColor ButtonBackground { get; init; } = IndicatorColor.FromUInt(0xFF007ACC);
    public IndicatorColor ButtonText { get; init; } = IndicatorColor.FromUInt(0xFFFFFFFF);
    public IndicatorColor ButtonHover { get; init; } = IndicatorColor.FromUInt(0xFF1C97EA);
    public IndicatorColor ButtonPressed { get; init; } = IndicatorColor.FromUInt(0xFF005A9E);

    // Control Surface Colors
    public IndicatorColor ControlBackground { get; init; } = IndicatorColor.FromUInt(0xFFF3F4F6);
    public IndicatorColor ControlBackgroundHover { get; init; } = IndicatorColor.FromUInt(0xFFE5E7EB);
    public IndicatorColor ControlBackgroundPressed { get; init; } = IndicatorColor.FromUInt(0xFFD1D5DB);

    // Chart Types
    public IndicatorColor AreaChartFillBase { get; init; } = new IndicatorColor(255, 30, 144, 255); // DodgerBlue
    public IndicatorColor LineChartLine { get; init; } = new IndicatorColor(255, 30, 144, 255); // DodgerBlue
    public IndicatorColor OhlcBullish { get; init; } = IndicatorColor.FromRgb(0x10, 0xB9, 0x81);
    public IndicatorColor OhlcBearish { get; init; } = IndicatorColor.FromRgb(0xEF, 0x44, 0x44);

    // Indicators
    public IndicatorColor IchimokuTenkan { get; init; } = new IndicatorColor(255, 0, 0, 255); // Blue
    public IndicatorColor IchimokuKijun { get; init; } = new IndicatorColor(255, 255, 0, 0); // Red
    public IndicatorColor IchimokuChikou { get; init; } = new IndicatorColor(255, 0, 128, 0); // Green
    public IndicatorColor IchimokuSenkou { get; init; } = new IndicatorColor(255, 255, 165, 0); // Orange

    // Reverse Watch Phases (Default)
    public IndicatorColor RwPhase1 { get; init; } = new IndicatorColor(255, 255, 0, 0); // Red
    public IndicatorColor RwPhase2 { get; init; } = new IndicatorColor(255, 255, 165, 0); // Orange
    public IndicatorColor RwPhase3 { get; init; } = new IndicatorColor(255, 255, 255, 0); // Yellow
    public IndicatorColor RwPhase4 { get; init; } = new IndicatorColor(255, 144, 238, 144); // LightGreen
    public IndicatorColor RwPhase5 { get; init; } = new IndicatorColor(255, 0, 128, 0); // Green
    public IndicatorColor RwPhase6 { get; init; } = new IndicatorColor(255, 0, 255, 255); // Cyan
    public IndicatorColor RwPhase7 { get; init; } = new IndicatorColor(255, 0, 0, 255); // Blue
    public IndicatorColor RwPhase8 { get; init; } = new IndicatorColor(255, 255, 0, 255); // Magenta

    // Reverse Watch XY Chart
    public IndicatorColor RwChartBackground { get; init; } = IndicatorColor.FromRgb(30, 30, 30);
    public IndicatorColor RwGridLine { get; init; } = IndicatorColor.FromRgb(40, 40, 40);
    public IndicatorColor RwAxisLine { get; init; } = IndicatorColor.FromRgb(80, 80, 80);
    public IndicatorColor RwAxisText { get; init; } = IndicatorColor.FromRgb(150, 150, 150);
    public IndicatorColor RwCurvePath { get; init; } = IndicatorColor.FromRgb(100, 100, 100);
    public IndicatorColor RwHoverPoint { get; init; } = new IndicatorColor(255, 255, 0, 0); // Red

    // Geometric Pattern Overlay (Prompt 33-7)
    public IndicatorColor GeometricResistanceLine { get; init; } = IndicatorColor.FromArgb(180, 220, 50, 50);
    public IndicatorColor GeometricSupportLine { get; init; } = IndicatorColor.FromArgb(180, 50, 150, 50);
    public IndicatorColor GeometricFormationFill { get; init; } = IndicatorColor.FromArgb(25, 100, 100, 200);
    public IndicatorColor GeometricLabelText { get; init; } = IndicatorColor.FromArgb(200, 80, 80, 80);

    // Harmonic Patterns (Prompt 33-9)
    public IndicatorColor HarmonicLineColor { get; init; } = new IndicatorColor(255, 0, 0, 255); // Blue
    public IndicatorColor HarmonicFillBull { get; init; } = IndicatorColor.FromArgb(30, 0, 0, 255);
    public IndicatorColor HarmonicFillBear { get; init; } = IndicatorColor.FromArgb(30, 255, 0, 0);
    public IndicatorColor HarmonicPrzBull { get; init; } = IndicatorColor.FromArgb(50, 0, 255, 0);
    public IndicatorColor HarmonicPrzBear { get; init; } = IndicatorColor.FromArgb(50, 255, 0, 0);

    // Cross Markers (Oscillator GC/DC)
    public IndicatorColor CrossMarkerGolden { get; init; } = IndicatorColor.FromArgb(0xE0, 0x10, 0xB9, 0x81);
    public IndicatorColor CrossMarkerDead { get; init; } = IndicatorColor.FromArgb(0xE0, 0xEF, 0x44, 0x44);

    public IndicatorColor GetSemanticColor(SemanticRole role) => role switch
    {
        SemanticRole.Bullish    => SemanticPlus,
        SemanticRole.Bearish    => SemanticMinus,
        SemanticRole.Neutral    => SemanticNeutral,
        SemanticRole.Support    => GeometricSupportLine,
        SemanticRole.Resistance => GeometricResistanceLine,
        SemanticRole.EntryLong  => CrossMarkerGolden,
        SemanticRole.EntryShort => CrossMarkerDead,
        SemanticRole.Exit       => SemanticNeutral,
        SemanticRole.PivotHigh  => SemanticMinus,
        SemanticRole.PivotLow   => SemanticPlus,
        _                       => Neutral, // Fallback
    };
}
