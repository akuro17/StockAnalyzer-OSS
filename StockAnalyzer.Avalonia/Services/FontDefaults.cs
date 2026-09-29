namespace StockAnalyzer.Avalonia.Services
{
    /// <summary>
    /// Single source of truth for the default sizes of the five semantic font resources
    /// (Settings &gt; Fonts). Used when no user_font_settings.json exists and by "Reset to Default";
    /// saved user values are never overridden by these.
    /// </summary>
    public static class FontDefaults
    {
        public const double Base = 16.0;
        public const double Title = 20.0;
        public const double Detail = 16.0;
        public const double Helper = 16.0;
        public const double Tooltip = 16.0;
    }
}
