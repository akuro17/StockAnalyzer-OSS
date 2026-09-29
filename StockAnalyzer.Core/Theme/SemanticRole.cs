using SkiaSharp;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Defines the semantic role of a chart drawing element.
/// Color resolution is performed by ThemeColors based on this role.
/// </summary>
public enum SemanticRole
{
    // === Direction (Objective classification of market direction) ===
    Bullish,        // Price increasing direction
    Bearish,        // Price decreasing direction
    Neutral,        // No direction
    
    // === Technical Structure ===
    Support,        // Support line (Resolves to Bullish direction)
    Resistance,     // Resistance line (Resolves to Bearish direction)
    
    // === Signals ===
    EntryLong,      // Long entry (Resolves to Bullish direction)
    EntryShort,     // Short entry (Resolves to Bearish direction)
    Exit,           // Execution/Exit (Resolves to Neutral direction)
    
    // === Pivot/Structure Shift ===
    PivotHigh,      // High pivot (Origin of Bearish pressure)
    PivotLow,       // Low pivot (Origin of Bullish pressure)
}

/// <summary>
/// Extension methods for SemanticRole.
/// </summary>
public static class SemanticRoleExtensions
{
    /// <summary>
    /// Returns a human-readable label for the role.
    /// Useful for FR-39-11-02 (Semantic Label).
    /// </summary>
    public static string ToLabel(this SemanticRole role) => role switch
    {
        SemanticRole.Bullish    => "強気",
        SemanticRole.Bearish    => "弱気",
        SemanticRole.Neutral    => "中立",
        SemanticRole.Support    => "支持線",
        SemanticRole.Resistance => "抵抗線",
        SemanticRole.EntryLong  => "買エントリー",
        SemanticRole.EntryShort => "売エントリー",
        SemanticRole.Exit       => "決済",
        SemanticRole.PivotHigh  => "高値ピボット",
        SemanticRole.PivotLow   => "安値ピボット",
        _                       => role.ToString()
    };
}
