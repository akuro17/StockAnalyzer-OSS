using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable template containing a filter subtree (rules and nested children).
/// </summary>
public class FilterTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Filter;

    /// <summary>
    /// The root of the saved filter subtree, including its rules and nested children.
    /// </summary>
    public FilterSettings RootSettings { get; set; } = new();
}
