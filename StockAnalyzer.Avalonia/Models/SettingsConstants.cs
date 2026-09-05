using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Models;

/// <summary>
/// Centralizes constants and metadata for the unified settings system.
/// </summary>
public static class SettingsConstants
{
    public const string VersionSuffix = "";
    public const int SettingsPreviewThrottleIntervalMs = 500;
    public const int UI_StandardInputThrottleMs = 300;
    public const int UI_FastInputThrottleMs = 150;
    public const int UI_StatusMessageDurationMs = 3000;
    public const int UI_AnimationFastMs = 150;
    public const int UI_AnimationStandardMs = 300;
    public const int UI_WorkspaceSaveThrottleMs = 500;

    public static class Keys
    {
        public const string Theme = "Theme";
        public const string Fonts = "Fonts";
        public const string Python = "Python";
        public const string AIPredictions = "AIPredictions";
        public const string Notes = "Notes";
        public const string Chart = "Chart";
        public const string Notifications = "Notifications";
        public const string Connectivity = "Connectivity";
        public const string Advanced = "Advanced";
        public const string About = "About";

        // Chart Sub-categories
        public const string ChartDrawing = "Chart.Drawing";
        public const string ChartCandlestick = "Chart.Candlestick";
        public const string ChartOhlcBar = "Chart.OhlcBar";
        public const string ChartLine = "Chart.Line";
        public const string ChartArea = "Chart.Area";
        public const string ChartHeikinAshi = "Chart.HeikinAshi";
        public const string ChartRenko = "Chart.Renko";
        public const string ChartPnf = "Chart.Pnf";
        public const string ChartKagi = "Chart.Kagi";
        public const string ChartTlb = "Chart.Tlb";
        public const string ChartRelativePerformance = "Chart.RelativePerformance";
        public const string ChartReverseWatch = "Chart.ReverseWatch";
    }

    public static readonly IReadOnlyList<SettingsCategory> Categories = new List<SettingsCategory>
    {
        new(Keys.Theme, "Settings_Theme", "SettingsThemeIcon"),
        new(Keys.Fonts, "Settings_Fonts", "SettingsFontsIcon"),
        new(Keys.Python, "Settings_Python", "SettingsPythonIcon"),
        new(Keys.AIPredictions, "Settings_AIPredictions", "SettingsAdvIcon"),
        new(Keys.Notes, "Settings_Notes", "SettingsNotesIcon"),
        new(Keys.Chart, "Settings_Chart", "SettingsChartIcon", true, new List<SettingsCategory>
        {
            new(Keys.ChartDrawing, "Settings_Chart_Drawing_CategoryTitle", "SettingsChartIcon"),
            new(Keys.ChartCandlestick, "ChartType_Candlestick", "SettingsChartIcon"),
            new(Keys.ChartOhlcBar, "ChartType_OHLCBar", "SettingsChartIcon"),
            new(Keys.ChartLine, "ChartType_Line", "SettingsChartIcon"),
            new(Keys.ChartArea, "ChartType_Area", "SettingsChartIcon"),
            new(Keys.ChartHeikinAshi, "ChartType_HeikinAshi", "SettingsChartIcon"),
            new(Keys.ChartRenko, "ChartType_Renko", "SettingsChartIcon"),
            new(Keys.ChartPnf, "ChartType_PointAndFigure", "SettingsChartIcon"),
            new(Keys.ChartKagi, "ChartType_Kagi", "SettingsChartIcon"),
            new(Keys.ChartTlb, "ChartType_ThreeLineBreak", "SettingsChartIcon"),
            new(Keys.ChartRelativePerformance, "ChartType_RelativePerformance", "SettingsChartIcon"),
            new(Keys.ChartReverseWatch, "ChartType_ReverseWatch", "SettingsChartIcon")
        }),
        new(Keys.About, "Settings_About", "SettingsAboutIcon")
    };
    
    public static readonly IReadOnlyList<string> AvailableColors = new List<string> 
    { 
        "Red", "Green", "Blue", "Black", "White", "Gray", "Yellow", "Orange", "Purple", "Cyan", "Magenta" 
    };

    public static readonly IReadOnlyList<NamedColor> ReverseWatchColorOptions = new List<NamedColor>
    {
        new("Lime Green", "#CCFF99"),
        new("Green", "#00CC00"),
        new("Dark Green", "#006600"),
        new("Yellow", "#FFD700"),
        new("Salmon", "#FF9999"),
        new("Red", "#FF0000"),
        new("Dark Red", "#8B0000"),
        new("Blue Gray", "#607D8B"),
        new("Blue", "Blue"),
        new("Black", "Black"),
        new("Orange", "Orange"),
        new("Violet", "Violet"),
        new("Indigo", "Indigo"),
        new("Gray", "Gray")
    };
}
