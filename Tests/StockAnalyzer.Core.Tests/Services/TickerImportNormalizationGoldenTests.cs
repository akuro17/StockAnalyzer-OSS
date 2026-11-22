using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// What the bulk import makes of ONE token, over the shared corpus. The expected value is an independent model composed of public pieces (ASCII-only guard, upper-case,
/// '.' to '-', the four-digit suffix, <see cref="TickerSymbol.IsAcceptedSymbolFormat"/>, the import bound); literal rows pin the owner-visible results. They must not change when the
/// per-character loop of the import is replaced by a shared span function.
/// </summary>
public class TickerImportNormalizationGoldenTests
{
    private static readonly char[] SplitCharacters = { ',', '\t', ' ', '\r', '\n' };

    private static async Task<string[]> ImportAsync(string token)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));
        return (await new TickerImportService().ImportTickersAsync(stream)).ToArray();
    }

    /// <summary>The expected import result of one line that holds no delimiter after trimming (the importer trims every line with the Unicode white-space rule): one symbol or none.</summary>
    private static string[] Model(string line)
    {
        string token = line.Trim();
        if (token.Length == 0 || token.Length > TickerSymbolLimits.ImportMaxLength) return System.Array.Empty<string>();
        if (token.Any(c => !char.IsAscii(c))) return System.Array.Empty<string>();
        string normalized = new(token.Select(c => char.ToUpperInvariant(c) == '.' ? '-' : char.ToUpperInvariant(c)).ToArray());
        if (TickerHelper.IsFourDigitJapaneseCode(normalized)) return new[] { normalized + "-T" };
        return TickerSymbol.IsAcceptedSymbolFormat(normalized, TickerSymbolLimits.ImportMaxLength) ? new[] { normalized } : System.Array.Empty<string>();
    }

    public static IEnumerable<object?[]> Tokens() => SymbolSpellingCorpus.EnumeratedRows
        .Where(r => !string.IsNullOrWhiteSpace(r) && r.Trim().IndexOfAny(SplitCharacters) < 0)
        .Select(r => new object?[] { r });

    [Theory]
    [MemberData(nameof(Tokens))]
    public async Task EveryDelimiterFreeRow_GivesTheModelResult(string token)
    {
        Assert.Equal(Model(token), await ImportAsync(token));
    }

    [Theory]
    [InlineData("7203", "7203-T")]
    [InlineData("7203.t", "7203-T")]
    [InlineData("7203-T", "7203-T")]
    [InlineData("brk.b", "BRK-B")]
    [InlineData("cl=f", "CL=F")]
    [InlineData("JPY=X", "JPY=X")]
    [InlineData("aapl", "AAPL")]
    [InlineData("12345", "12345")]
    public async Task KnownResults(string token, string expected)
    {
        Assert.Equal(new[] { expected }, await ImportAsync(token));
    }

    [Theory]
    [InlineData("A_B")]
    [InlineData("A/B")]
    [InlineData("CL=")]
    [InlineData("=F")]
    [InlineData("A=B=F")]
    [InlineData("^gspc")]   // an index symbol is not importable: '^' is not part of the plain character class
    public async Task KnownRefusals(string token)
    {
        Assert.Empty(await ImportAsync(token));
    }

    [Fact]
    public async Task EveryAsciiCharacterBetweenTwoLetters_GivesTheModelResult()
    {
        var mismatches = new List<string>();
        for (int code = 0; code < 128; code++)
        {
            char c = (char)code;
            if (SplitCharacters.Contains(c)) continue;
            string token = "A" + c + "B";
            var expected = Model(token);
            var actual = await ImportAsync(token);
            if (!expected.SequenceEqual(actual)) mismatches.Add($"U+{code:X4}");
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task EveryNonAsciiCharacter_IsRefusedAlone_AndNextToALetter()
    {
        var accepted = new List<string>();
        for (int code = 128; code <= char.MaxValue; code++)
        {
            char c = (char)code;
            if (char.IsWhiteSpace(c)) continue; // the reader and the tokeniser treat some of these as line or token ends; covered by the enumerated rows
            foreach (string token in new[] { c.ToString(), "A" + c })
            {
                if ((await ImportAsync(token)).Length > 0) accepted.Add($"U+{code:X4}");
            }
        }

        Assert.Empty(accepted);
    }
}
