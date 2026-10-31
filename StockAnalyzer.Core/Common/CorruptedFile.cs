namespace StockAnalyzer.Core.Common;

/// <summary>
/// The one definition of the name under which an unreadable stored file is kept for diagnosis before a fallback replaces it
/// (<c>&lt;file&gt;.corrupted</c>). Every recovery path that keeps such a copy takes the path from here.
/// </summary>
public static class CorruptedFile
{
    public const string Suffix = ".corrupted";

    /// <summary>The path of the kept copy of <paramref name="filePath"/>.</summary>
    public static string PathFor(string filePath) => filePath + Suffix;
}
