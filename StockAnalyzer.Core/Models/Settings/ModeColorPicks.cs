namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The color the user picked for one Notes appearance setting, one per Light/Dark mode; null means "no pick for that mode, use the
/// default of the theme in effect". A value type so a settings page can snapshot and restore both modes at once, which a single
/// effective color cannot do (a pick made in one mode is invisible while the other mode is shown).
/// </summary>
public readonly record struct ModeColorPicks(IndicatorColor? Dark, IndicatorColor? Light);
