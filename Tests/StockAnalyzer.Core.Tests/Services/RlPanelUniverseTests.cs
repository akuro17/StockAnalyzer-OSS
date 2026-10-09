using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Universe validation, split and identity hashes. Literals were computed once with an independent implementation (Python hashlib).</summary>
public sealed class RlPanelUniverseTests
{
    private const string Prefix = "PortfolioUniverse:";

    [Fact]
    public void CanonicalizeTrimsAndSortsByOrdinal()
        => Assert.Equal(new[] { "AAPL", "B", "a1" }, RlPanelUniverse.Canonicalize(new[] { " a1", "B ", "AAPL" }, false, 10).ToArray());

    [Theory]
    [InlineData(new[] { "A", "" }, false, 10)]
    [InlineData(new[] { "A", "  " }, false, 10)]
    [InlineData(new[] { "A", "B/C" }, false, 10)]
    [InlineData(new[] { "A", "B\\C" }, false, 10)]
    [InlineData(new[] { "A", "B:C" }, false, 10)]
    [InlineData(new[] { "A", "CON.DE" }, false, 10)]       // a Windows device name with an extension is still the device
    [InlineData(new[] { "A", "nul" }, false, 10)]
    [InlineData(new[] { "A", "com1" }, false, 10)]
    [InlineData(new[] { "A", "AAA." }, false, 10)]         // Windows drops a trailing period, so the folder would not be the symbol
    [InlineData(new[] { "A", "é" }, false, 10)]      // "e" + combining acute: not NFC, the same symbol as "é" would split into two seeds
    [InlineData(new[] { "AAPL", "aapl" }, false, 10)]
    [InlineData(new[] { "A", "B", " b " }, false, 10)]
    [InlineData(new[] { "A" }, false, 10)]
    [InlineData(new string[0], false, 10)]
    [InlineData(new[] { "A", "B", "C" }, false, 2)]
    [InlineData(new[] { "A", "B", "C" }, true, 10)]
    public void InvalidUniversesAreRejectedWithThePrefix(string[] symbols, bool holdout, int maxSymbols)
    {
        var error = Assert.Throws<ArgumentException>(() => RlPanelUniverse.Canonicalize(symbols, holdout, maxSymbols));
        Assert.StartsWith(Prefix, error.Message);
    }

    [Fact]
    public void BoundaryCountsAreAccepted()
    {
        Assert.Equal(2, RlPanelUniverse.Canonicalize(new[] { "B", "A" }, false, 2).Length);
        Assert.Equal(4, RlPanelUniverse.Canonicalize(new[] { "D", "C", "B", "A" }, true, 4).Length);
        Assert.Throws<ArgumentNullException>(() => RlPanelUniverse.Canonicalize(null!, false, 5));
    }

