using System.IO;

namespace StockAnalyzer.Core.Common;

/// <summary>The single definition of a plain file name: one path segment that cannot leave the directory it is joined to.</summary>
public static class PlainFileName
{
    /// <summary>
    /// True for a non-empty name that is its own file-name part (no directory separator of either operating system), is neither
    /// <c>.</c> nor <c>..</c> and contains no character that is invalid in a file name here.
    /// </summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name == Path.GetFileName(name) && name is not ("." or "..")
        && !name.Contains('/') && !name.Contains('\\') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
