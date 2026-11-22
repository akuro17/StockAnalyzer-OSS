using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Helpers;

/// <summary>
/// The entry-point spelling (<see cref="TickerHelper.NormalizeTicker"/>) over the shared corpus: it must equal the independent reference of the pre-consolidation rule
/// (<see cref="SymbolSpellingCorpus.ReferenceNormalizeTicker"/>) on every enumerated row, on every single character and on every one- or two-character string of the
/// representative alphabet, and it must equal "canonical form plus the Japanese suffix". These tests are the proof of "no observable change" for the consolidation.
/// </summary>
public class NormalizeTickerEquivalenceTests
{
    public static IEnumerable<object?[]> Rows() => SymbolSpellingCorpus.Rows();

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryEnumeratedRow_EqualsTheReference(string? input)
    {
        Assert.Equal(SymbolSpellingCorpus.ReferenceNormalizeTicker(input), TickerHelper.NormalizeTicker(input!));
    }

    // Known answers, independent of the reference (the owner-visible spellings).
    [Theory]
    [InlineData("7203", "7203-T")]
    [InlineData(" 7203 ", "7203-T")]
    [InlineData("7203.T", "7203-T")]
    [InlineData("7203.t", "7203-T")]
    [InlineData("7203-T", "7203-T")]
    [InlineData("brk.b", "BRK-B")]
    [InlineData("^gspc", "^GSPC")]
    [InlineData("cl=f", "CL=F")]
    [InlineData("jpy=x", "JPY=X")]
    [InlineData("12345", "12345")]
    [InlineData("123", "123")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void KnownAnswers(string input, string expected)
    {
        Assert.Equal(expected, TickerHelper.NormalizeTicker(input));
    }

    [Fact]
    public void ANullInputGivesAnEmptyResult()
    {
        Assert.Equal(string.Empty, TickerHelper.NormalizeTicker(null!));
    }

    [Fact]
    public void EveryCharacterAlone_AndNextToALetter_EqualsTheReference()
    {
        var mismatches = new List<string>();
        for (int code = char.MinValue; code <= char.MaxValue; code++)
        {
            char c = (char)code;
            foreach (string input in new[] { c.ToString(), "A" + c, c + "A" })
            {
                if (SymbolSpellingCorpus.ReferenceNormalizeTicker(input) != TickerHelper.NormalizeTicker(input))
                {
                    mismatches.Add($"U+{code:X4} in '{input.Length} chars'");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void EveryShortStringOfTheRepresentativeAlphabet_EqualsTheReference()
    {
        var mismatches = SymbolSpellingCorpus.AlphabetStrings()
            .Where(s => SymbolSpellingCorpus.ReferenceNormalizeTicker(s) != TickerHelper.NormalizeTicker(s))
            .Select(s => string.Join(' ', s.Select(ch => ((int)ch).ToString("X4"))))
            .ToList();

        Assert.Empty(mismatches);
    }

    // The planned shape of the implementation: canonical form (Trim, ToUpperInvariant, '.' to '-') plus the suffix for a four-digit code. Proven here on today's code.
    private static string CanonicalPlusSuffix(string input)
    {
        string canonical = TickerSymbol.Canonicalize(input);
        return TickerHelper.IsFourDigitJapaneseCode(canonical) ? canonical + "-T" : canonical;
    }

    [Fact]
    public void EveryNonBlankRow_EqualsTheCanonicalFormPlusTheSuffix()
    {
        var mismatches = SymbolSpellingCorpus.EnumeratedRows
            .Concat(SymbolSpellingCorpus.AlphabetStrings())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => CanonicalPlusSuffix(s!) != TickerHelper.NormalizeTicker(s!))
            .Select(s => string.Join(' ', s!.Select(ch => ((int)ch).ToString("X4"))))
            .ToList();

        Assert.Empty(mismatches);
    }
}
