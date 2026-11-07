using System;
using System.Globalization;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// The one definition of the names under which an unreadable stored file is kept for diagnosis.
/// <list type="bullet">
/// <item><c>&lt;file&gt;.corrupted</c> (<see cref="Suffix"/>, <see cref="PathFor"/>): one fixed-name copy that a later fallback overwrites.</item>
/// <item><c>&lt;file&gt;.corrupt.&lt;UTC yyyyMMddTHHmmssZ&gt;[-n]</c> (<see cref="PreservedPathFor"/>): timestamped copies of which a bounded number is kept
/// (see <see cref="CorruptFilePreserver"/>).</item>
/// </list>
/// Every recovery path that keeps such a copy takes the path from here.
/// </summary>
public static class CorruptedFile
{
    public const string Suffix = ".corrupted";

    /// <summary>Separator between the original file name and the timestamp of a preserved copy.</summary>
    public const string PreservedMarker = ".corrupt.";

    /// <summary>Format of the UTC timestamp inside a preserved copy's name. Fixed-width, so names sort chronologically.</summary>
    public const string PreservedTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Separator between the timestamp and the ordinal that disambiguates copies made within the same second.</summary>
    public const char PreservedOrdinalSeparator = '-';

    /// <summary>The first copy of a given second carries no ordinal; later ones start at this value.</summary>
    public const int FirstPreservedOrdinal = 1;

    /// <summary>The path of the kept copy of <paramref name="filePath"/>.</summary>
    public static string PathFor(string filePath) => filePath + Suffix;

    /// <summary>
    /// The path of a timestamped preserved copy. <paramref name="ordinal"/> is <see cref="FirstPreservedOrdinal"/> for the first copy
    /// made in that second and 2, 3, ... when the name is already taken.
    /// </summary>
    public static string PreservedPathFor(string filePath, DateTime utcTimestamp, int ordinal = FirstPreservedOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, FirstPreservedOrdinal);
        var stamp = utcTimestamp.ToUniversalTime().ToString(PreservedTimestampFormat, CultureInfo.InvariantCulture);
        return filePath + PreservedMarker + stamp + (ordinal == FirstPreservedOrdinal ? string.Empty : PreservedOrdinalSeparator + ordinal.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The directory search pattern that matches preserved copies of the file called <paramref name="fileName"/>.</summary>
    public static string PreservedSearchPattern(string fileName) => fileName + PreservedMarker + "*";

    /// <summary>
    /// Parses what follows <see cref="PreservedMarker"/> in a file name. Returns false for anything that is not exactly a name
    /// produced by <see cref="PreservedPathFor"/>, so unrelated files that merely share the prefix are never mistaken for copies.
    /// </summary>
    public static bool TryParsePreservedSuffix(string suffix, out DateTime utcTimestamp, out int ordinal)
    {
        utcTimestamp = default;
        ordinal = FirstPreservedOrdinal;

        var separator = suffix.IndexOf(PreservedOrdinalSeparator);
        var stampText = separator < 0 ? suffix : suffix[..separator];
        if (separator >= 0
            && (!int.TryParse(suffix[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
                || ordinal <= FirstPreservedOrdinal))
        {
            return false;
        }

        return DateTime.TryParseExact(
            stampText,
            PreservedTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out utcTimestamp);
    }
}
