namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Specifies how a template should be applied to the current state.
/// </summary>
public enum TemplateApplyMode
{
    /// <summary>
    /// Completely replaces the current configuration with the template contents.
    /// </summary>
    Replace = 0,

    /// <summary>
    /// Adds the template items to the existing configuration.
    /// </summary>
    Add = 1,

    /// <summary>
    /// Merges template items with existing configuration, updating matches and keeping unique items.
    /// </summary>
    Merge = 2
}
