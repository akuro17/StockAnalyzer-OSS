using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing custom application and chart theme colors.
/// </summary>
public class ThemeTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Theme;

    /// <summary>
    /// The full theme colors configuration for this template.
    /// </summary>
    public ThemeColors Colors { get; set; } = ThemeColors.Dark;
}
