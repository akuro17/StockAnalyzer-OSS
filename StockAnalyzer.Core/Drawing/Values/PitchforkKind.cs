namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// The kind of pitchfork. The numeric values equal the members of the GUI enum <c>PitchforkType</c> (pinned by a test), so a stored kind keeps its meaning.
/// </summary>
public enum PitchforkKind
{
    Andrews = 0,
    Schiff = 1,
    ModifiedSchiff = 2
}
