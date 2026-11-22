using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// <c>StockAnalyzer.Python/symbol_contract.json</c> is the shared, machine-readable definition of the file-safety symbol rules; <c>symbol_validator.py</c> reads it
/// (and evaluates its probes with <c>--selfcheck</c>). These tests hold <see cref="TickerSymbol"/> and <see cref="TickerSymbolLimits"/> to that file in both
/// directions for a character between two letters: the bound, the parent segment and every probe are enforced by C# (file to C#), and a sweep over every single
/// character shows that C# refuses exactly the characters that the file lists (both ways). The length bound and the parent segment have one value each and are compared
/// directly. A character at the start or the end is also subject to each language's trim step: the probes cover ordinary white space, and the code points that
/// Python would trim but C# keeps are listed in the file and checked below.
/// </summary>
public sealed class TickerSymbolContractParityTests
{
    private static string ContractFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "StockAnalyzer.Python", "symbol_contract.json");
    }

    private static JsonElement Contract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractFile()));
        return document.RootElement.Clone();
    }

    [Fact]
    public void TheFileSafetyBoundEqualsTheSharedDefinition()
    {
        int shared = Contract().GetProperty("max_file_safety_length").GetInt32();

        Assert.Equal(shared, TickerSymbolLimits.FileSafetyMaxLength);
        Assert.Equal(shared, TickerSymbol.MaxLength);
    }

    [Fact]
    public void TheParentSegmentIsRefusedLikeTheSharedDefinition()
    {
        string segment = Contract().GetProperty("parent_segment").GetString()!;

        Assert.False(TickerSymbol.IsWellFormed("A" + segment + "B"));
        Assert.False(TickerSymbol.IsWellFormed(segment));
    }

    [Fact]
    public void EverySingleCharacterIsJudgedLikeTheSharedDefinition()
    {
        var contract = Contract();
        var invalid = contract.GetProperty("invalid_characters").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var ranges = contract.GetProperty("control_code_points").EnumerateArray()
            .Select(r => (First: r.GetProperty("first").GetInt32(), Last: r.GetProperty("last").GetInt32())).ToArray();

        Assert.NotEmpty(invalid); // the sweep must not pass vacuously
        Assert.NotEmpty(ranges);

        var mismatches = new List<string>();
        for (int code = char.MinValue; code <= char.MaxValue; code++)
        {
            char c = (char)code;
            bool refusedByTheFile = invalid.Contains(c.ToString()) || ranges.Any(r => code >= r.First && code <= r.Last);
            bool refusedByCSharp = !TickerSymbol.IsWellFormed("A" + c + "B");
            if (refusedByTheFile != refusedByCSharp) mismatches.Add($"U+{code:X4}: file refuses {refusedByTheFile}, C# refuses {refusedByCSharp}");
        }

        Assert.Empty(mismatches);
    }

    // At the edges the language's trim step applies first. C# Trim() keeps U+001C..U+001F (control characters that are not white space), so C# refuses them; the
    // shared file lists them as not_trimmed_code_points so that symbol_validator.py keeps them too (Python str.strip() alone would remove them and accept the symbol).
    [Fact]
    public void EveryNotTrimmedCodePointOfTheSharedDefinitionIsRefusedAtTheEdges()
    {
        var refusedWrongly = new List<string>();
        int checkedCodePoints = 0;
        foreach (var range in Contract().GetProperty("not_trimmed_code_points").EnumerateArray())
        {
            for (int code = range.GetProperty("first").GetInt32(); code <= range.GetProperty("last").GetInt32(); code++)
            {
                checkedCodePoints++;
                string edge = ((char)code).ToString();
                if (TickerSymbol.IsWellFormed(edge + "AAPL") || TickerSymbol.IsWellFormed("AAPL" + edge)) refusedWrongly.Add($"U+{code:X4}");
            }
        }

        Assert.True(checkedCodePoints > 0);
        Assert.Empty(refusedWrongly); // the listed code points that C# would accept at an edge (its Trim() removed them)
    }

    [Fact]
    public void EveryProbeOfTheSharedDefinitionGivesTheSameAnswerInCSharp()
    {
        var mismatches = new List<string>();
        int probes = 0;
        foreach (var probe in Contract().GetProperty("probes").EnumerateArray())
        {
            probes++;
            string symbol = probe.GetProperty("symbol").GetString()!;
            bool expected = probe.GetProperty("well_formed").GetBoolean();
            if (TickerSymbol.IsWellFormed(symbol) != expected) mismatches.Add($"{symbol.Length} chars '{symbol.Trim()}' expected {expected}");
        }

        Assert.True(probes > 0);
        Assert.Empty(mismatches);
    }
}
