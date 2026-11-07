#nullable enable
using System;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T4, spec 5.10.1: the canonical form of the arguments of an analysis snapshot (the reuse key and the stored arguments).</summary>
public sealed class DiscussionCanonicalJsonTests
{
    private static string Canon(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return DiscussionCanonicalJson.Canonicalize(document.RootElement);
    }

    [Fact]
    public void KeysAreSortedByOrdinalOrderAtEveryDepthAndWhiteSpaceIsRemoved()
    {
        Assert.Equal("""{"a":1,"b":{"x":true,"y":[3,2,1]},"c":null}""", Canon("""{ "c": null, "b": { "y": [3, 2, 1], "x": true }, "a": 1 }"""));
        Assert.Equal(Canon("""{"a":1,"b":2}"""), Canon("""{"b":2,"a":1}"""));
        Assert.Equal(Canon("""{"a":1,"b":2}"""), Canon("{\n  \"a\" : 1 ,\r\n\t\"b\" : 2\n}"));
    }

    [Fact]
    public void OrdinalOrderPutsUpperCaseBeforeLowerCaseAndNeverUsesTheCulture()
    {
        Assert.Equal("""{"B":1,"Z":2,"a":3,"b":4}""", Canon("""{"b":4,"a":3,"Z":2,"B":1}"""));
        Assert.Equal("""{"a":1,"ab":2,"b":3}""", Canon("""{"b":3,"ab":2,"a":1}"""));
    }

    [Fact]
    public void ArrayOrderIsKeptAndObjectsInsideArraysAreSortedToo()
    {
        Assert.Equal("""{"items":[{"a":1,"b":2},{"a":3}],"z":[2,1]}""", Canon("""{"z":[2,1],"items":[{"b":2,"a":1},{"a":3}]}"""));
    }

    [Fact]
    public void NumbersKeepTheirOriginalTextSoLargeAndPreciseValuesSurviveByteExact()
    {
        string canonical = Canon("""{"big":12345678901234567890.123456789,"exp":1E+5,"neg":-0.50,"zero":0}""");

        Assert.Equal("""{"big":12345678901234567890.123456789,"exp":1E+5,"neg":-0.50,"zero":0}""", canonical);
    }

    [Fact]
    public void OneAndOnePointZeroAreDifferentKeys()
    {
        Assert.NotEqual(Canon("""{"n":1}"""), Canon("""{"n":1.0}"""));
    }

    [Fact]
    public void AnEscapedAndALiteralCharacterGiveTheSameBytes()
    {
        Assert.Equal(Canon("""{"k":"A"}"""), Canon("""{"k":"A"}"""));
        Assert.Equal(Canon("""{"a":1}"""), Canon("""{"a":1}"""));
        Assert.Equal("{\"k\":\"日本\"}", Canon("""{"k":"日本"}"""));
    }

    [Fact]
    public void ControlCharactersInStringsStayEscapedSoTheTextIsOneLine()
    {
        string canonical = Canon("{\"k\":\"line\\nbreak\\ttab\"}");

        Assert.DoesNotContain('\n', canonical);
        Assert.Equal("""{"k":"line\nbreak\ttab"}""", canonical);
    }

    [Fact]
    public void CanonicalizationIsIdempotent()
    {
        string once = Canon("""{"b":[{"d":1,"c":2}],"a":"x y","e":1.50}""");

        Assert.Equal(once, Canon(once));
    }

    [Fact]
    public void ADuplicateKeyIsRefusedEvenWhenOnlyTheEscapedSpellingsMatch()
    {
        DiscussionException same = Assert.Throws<DiscussionException>(() => Canon("""{"a":1,"a":2}"""));
        DiscussionException escaped = Assert.Throws<DiscussionException>(() => Canon("""{"a":1,"a":2}"""));
        DiscussionException nested = Assert.Throws<DiscussionException>(() => Canon("""{"o":{"k":1,"k":1}}"""));

        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "arguments", "duplicate key"), (same.Code, same.Parameter, same.Hint));
        Assert.Equal("duplicate key", escaped.Hint);
        Assert.Equal("duplicate key", nested.Hint);
    }

    [Fact]
    public void OnlyAnObjectIsAccepted()
    {
        foreach (string json in new[] { "[1]", "5", "\"text\"", "null", "true" })
        {
            using JsonDocument document = JsonDocument.Parse(json);
            DiscussionException error = Assert.Throws<DiscussionException>(() => DiscussionCanonicalJson.Canonicalize(document.RootElement));
            Assert.Equal((DiscussionErrorCodes.InvalidArgument, "arguments"), (error.Code, error.Parameter));
        }
    }

    [Fact]
    public void ThirtyTwoLevelsAreAcceptedAndThirtyThreeAreRefused()
    {
        static string Nested(int levels)
        {
            var text = new StringBuilder();
            for (int i = 1; i < levels; i++)
            {
                text.Append("{\"k\":");
            }

            text.Append("{}");
            text.Append('}', levels - 1);
            return text.ToString();
        }

        Assert.NotEmpty(Canon(Nested(32)));
        DiscussionException error = Assert.Throws<DiscussionException>(() => Canon(Nested(33)));
        Assert.Equal((DiscussionErrorCodes.InvalidArgument, "arguments"), (error.Code, error.Parameter));
        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArraysCountAsLevelsToo()
    {
        string deepArrays = "{\"k\":" + new string('[', 31) + new string(']', 31) + "}";
        string tooDeep = "{\"k\":" + new string('[', 32) + new string(']', 32) + "}";

        Assert.NotEmpty(Canon(deepArrays));
        Assert.Equal("arguments", Assert.Throws<DiscussionException>(() => Canon(tooDeep)).Parameter);
    }

    [Fact]
    public void TheCanonicalTextMayBeAtMost4096BytesAndTheEdgeIsAccepted()
    {
        string edge = "{\"k\":\"" + new string('a', 4096 - 8) + "\"}";
        string over = "{\"k\":\"" + new string('a', 4096 - 7) + "\"}";

        Assert.Equal(4096, Encoding.UTF8.GetByteCount(Canon(edge)));
        DiscussionException error = Assert.Throws<DiscussionException>(() => Canon(over));
        Assert.Equal((DiscussionErrorCodes.LimitExceeded, "arguments"), (error.Code, error.Parameter));
    }

    [Fact]
    public void TheSizeIsMeasuredOnTheCanonicalTextNotOnTheInput()
    {
        string spaced = "{\"k\":\"" + new string('a', 4096 - 8) + "\"" + new string(' ', 500) + "}";

        Assert.Equal(4096, Encoding.UTF8.GetByteCount(Canon(spaced)));
    }
}
