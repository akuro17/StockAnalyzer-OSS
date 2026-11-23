using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Parity tests holding C# <see cref="CandleInvariant"/> and Python <c>update_daily.check_ohlc_invariants</c>
/// to the shared fixture <c>StockAnalyzer.Python/candle_invariant_contract.json</c>.
/// </summary>
public sealed class CandleInvariantParityTests
{
    private static string ContractFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "StockAnalyzer.Python", "candle_invariant_contract.json");
    }

    private static JsonElement Contract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractFile()));
        return document.RootElement.Clone();
    }

    [Fact]
    public void ViolationEnumOrderMatchesSharedContract()
    {
        string[] expectedOrder = Contract()
            .GetProperty("violation_order")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        string[] actualOrder = Enum.GetNames<CandleViolationCode>();
        Assert.Equal(expectedOrder, actualOrder);
    }

    [Fact]
    public void EveryContractCaseMatchesCandleInvariantDescribeAndIsValidBar()
    {
        foreach (JsonElement testCase in Contract().GetProperty("cases").EnumerateArray())
        {
            string name = testCase.GetProperty("name").GetString()!;
            DateTime date = DateTime.ParseExact(testCase.GetProperty("date").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            decimal open = testCase.GetProperty("open").GetDecimal();
            decimal high = testCase.GetProperty("high").GetDecimal();
            decimal low = testCase.GetProperty("low").GetDecimal();
            decimal close = testCase.GetProperty("close").GetDecimal();
            long volume = testCase.GetProperty("volume").GetInt64();
            string[] expectedCodes = testCase.GetProperty("expected_codes").EnumerateArray().Select(e => e.GetString()!).ToArray();

            CandleData bar = CandleData.CreateUnchecked(date, open, high, low, close, volume);
            IReadOnlyList<CandleViolationCode> actual = CandleInvariant.Describe(bar);
            string[] actualCodes = actual.Select(c => c.ToString()).ToArray();

            Assert.True(expectedCodes.SequenceEqual(actualCodes), $"Mismatch on contract case '{name}': expected [{string.Join(", ", expectedCodes)}], got [{string.Join(", ", actualCodes)}].");
            Assert.Equal(expectedCodes.Length == 0, CandleInvariant.IsValidBar(bar));
        }
    }
}
