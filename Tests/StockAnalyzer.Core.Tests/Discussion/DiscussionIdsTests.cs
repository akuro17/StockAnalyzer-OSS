#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T1 of the discussion board: the shapes of the epoch tag, the session id and the participant token (spec 5.1), and the redraw on a collision.</summary>
public sealed class DiscussionIdsTests
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A random source that hands out a fixed script of bytes, so a draw is exactly predictable.</summary>
    private static Action<byte[]> Script(params byte[] bytes)
    {
        int position = 0;
        return buffer =>
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = bytes[position++ % bytes.Length];
            }
        };
    }

    [Fact]
    public void AnEpochIsSixBase32CharactersAndEveryByteMapsByItsLowFiveBits()
    {
        Assert.Equal("abcdef", new DiscussionIds(Script(0, 1, 2, 3, 4, 5)).NewEpoch());
        Assert.Equal("567abc", new DiscussionIds(Script(29, 30, 31, 32, 33, 34)).NewEpoch());

        string live = new DiscussionIds().NewEpoch();
        Assert.Equal(6, live.Length);
        Assert.All(live, character => Assert.Contains(character, Alphabet));
    }

    [Fact]
    public void ASessionIdIsTheLetterSFollowedBySixBase32Characters()
    {
        string id = new DiscussionIds().NewSessionId(_ => false);

        Assert.Equal(7, id.Length);
        Assert.Equal('S', id[0]);
        Assert.All(id.Skip(1), character => Assert.Contains(character, Alphabet));
        Assert.Equal("Sbcdefg", new DiscussionIds(Script(1, 2, 3, 4, 5, 6)).NewSessionId(_ => false));
    }

    [Fact]
    public void ATakenSessionIdIsRedrawnUntilAFreeOneIsFound()
    {
        // First draw "Saaaaaa" (taken), second "Sbbbbbb" (taken), third "Scccccc" (free).
        var ids = new DiscussionIds(Script(0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2));
        var taken = new HashSet<string> { "Saaaaaa", "Sbbbbbb" };

        string id = ids.NewSessionId(taken.Contains);

        Assert.Equal("Scccccc", id);
    }

    [Fact]
    public void AnEndlessCollisionStopsAfterTheDrawAttemptsInsteadOfLoopingForever()
    {
        int draws = 0;
        var ids = new DiscussionIds(Script(7));

        Assert.Throws<InvalidOperationException>(() => ids.NewSessionId(_ => { draws++; return true; }));

        Assert.Equal(DiscussionLimits.IdDrawAttempts, draws);
    }

    [Fact]
    public void TenThousandSessionIdsDrawnAgainstTheLiveSetAreAllDistinct()
    {
        var ids = new DiscussionIds();
        var live = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 10_000; i++)
        {
            string id = ids.NewSessionId(live.Contains);
            Assert.True(live.Add(id));
        }

        Assert.Equal(10_000, live.Count);
    }

    [Fact]
    public void ATokenIsTheEpochADotAndTwentyTwoBase64UrlCharactersOfSixteenRandomBytes()
    {
        string token = new DiscussionIds(Script(255, 254, 253, 252, 251, 250, 249, 248, 247, 246, 245, 244, 243, 242, 241, 240)).NewToken("abcdef");

        Assert.Equal(29, token.Length);
        Assert.StartsWith("abcdef.", token, StringComparison.Ordinal);
        string body = token[7..];
        Assert.Equal(22, body.Length);
        Assert.All(body, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        // 0xFF 0xFE 0xFD in Base64 is "//79", which the URL-safe form writes as "__79".
        Assert.StartsWith("__79", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTokensOfTheSameEpochDifferAndTheEpochCanBeReadBack()
    {
        var ids = new DiscussionIds();

        string first = ids.NewToken("abcdef");
        string second = ids.NewToken("abcdef");

        Assert.NotEqual(first, second);
        Assert.True(DiscussionIds.TryGetEpoch(first, out string epoch));
        Assert.Equal("abcdef", epoch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcde")]
    [InlineData("abcdefg.tail")]
    [InlineData("abcdef")]
    [InlineData("no-dot-in-this-text")]
    public void AnEpochIsNotReadFromTextWithoutADotAtTheSixthPosition(string? text)
        => Assert.False(DiscussionIds.TryGetEpoch(text, out _));

    [Fact]
    public void OnlyTheSha256OfATokenIsKeptAndEqualHashesCompareEqual()
    {
        string token = new DiscussionIds().NewToken("abcdef");

        byte[] hash = DiscussionIds.HashToken(token);
        byte[] again = DiscussionIds.HashToken(token);
        byte[] other = DiscussionIds.HashToken(token + "x");

        Assert.Equal(32, hash.Length);
        Assert.True(DiscussionIds.HashesEqual(hash, again));
        Assert.False(DiscussionIds.HashesEqual(hash, other));
        Assert.False(DiscussionIds.HashesEqual(hash, hash[..16]));
        // SHA-256 of the UTF-8 bytes of "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Convert.ToHexString(DiscussionIds.HashToken("abc")).ToLowerInvariant());
    }
}
