namespace StockAnalyzer.Core.Models;

/// <summary>
/// Single definition of the canonical ticker-symbol form and of the well-formedness check applied to
/// symbols that come from outside the process (for example an AI client). Canonical form:
/// <c>Trim().ToUpperInvariant()</c> with '.' replaced by '-' (the data files are named <c>2681-T.parquet</c>).
/// </summary>
public static class TickerSymbol
{
    /// <summary>Longest accepted external symbol, in characters (safety bound; real tickers are far shorter).</summary>
    public const int MaxLength = TickerSymbolLimits.FileSafetyMaxLength;

    private const string ParentSegment = "..";
    private const char DriveSeparator = ':';

    /// <summary>Returns the canonical form of <paramref name="symbol"/>. No validation is performed.</summary>
    public static string Canonicalize(string symbol) => symbol.Trim().ToUpperInvariant().Replace('.', '-');

    /// <summary>
    /// True when <paramref name="symbol"/> is safe to map to a file name: non-empty after trimming, at most
    /// <see cref="MaxLength"/> characters, and free of path separators, drive separators, parent-directory
    /// segments and control characters.
    /// </summary>
    /// <remarks>
    /// File-safety check only (pinned cross-language against <c>StockAnalyzer.Python/symbol_contract.json</c>).
    /// Callers validating accepted ticker syntax MUST use <see cref="IsAcceptedSymbolFormat"/> instead,
    /// and callers enforcing OS-safe plain file stems MUST use <see cref="AttestedFileNames"/>.
    /// </remarks>
    public static bool IsWellFormed(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        string trimmed = symbol.Trim();
        if (trimmed.Length > MaxLength || trimmed.Contains(ParentSegment, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c == '/' || c == '\\' || c == DriveSeparator || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Vendor separator between the root and the class suffix of a symbol such as <c>CL=F</c>.</summary>
    internal const char VendorSuffixSeparator = '=';

    /// <summary>
    /// The one class suffix of vendor continuous futures. <see cref="IsContinuousFuturesFormat"/> is futures-only by contract:
    /// another vendor class (for example currency pairs) is never added here; it gets its own function and a wrapper that combines them.
    /// </summary>
    internal const char ContinuousFuturesSuffix = 'F';

    // Separator and suffix.
    private const int ContinuousFuturesTailLength = 2;

    /// <summary>
    /// True when <paramref name="value"/> is exactly <c>&lt;ROOT&gt;=F</c>: ROOT is one or more ASCII letters or digits, the suffix is
    /// <see cref="ContinuousFuturesSuffix"/> (ASCII case-insensitive), and the total length is at most <paramref name="maxLength"/>.
    /// Format check only: nothing is canonicalised and <see cref="IsWellFormed"/> is not consulted.
    /// </summary>
    public static bool IsContinuousFuturesFormat(ReadOnlySpan<char> value, int maxLength)
    {
        const int minLength = ContinuousFuturesTailLength + 1;
        if (maxLength < minLength || value.Length < minLength || value.Length > maxLength)
        {
            return false;
        }

        int separatorIndex = value.Length - ContinuousFuturesTailLength;
        if (value[separatorIndex] != VendorSuffixSeparator || char.ToUpperInvariant(value[^1]) != ContinuousFuturesSuffix)
        {
            return false;
        }

        for (int i = 0; i < separatorIndex; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The class suffix of vendor currency pairs (<c>EURUSD=X</c>, <c>JPY=X</c>).</summary>
    internal const char ForexPairSuffix = 'X';

    // Separator and suffix (the same tail shape as the futures form; kept separate so the futures function stays byte-identical).
    private const int ForexPairTailLength = 2;

    /// <summary>
    /// True when <paramref name="value"/> is exactly <c>&lt;ROOT&gt;=X</c>: ROOT is one or more ASCII LETTERS (no digits, unlike the futures form),
    /// the suffix is <see cref="ForexPairSuffix"/> (ASCII case-insensitive), and the total length is at most <paramref name="maxLength"/>.
    /// The ROOT length is deliberately not specified (no official provider rule exists). Format check only, like <see cref="IsContinuousFuturesFormat"/>.
    /// </summary>
    public static bool IsForexPairFormat(ReadOnlySpan<char> value, int maxLength)
    {
        const int minLength = ForexPairTailLength + 1;
        if (maxLength < minLength || value.Length < minLength || value.Length > maxLength)
        {
            return false;
        }

        int separatorIndex = value.Length - ForexPairTailLength;
        if (value[separatorIndex] != VendorSuffixSeparator || char.ToUpperInvariant(value[^1]) != ForexPairSuffix)
        {
            return false;
        }

        for (int i = 0; i < separatorIndex; i++)
        {
            if (!char.IsAsciiLetter(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for every vendor-suffixed symbol the application accepts besides the plain character-class symbols: a continuous future
    /// (<see cref="IsContinuousFuturesFormat"/>) or a currency pair (<see cref="IsForexPairFormat"/>). Callers use this one function; a further vendor
    /// class is added here as its own function plus one more alternative.
    /// </summary>
    public static bool IsVendorSuffixedSymbolFormat(ReadOnlySpan<char> value, int maxLength) =>
        IsContinuousFuturesFormat(value, maxLength) || IsForexPairFormat(value, maxLength);

    // The two punctuation characters of the plain form (a share class such as BRK-B, a market suffix such as 7203.T before canonicalisation).
    private const char PlainSymbolHyphen = '-';
    private const char PlainSymbolDot = '.';

    /// <summary>
    /// True when <paramref name="value"/> is a plain symbol: 1 to <paramref name="maxLength"/> characters, each an ASCII letter, an ASCII digit, '-' or '.'.
    /// This is the one character class of the plain form; no case folding or normalisation is applied (so non-ASCII look-alikes are never accepted).
    /// </summary>
    public static bool IsPlainSymbolFormat(ReadOnlySpan<char> value, int maxLength)
    {
        if (value.IsEmpty || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != PlainSymbolHyphen && c != PlainSymbolDot)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The format check that the chart and the bulk import apply: a plain symbol (<see cref="IsPlainSymbolFormat"/>) or a vendor-suffixed one
    /// (<see cref="IsVendorSuffixedSymbolFormat"/>), both within <paramref name="maxLength"/> characters. Pass the bound of the calling layer from <see cref="TickerSymbolLimits"/>.
    /// </summary>
    public static bool IsAcceptedSymbolFormat(ReadOnlySpan<char> value, int maxLength) =>
        IsPlainSymbolFormat(value, maxLength) || IsVendorSuffixedSymbolFormat(value, maxLength);

    // Distance between an ASCII lower-case letter and its upper-case letter ('a' - 'A').
    private const int AsciiCaseOffset = 'a' - 'A';

    /// <summary>
    /// The allocation-free form of <see cref="Canonicalize"/> for an ASCII token (the bulk import writes into a stack buffer). Contract:
    /// <list type="bullet">
    /// <item>Success (<c>true</c>): <paramref name="source"/> is not empty, <paramref name="destination"/> is at least as long, and every character of <paramref name="source"/> is ASCII (U+0000 to U+007F).
    /// The first <c>source.Length</c> characters of <paramref name="destination"/> are written one for one: '.' becomes '-', ASCII 'a' to 'z' become 'A' to 'Z', every other ASCII character is copied unchanged.
    /// <paramref name="written"/> is <c>source.Length</c>.</item>
    /// <item>Refusal (<c>false</c>): an empty source, a destination shorter than the source, or a non-ASCII character. <paramref name="written"/> is 0 and <paramref name="destination"/> is NOT modified
    /// (the whole source is validated before the first write), so an in-place call never leaves a half-converted buffer.</item>
    /// <item>No trimming and no format check (<see cref="IsAcceptedSymbolFormat"/> is the caller's next step). Case mapping is ASCII arithmetic, never culture or Unicode case mapping.</item>
    /// <item><paramref name="source"/> and <paramref name="destination"/> may be the same span (in-place); partially overlapping spans are not supported.</item>
    /// </list>
    /// Equals <see cref="Canonicalize"/> on every non-empty ASCII string that has no leading or trailing white space.
    /// </summary>
    public static bool TryCanonicalizeAscii(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        written = 0;
        if (source.IsEmpty || destination.Length < source.Length)
        {
            return false;
        }

        for (int i = 0; i < source.Length; i++)
        {
            if (!char.IsAscii(source[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            destination[i] = c == PlainSymbolDot ? PlainSymbolHyphen : char.IsAsciiLetterLower(c) ? (char)(c - AsciiCaseOffset) : c;
        }

        written = source.Length;
        return true;
    }
}
