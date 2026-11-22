using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>The boundary contract of <see cref="TickerSymbol.TryCanonicalizeAscii"/>: success writes the canonical ASCII form; every refusal returns written 0 and leaves the destination untouched.</summary>
public class TickerSymbolCanonicalizeAsciiTests
{
    private const char Sentinel = '#';

    private static char[] Buffer(int length) => Enumerable.Repeat(Sentinel, length).ToArray();

    private static string Convert(string source, int bufferLength, out bool ok, out int written, out char[] buffer)
    {
        buffer = Buffer(bufferLength);
        ok = TickerSymbol.TryCanonicalizeAscii(source, buffer, out written);
        return new string(buffer, 0, ok ? written : 0);
    }

    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData("AAPL", "AAPL")]
    [InlineData("brk.b", "BRK-B")]
    [InlineData("7203.t", "7203-T")]
    [InlineData("7203", "7203")]
    [InlineData("^gspc", "^GSPC")]
    [InlineData("cl=f", "CL=F")]
    [InlineData("a.b.c", "A-B-C")]
    [InlineData("x-y", "X-Y")]
    [InlineData("a b", "A B")]          // blanks and control characters are copied; the format check is the caller's next step
    [InlineData("a_z", "A_Z")]
    [InlineData("`{@[", "`{@[")]        // the characters next to the letter ranges are not letters
    public void TheConversionRule_UpperCasesAsciiLettersAndReplacesDots(string source, string expected)
    {
        string result = Convert(source, source.Length, out bool ok, out int written, out _);

        Assert.True(ok);
        Assert.Equal(expected, result);
        Assert.Equal(source.Length, written);
    }

    [Fact]
    public void EveryAsciiCharacter_IsConvertedOneForOne()
    {
        for (int code = 0; code <= 0x7F; code++)
        {
            char c = (char)code;
            char expected = c == '.' ? '-' : c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

            string result = Convert(c.ToString(), 1, out bool ok, out int written, out _);

            Assert.True(ok, $"U+{code:X4}");
            Assert.Equal(expected.ToString(), result);
            Assert.Equal(1, written);
        }
    }

    [Fact]
    public void EveryNonAsciiCharacter_IsRefused_AndTheDestinationIsUntouched()
    {
        for (int code = 0x80; code <= char.MaxValue; code++)
        {
            string source = "AB" + (char)code;
            string result = Convert(source, source.Length, out bool ok, out int written, out char[] buffer);

            Assert.False(ok, $"U+{code:X4}");
            Assert.Equal(0, written);
            Assert.True(buffer.All(b => b == Sentinel), $"U+{code:X4} modified the destination");
            Assert.Equal(string.Empty, result);
        }
    }

    [Theory]
    [InlineData(0)]   // at the start
    [InlineData(1)]   // in the middle
    [InlineData(2)]   // at the end
    public void ANonAsciiCharacter_AtAnyPosition_RefusesTheWholeSource(int position)
    {
        char[] chars = "abc".ToCharArray();
        chars[position] = (char)0x0130;

        Convert(new string(chars), 3, out bool ok, out int written, out char[] buffer);

        Assert.False(ok);
        Assert.Equal(0, written);
        Assert.All(buffer, b => Assert.Equal(Sentinel, b));
    }

    [Fact]
    public void AnEmptySource_IsRefused_WithWrittenZero()
    {
        string result = Convert(string.Empty, 8, out bool ok, out int written, out char[] buffer);

        Assert.False(ok);
        Assert.Equal(0, written);
        Assert.All(buffer, b => Assert.Equal(Sentinel, b));
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ABufferOneShort_IsRefused_AndTheDestinationIsUntouched()
    {
        Convert("abcd", 3, out bool ok, out int written, out char[] buffer);

        Assert.False(ok);
        Assert.Equal(0, written);
        Assert.All(buffer, b => Assert.Equal(Sentinel, b));
    }

    [Fact]
    public void ABufferOfZeroLength_IsRefused()
    {
        Assert.False(TickerSymbol.TryCanonicalizeAscii("a", Span<char>.Empty, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void ALongerBuffer_ReceivesOnlyTheSourceLength()
    {
        string result = Convert("ab", 5, out bool ok, out int written, out char[] buffer);

        Assert.True(ok);
        Assert.Equal(2, written);
        Assert.Equal("AB", result);
        Assert.Equal(new[] { 'A', 'B', Sentinel, Sentinel, Sentinel }, buffer);
    }

    [Fact]
    public void TheCodeUnitBoundaryOfAscii_Is0x7F()
    {
        Assert.True(TickerSymbol.TryCanonicalizeAscii(((char)0x7F).ToString(), new char[1], out int writtenAscii));
        Assert.Equal(1, writtenAscii);
        Assert.False(TickerSymbol.TryCanonicalizeAscii(((char)0x80).ToString(), new char[1], out int writtenOther));
        Assert.Equal(0, writtenOther);
    }

    [Fact]
    public void InPlace_TheSameSpanAsSourceAndDestination_IsConvertedCorrectly()
    {
        char[] chars = "brk.b".ToCharArray();

        bool ok = TickerSymbol.TryCanonicalizeAscii(chars, chars, out int written);

        Assert.True(ok);
        Assert.Equal(5, written);
        Assert.Equal("BRK-B", new string(chars));
    }

    [Fact]
    public void InPlace_ARefusalLeavesTheBufferUnchanged()
    {
        char[] chars = ("abc.d" + (char)0x017F).ToCharArray();
        string before = new(chars);

        bool ok = TickerSymbol.TryCanonicalizeAscii(chars, chars, out int written);

        Assert.False(ok);
        Assert.Equal(0, written);
        Assert.Equal(before, new string(chars));
    }

    // Equals Canonicalize on every non-empty ASCII string without leading or trailing white space (the stated equivalence).
    private static bool NeedsNoTrimming(string s) => s.Length > 0 && s.All(char.IsAscii) && s == s.Trim();

    [Fact]
    public void EqualsCanonicalize_OnTheAsciiCorpus_TheAsciiCharacterSweep_AndTheAlphabetStrings()
    {
        var inputs = SymbolSpellingCorpus.EnumeratedRows.Where(r => r is not null).Select(r => r!)
            .Concat(SymbolSpellingCorpus.AlphabetStrings())
            .Concat(Enumerable.Range(0, 0x80).SelectMany(code => new[] { "A" + (char)code + "B", ((char)code).ToString() }))
            .Where(NeedsNoTrimming)
            .Distinct()
            .ToList();

        Assert.True(inputs.Count > 300);
        var mismatches = new List<string>();
        foreach (string input in inputs)
        {
            string result = Convert(input, input.Length, out bool ok, out _, out _);
            if (!ok || result != TickerSymbol.Canonicalize(input)) mismatches.Add(string.Join(' ', input.Select(ch => ((int)ch).ToString("X4"))));
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void ACorpusRowWithAnyNonAsciiCharacter_IsRefused()
    {
        var rows = SymbolSpellingCorpus.EnumeratedRows.Where(r => !string.IsNullOrEmpty(r) && r.Any(c => !char.IsAscii(c))).Select(r => r!).ToList();

        Assert.NotEmpty(rows);
        foreach (string row in rows)
        {
            Assert.False(TickerSymbol.TryCanonicalizeAscii(row, new char[row.Length], out int written), row);
            Assert.Equal(0, written);
        }
    }
}
