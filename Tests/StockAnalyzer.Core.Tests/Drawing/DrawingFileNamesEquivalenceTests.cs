using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>
/// The drawing file name is built from the entry-point spelling, so a changed spelling would orphan saved drawings. These tests hold <see cref="DrawingFileNames.TryCreate"/> to the
/// independent reference of the spelling (<see cref="SymbolSpellingCorpus.ReferenceNormalizeTicker"/>) over the shared corpus and pin literal file names.
/// </summary>
public class DrawingFileNamesEquivalenceTests
{
    public static IEnumerable<object?[]> Rows() => SymbolSpellingCorpus.Rows();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string? Check(string? ticker)
    {
        var refusal = DrawingFileNames.TryCreate(ticker, TimeframeType.Daily, out string normalized, out string fileName);
        string reference = SymbolSpellingCorpus.ReferenceNormalizeTicker(ticker);
        switch (refusal)
        {
            case DrawingFileNameRefusal.Empty:
                return string.IsNullOrWhiteSpace(ticker) && normalized.Length == 0 && fileName.Length == 0 ? null : "Empty for a non-blank ticker";
            case DrawingFileNameRefusal.SyntheticExpression:
                return !string.IsNullOrWhiteSpace(ticker) && SyntheticSymbolParser.IsSyntheticExpression(ticker) ? null : "Synthetic without a synthetic expression";
            case DrawingFileNameRefusal.InvalidCharacters:
                return reference.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reference.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? null : "InvalidCharacters for a valid reference";
            case DrawingFileNameRefusal.ReservedDeviceName:
                return Reserved.Contains(reference) ? null : "ReservedDeviceName for a non-reserved reference";
            default:
                if (string.IsNullOrWhiteSpace(ticker)) return "None for a blank ticker";
                if (SyntheticSymbolParser.IsSyntheticExpression(ticker)) return "None for a synthetic expression";
                if (reference.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reference.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "None for invalid characters";
                if (Reserved.Contains(reference)) return "None for a reserved name";
                return normalized == reference && fileName == reference + ".Daily.json" ? null : $"name '{normalized}' / '{fileName}' differs from the reference";
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryEnumeratedRow_IsJudgedLikeTheReference(string? ticker)
    {
        Assert.Null(Check(ticker));
    }

    [Theory]
    [InlineData("7203", "7203-T.Daily.json")]
    [InlineData("7203.T", "7203-T.Daily.json")]
    [InlineData("7203-T", "7203-T.Daily.json")]
    [InlineData(" 7203 ", "7203-T.Daily.json")]
    [InlineData("brk.b", "BRK-B.Daily.json")]
    [InlineData("BRK-A", "BRK-A.Daily.json")]
    [InlineData("AAPL", "AAPL.Daily.json")]
    [InlineData("aapl", "AAPL.Daily.json")]
    [InlineData("^gspc", "^GSPC.Daily.json")]
    [InlineData("cl=f", "CL=F.Daily.json")]
    [InlineData("JPY=X", "JPY=X.Daily.json")]
    public void KnownFileNames(string ticker, string expectedFileName)
    {
        var refusal = DrawingFileNames.TryCreate(ticker, TimeframeType.Daily, out _, out string fileName);

        Assert.Equal(DrawingFileNameRefusal.None, refusal);
        Assert.Equal(expectedFileName, fileName);
    }

    [Theory]
    [InlineData("", DrawingFileNameRefusal.Empty)]
    [InlineData("   ", DrawingFileNameRefusal.Empty)]
    [InlineData("CON", DrawingFileNameRefusal.ReservedDeviceName)]
    [InlineData("nul", DrawingFileNameRefusal.ReservedDeviceName)]
    [InlineData("A:B", DrawingFileNameRefusal.InvalidCharacters)]
    public void KnownRefusals(string ticker, DrawingFileNameRefusal expected)
    {
        Assert.Equal(expected, DrawingFileNames.TryCreate(ticker, TimeframeType.Daily, out _, out _));
    }

    [Fact]
    public void EveryCharacterAlone_AndNextToALetter_IsJudgedLikeTheReference()
    {
        var failures = new List<string>();
        for (int code = char.MinValue; code <= char.MaxValue; code++)
        {
            char c = (char)code;
            foreach (string input in new[] { c.ToString(), "A" + c, c + "A" })
            {
                string? problem = Check(input);
                if (problem is not null) failures.Add($"U+{code:X4}: {problem}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void EveryShortStringOfTheRepresentativeAlphabet_IsJudgedLikeTheReference()
    {
        var failures = SymbolSpellingCorpus.AlphabetStrings()
            .Select(s => (Text: s, Problem: Check(s)))
            .Where(r => r.Problem is not null)
            .Select(r => $"{string.Join(' ', r.Text.Select(ch => ((int)ch).ToString("X4")))}: {r.Problem}")
            .ToList();

        Assert.Empty(failures);
    }
}
