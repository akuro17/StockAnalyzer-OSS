namespace StockAnalyzer.Core.Models.Semantic;

/// <summary>
/// Defines the semantic role or meaning of a chart object or signal.
/// </summary>
public enum SemanticRole
{
    /// <summary>No semantic role defined (purely visual or untracked)</summary>
    None,

    /// <summary>Acts as a support level</summary>
    Support,

    /// <summary>Acts as a resistance level</summary>
    Resistance,

    /// <summary>Represents a broader trend direction</summary>
    Trend,

    /// <summary>Defines the boundary of a technical pattern</summary>
    PatternBoundary
}
