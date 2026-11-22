using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// The shared input corpus of the symbol-spelling equivalence tests (NormalizeTicker, DrawingFileNames, the bulk import, the span canonicaliser) and the independent reference of the
/// entry-point spelling. Every non-ASCII character is built from UTF-16 code units, so no non-ASCII character appears in any test source.
/// </summary>
public static class SymbolSpellingCorpus
{
    /// <summary>A string of the given UTF-16 code units (a lone surrogate is allowed).</summary>
    public static string Units(params int[] codeUnits) => new(codeUnits.Select(u => (char)u).ToArray());

    private static string Emoji => char.ConvertFromUtf32(0x1F600);

    private static readonly Regex FourAsciiDigits = new("^[0-9]{4}$", RegexOptions.Compiled);

    /// <summary>
    /// The entry-point spelling as it was written before the consolidation (an independent copy for the equivalence proof): empty for null or white space; trim and upper-case
    /// (invariant); a four-ASCII-digit code gets "-T"; then every '.' becomes '-'.
    /// </summary>
    public static string ReferenceNormalizeTicker(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string raw = input.Trim().ToUpperInvariant();
        if (FourAsciiDigits.IsMatch(raw)) raw += "-T";
        return raw.Replace('.', '-');
    }

    /// <summary>The enumerated rows (null included). Rows with blanks matter for the trimming functions; the import splits on them.</summary>
    public static IReadOnlyList<string?> EnumeratedRows { get; } = BuildRows();

    /// <summary>xUnit member data: one row per corpus entry.</summary>
    public static IEnumerable<object?[]> Rows() => EnumeratedRows.Select(r => new object?[] { r });

    /// <summary>The 40-character alphabet of the two-character sweep.</summary>
    public static IReadOnlyList<char> Alphabet { get; } =
        ("aAiIzZtTbBkKsS" + "079" + ".-^= \t" + "FX_/\\1").ToCharArray()
            .Concat(new[] { (char)0x3000, (char)0x00A0, (char)0x0130, (char)0x0131, (char)0x212A, (char)0x017F, (char)0xFF21, (char)0xFF0E, (char)0xFF17, (char)0xD83D, (char)0xDE00 })
            .ToArray();

    /// <summary>Every string of one or two characters over <see cref="Alphabet"/>.</summary>
    public static IEnumerable<string> AlphabetStrings()
    {
        foreach (char a in Alphabet)
        {
            yield return a.ToString();
            foreach (char b in Alphabet) yield return string.Concat(a, b);
        }
    }

    private static List<string?> BuildRows()
    {
        var rows = new List<string?>
        {
            // plain
            "AAPL", "aapl", "A", "1", "BRK-B", "BRK.B", "brk.b", "^GSPC", "^gspc", "7203-T", "7203-t", "7203.T", "7203.t", "MSFT", "msft ", " msft",
            // four-digit codes (half-width), with and without blanks, and near misses
            "7203", " 7203 ", "7203 ", " 7203", "\t7203\t", "0000", "9999", "123", "12345", "7203A", "A7203", "720", "72031", "7203-", "-7203",
            // four digits of other scripts (full-width, Arabic-Indic, Devanagari)
            Units(0xFF17, 0xFF12, 0xFF10, 0xFF13), Units(0x0663, 0x0663, 0x0663, 0x0663), Units(0x0967, 0x0967, 0x0967, 0x0967), "72" + Units(0xFF10, 0xFF13),
            // full-width letters and dot
            Units(0xFF22, 0xFF32, 0xFF2B, 0xFF0E, 0xFF22), Units(0xFF21, 0xFF22), "BRK" + Units(0xFF0E) + "B",
            // non-ASCII blanks
            "A" + Units(0x3000) + "B", Units(0x3000) + "AAPL", "AAPL" + Units(0x00A0), Units(0x00A0) + "AAPL" + Units(0x00A0), Units(0x3000) + "7203" + Units(0x3000),
            // Turkish and special case mappings
            "i", "I", Units(0x0130), Units(0x0131), "A" + Units(0x0130), "A" + Units(0x0131), Units(0x212A), "A" + Units(0x212A), Units(0x017F), "A" + Units(0x017F),
            // surrogates
            Emoji, "A" + Emoji, Emoji + "A", Emoji + Emoji, Units(0xD83D), Units(0xDE00), "A" + Units(0xD83D), Units(0xDE00) + "A",
            // vendor-suffixed
            "CL=F", "cl=f", "GC=F", "HG=F", "JPY=X", "jpy=x", "EURUSD=X", "A=B=F", "=F", "=X", "CL=", "CL==F", "6E=X", "USD-JPY=X",
            // separators, controls and operators
            "A/B", "A\\B", "A:B", "..", "A..B", "A.", ".A", "-", ".", "A+B", "A B", "A_B", "A,B", "A" + Units(0x1F) + "B", Units(0x1F) + "AAPL", "AAPL" + Units(0x1C), "A\tB", "A\nB",
            "CON", "con", "NUL", "COM1", "LPT9", "AUX.T",
            // lengths around the layer bounds (10, 15, 16, 32)
            new string('A', 10), new string('A', 11), new string('A', 15), new string('A', 16), new string('A', 17), new string('A', 32), new string('A', 33),
            new string('1', 4) + new string('A', 12),
            // empty, blank, null
            "", " ", "   ", "\t", null,
        };
        return rows;
    }
}
