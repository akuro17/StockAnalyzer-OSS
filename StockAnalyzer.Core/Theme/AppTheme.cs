using SkiaSharp;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Centralized theme colors for the application.
/// </summary>
public static class AppTheme
{
    // Chart Backgrounds
    public static readonly SKColor ChartBackground = new(0xFA, 0xEB, 0xBB);
    
    // Grid & Axes
    public static readonly SKColor GridLine = SKColors.LightGray;
    public static readonly SKColor AxisText = SKColors.Black;
    public static readonly SKColor Crosshair = SKColors.Gray;
    
    // Candle/Chart Colors
    public static readonly SKColor Bullish = SKColors.Green; // Western Style
    public static readonly SKColor Bearish = SKColors.Red; // Western Style
    public static readonly SKColor Neutral = SKColors.Gray;
    
    // Tools
    public static readonly SKColor RulerFill = new(0, 0, 255, 30); // Blue with alpha
    public static readonly SKColor RulerStroke = SKColors.Blue;
    public static readonly SKColor RulerBackground = new(0, 0, 0, 160);
    public static readonly SKColor RulerArea = new(0, 120, 215, 50);
    public static readonly SKColor RulerText = SKColors.White;
    
    public static readonly SKColor CrosshairLineAlpha = new(0x08, 0x12, 0x1F, 0xA0);
    public static readonly SKColor CrosshairText = new(0x08, 0x12, 0x1F);

    // Volume Profile
    public static readonly SKColor VolumeProfileFill = new(0, 0, 255, 50);
    public static readonly SKColor VolumeProfilePOC = SKColors.Red;
    public static readonly SKColor VolumeProfileBorder = new(100, 100, 100, 100);
    
    // Volume
    public static readonly SKColor VolumeUp = new(0, 128, 0, 128); // Green
    public static readonly SKColor VolumeDown = new(255, 0, 0, 128); // Red
    
    // Header & Info
    public static readonly SKColor HeaderBackground = new(255, 255, 255, 180);
    
    // Chart Types
    public static readonly SKColor AreaChartFillBase = SKColors.DodgerBlue;
    public static readonly SKColor LineChartLine = SKColors.DodgerBlue;
    public static readonly SKColor OhlcBullish = new(0x10, 0xB9, 0x81); // Green
    public static readonly SKColor OhlcBearish = new(0xEF, 0x44, 0x44); // Red
    
    // Indicators
    public static readonly SKColor IchimokuTenkan = SKColors.Blue;
    public static readonly SKColor IchimokuKijun = SKColors.Red;
    public static readonly SKColor IchimokuChikou = SKColors.Green;
    public static readonly SKColor IchimokuSenkou = SKColors.Orange;
    
    // Reverse Watch Phases (Default)
    public static readonly SKColor RwPhase1 = SKColors.Red;
    public static readonly SKColor RwPhase2 = SKColors.Orange;
    public static readonly SKColor RwPhase3 = SKColors.Yellow;
    public static readonly SKColor RwPhase4 = SKColors.LightGreen;
    public static readonly SKColor RwPhase5 = SKColors.Green;
    public static readonly SKColor RwPhase6 = SKColors.Cyan;
    public static readonly SKColor RwPhase7 = SKColors.Blue;
    public static readonly SKColor RwPhase8 = SKColors.Magenta;
    
    // Reverse Watch XY Chart
    public static readonly SKColor RwChartBackground = new(30, 30, 30);
    public static readonly SKColor RwGridLine = new(40, 40, 40);
    public static readonly SKColor RwAxisLine = new(80, 80, 80);
    public static readonly SKColor RwAxisText = new(150, 150, 150);
    public static readonly SKColor RwCurvePath = new(100, 100, 100);
    public static readonly SKColor RwHoverPoint = SKColors.Red;

    // Geometric Pattern Overlay (Prompt 33-7)
    public static readonly SKColor GeometricResistanceLine = new(220, 50, 50, 180); // Red with alpha
    public static readonly SKColor GeometricSupportLine = new(50, 150, 50, 180); // Green with alpha
    public static readonly SKColor GeometricFormationFill = new(100, 100, 200, 25); // Blue tint
    public static readonly SKColor GeometricLabelText = new(80, 80, 80, 200);

    // Harmonic Patterns (Prompt 33-9)
    public static readonly SKColor HarmonicLineColor = SKColors.Blue;
    public static readonly SKColor HarmonicFillBull = new(0, 0, 255, 30); // Blue with alpha
    public static readonly SKColor HarmonicFillBear = new(255, 0, 0, 30); // Red with alpha
    public static readonly SKColor HarmonicPrzBull = new(0, 255, 0, 50); // Green with alpha
    public static readonly SKColor HarmonicPrzBear = new(255, 0, 0, 50); // Red with alpha

    // Cross Markers (Oscillator GC/DC)
    public static readonly SKColor CrossMarkerGolden = new(0x10, 0xB9, 0x81, 0xE0); // Green
    public static readonly SKColor CrossMarkerDead = new(0xEF, 0x44, 0x44, 0xE0); // Red
}