    [Theory]
    [InlineData(4, 25, 2)]   // ceil(1) = 1, floored to the two-symbol minimum
    [InlineData(8, 25, 2)]   // ceil(2) = 2
    [InlineData(9, 25, 3)]   // ceil(2.25) = 3
    [InlineData(50, 25, 13)] // ceil(12.5) = 13
    [InlineData(10, 50, 5)]
    [InlineData(5, 50, 3)]   // ceil(2.5) = 3, leaving two training symbols
    [InlineData(4, 1, 2)]
    public void HoldoutCountIsCeilingWithAFloorOfTwo(int count, int percent, int expected)
        => Assert.Equal(expected, RlPanelUniverse.HoldoutCount(count, percent));

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-5)]
    public void HoldoutPercentOutsideTheHardBandIsRejected(int percent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelUniverse.HoldoutCount(10, percent));

    [Fact]
    public void SplitMatchesTheHandRankedKeys()
    {
        // sha256("<symbol>|12345") starts with BBB 24bc..., AAA 341e..., DDD 66d6..., CCC dd3c...; two are held out (the two smallest keys).
        var flags = RlPanelUniverse.AssignHoldout(new[] { "AAA", "BBB", "CCC", "DDD" }, 12345u, 25);
        Assert.Equal(new[] { true, true, false, false }, flags.ToArray());
    }

    [Fact]
    public void SplitRecomputedIndependentlyAgreesForManySeeds()
    {
        string[] symbols = Enumerable.Range(0, 13).Select(i => "S" + i.ToString("00")).ToArray();
        foreach (uint seed in new uint[] { 0, 1, 7, 12345, 4_000_000_000 })
        {
            var expected = symbols.OrderBy(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s + "|" + seed))), StringComparer.Ordinal)
                .Take(RlPanelUniverse.HoldoutCount(symbols.Length, 25)).ToHashSet();
            var flags = RlPanelUniverse.AssignHoldout(symbols, seed, 25);
            Assert.Equal(expected, symbols.Where((_, i) => flags[i]).ToHashSet());
        }
    }

    [Fact]
    public void SplitDoesNotDependOnInputOrder()
    {
        string[] symbols = { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF", "GGG", "HHH" };
        var forward = RlPanelUniverse.AssignHoldout(symbols, 99u, 25);
        string[] reversed = symbols.Reverse().ToArray();
        var backward = RlPanelUniverse.AssignHoldout(reversed, 99u, 25);
        Assert.Equal(symbols.Where((_, i) => forward[i]).OrderBy(s => s, StringComparer.Ordinal),
            reversed.Where((_, i) => backward[i]).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void SplitRejectsTooFewSymbolsAndAHoldoutThatStarvesTraining()
    {
        Assert.Throws<ArgumentException>(() => RlPanelUniverse.AssignHoldout(new[] { "A", "B", "C" }, 1u, 25));
        // Every count from 4 to 60 at the largest allowed share still leaves two training symbols.
        for (int n = 4; n <= 60; n++)
        {
            var flags = RlPanelUniverse.AssignHoldout(Enumerable.Range(0, n).Select(i => "S" + i).ToArray(), 5u, 50);
            Assert.True(flags.Count(f => !f) >= 2 && flags.Count(f => f) >= 2);
        }
    }

    [Fact]
    public void MembershipHashMatchesTheIndependentCanonicalJson()
    {
        string json = "{\"schema\":2,\"symbols\":[\"A\",\"B\"],\"source_manifest_sha256\":[\"h1\",\"h2\"]}";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(expected, RlPanelUniverse.MembershipSha256(new[] { "A", "B" }, new[] { "h1", "h2" }));
        Assert.Equal("8966df28377b63c147ebe271c9117100216cbab71bcc89840e46384cbb939557", expected);
    }

    [Fact]
    public void SymbolSetHashMatchesTheIndependentCanonicalJson()
    {
        string json = "{\"schema\":2,\"symbols\":[\"A\",\"B\"]}";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(expected, RlPanelUniverse.SymbolSetSha256(new[] { "A", "B" }));
        Assert.Equal("bf3a369dd3a0bd325422a4eac4dadcfb2f3b2f7e8f4c54480f1a0c01d63674ec", expected);
    }

    [Fact]
    public void SymbolSetHashDependsOnTheSymbolsOnlyAndNotOnTheSourceHashes()
    {
        Assert.NotEqual(RlPanelUniverse.SymbolSetSha256(new[] { "A", "B" }), RlPanelUniverse.SymbolSetSha256(new[] { "A", "C" }));
        Assert.Equal("03eacf691bfe750498018c87fcacbe7310bf21b54be06e50f9f313db80f9fb2b", RlPanelUniverse.SymbolSetSha256(new[] { "A", "C" }));
        Assert.Throws<ArgumentNullException>(() => RlPanelUniverse.SymbolSetSha256(null!));
    }

    [Fact]
    public void TheSplitOfAFixedSymbolListIsPinnedByAnIndependentlyComputedSeed()
    {
        // Seed and held-out names were computed with Python hashlib (sha256 of the canonical JSON, first eight hex digits, ranked keys).
        string[] symbols = { "AAA", "BBB", "CCC", "DDD", "EEE" };
        string hash = RlPanelUniverse.SymbolSetSha256(symbols);
        Assert.Equal("bac4a9059611d686c11ce149bfb3024049764daaabf93b0a95690fa9e03d3c24", hash);
        uint seed = RlPanelUniverse.SplitSeed(hash);
        Assert.Equal(3_133_450_501u, seed);
        var flags = RlPanelUniverse.AssignHoldout(symbols, seed, 25);
        Assert.Equal(new[] { "AAA", "EEE" }, symbols.Where((_, i) => flags[i]).ToArray());
    }

    [Fact]
    public void MembershipChangesWithSymbolsOrSourceHashes()
    {
        string baseline = RlPanelUniverse.MembershipSha256(new[] { "A", "B" }, new[] { "h1", "h2" });
        Assert.NotEqual(baseline, RlPanelUniverse.MembershipSha256(new[] { "A", "C" }, new[] { "h1", "h2" }));
        Assert.NotEqual(baseline, RlPanelUniverse.MembershipSha256(new[] { "A", "B" }, new[] { "h1", "h3" }));
        Assert.Throws<ArgumentException>(() => RlPanelUniverse.MembershipSha256(new[] { "A", "B" }, new[] { "h1" }));
    }

    [Fact]
    public void SeedIsTheFirstEightHexDigits()
    {
        Assert.Equal(0x01234567u, RlPanelUniverse.SplitSeed(string.Concat(Enumerable.Repeat("0123456789abcdef", 4))));
        Assert.Equal(uint.MaxValue, RlPanelUniverse.SplitSeed("ffffffff" + new string('0', 56)));
        Assert.Throws<ArgumentException>(() => RlPanelUniverse.SplitSeed("ABCDEF"));
        Assert.Throws<ArgumentException>(() => RlPanelUniverse.SplitSeed(new string('F', 64)));
    }

    [Fact]
    public void UniverseHashMatchesTheIndependentCanonicalJsonAndCoversTheSplitDefinition()
    {
        string membership = "8966df28377b63c147ebe271c9117100216cbab71bcc89840e46384cbb939557";
        string json = "{\"schema\":2,\"membership_sha256\":\"" + membership + "\",\"split_seed\":123,\"holdout_enabled\":true,\"holdout_percent\":25,\"holdout_symbols\":[\"B\"]}";
        string universe = RlPanelUniverse.UniverseSha256(membership, 123u, true, 25, new[] { "B" });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(), universe);
        Assert.Equal("2963386199ee3359bca69b8df4d4609c1d453a0f138d543b1431969fdc69e849", universe);
        Assert.NotEqual(universe, RlPanelUniverse.UniverseSha256(membership, 123u, true, 30, new[] { "B" }));
        Assert.NotEqual(universe, RlPanelUniverse.UniverseSha256(membership, 123u, false, 25, new[] { "B" }));
        Assert.NotEqual(universe, RlPanelUniverse.UniverseSha256(membership, 124u, true, 25, new[] { "B" }));
        Assert.NotEqual(universe, RlPanelUniverse.UniverseSha256(membership, 123u, true, 25, new[] { "A" }));
    }

    [Fact]
    public void CalendarHashJoinsIsoDatesWithLineFeeds()
    {
        var calendar = new[] { new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        Assert.Equal("1851517315ac89bdefdf2967df413912183803c15417e706a83db7310ea43cf8", RlPanelUniverse.CalendarSha256(calendar));
    }
}
