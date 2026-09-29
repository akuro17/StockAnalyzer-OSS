namespace StockAnalyzer.Core.Models.Semantic;

/// <summary>
/// Defines an entity or signal that carries semantic meaning and priority.
/// </summary>
public interface ISemanticSignal
{
    /// <summary>
    /// The index or timestamp ID where this signal occurs.
    /// </summary>
    int Index { get; }

    /// <summary>
    /// The priority of the signal for conflict resolution (higher wins).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// The semantic role (e.g., Support, Resistance).
    /// </summary>
    SemanticRole Role { get; }
}
