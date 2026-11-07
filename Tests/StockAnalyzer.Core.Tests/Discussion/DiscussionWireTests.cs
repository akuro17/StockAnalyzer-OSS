#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T7: the lines of the pipe (spec 5.12) and the status composed from its two parts.</summary>
public sealed class DiscussionWireTests
{
    private static MemoryStream Stream(string text) => new(new UTF8Encoding(false).GetBytes(text));

    private static MemoryStream Stream(byte[] bytes) => new(bytes);

    // ---- Request and reply lines ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ARequestTravelsAsOneLineWithTheArgumentsAsAJsonValue()
    {
        byte[] bytes = DiscussionWire.EncodeRequest(new DiscussionRequest("post", "Antigravity", """{"sessionId":"abc","content":"こんにちは \"x\" 😀"}"""));

        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(1, bytes.Count(b => b == (byte)'\n'));
        using JsonDocument line = JsonDocument.Parse(bytes);
        Assert.Equal(1, line.RootElement.GetProperty("wire").GetInt32());
        Assert.Equal("post", line.RootElement.GetProperty("op").GetString());
        Assert.Equal("Antigravity", line.RootElement.GetProperty("caller").GetString());
        Assert.Equal(JsonValueKind.Object, line.RootElement.GetProperty("args").ValueKind);
    }

    [Fact]
    public void ARequestIsReadBackWithTheArgumentsUnchangedAndWithoutEscapingNonAsciiText()
    {
        const string Arguments = """{"sessionId":"abc","content":"こんにちは \"x\" 😀 line1\nline2"}""";
        byte[] bytes = DiscussionWire.EncodeRequest(new DiscussionRequest("post", "VS Code", Arguments));

        // Relaxed escaping: UTF-8 text stays UTF-8 text, so the line is no longer than the message.
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("こんにちは", text, StringComparison.Ordinal);
        Assert.Contains("😀", text, StringComparison.Ordinal);

        DiscussionWire.WireRequest read = DiscussionWire.ParseRequest(bytes[..^1]);
        Assert.Equal("post", read.Operation);
        Assert.Equal("VS Code", read.Caller);
        using JsonDocument original = JsonDocument.Parse(Arguments);
        using JsonDocument roundTrip = JsonDocument.Parse(read.ArgumentsJson);
        Assert.Equal(original.RootElement.GetProperty("content").GetString(), roundTrip.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void ABlankArgumentsTextIsAnEmptyObjectAndATextThatIsNotJsonIsRefused()
    {
        using JsonDocument blank = JsonDocument.Parse(DiscussionWire.EncodeRequest(new DiscussionRequest("status", "x", " ")));
        Assert.Equal(JsonValueKind.Object, blank.RootElement.GetProperty("args").ValueKind);

        Assert.ThrowsAny<Exception>(() => DiscussionWire.EncodeRequest(new DiscussionRequest("status", "x", "{not json")));
    }

    [Fact]
    public void AnOkReplyAndAFailureAreReadBackAsTheyWereWritten()
    {
        DiscussionReply ok = DiscussionWire.ParseReply(DiscussionWire.EncodeReply(DiscussionReply.Ok("""{"sequence":7,"big":12345678901234567890}""")));
        DiscussionReply failure = DiscussionWire.ParseReply(DiscussionWire.EncodeReply(
            DiscussionReply.Fail(DiscussionErrorCodes.RateLimited, "Too many posts.", "content", "Wait 12 seconds.")));

        Assert.True(ok.IsOk);
        Assert.Equal("""{"sequence":7,"big":12345678901234567890}""", ok.ResultJson); // numbers keep their text
        Assert.False(failure.IsOk);
        Assert.Equal(new DiscussionFailure(DiscussionErrorCodes.RateLimited, "Too many posts.", "content", "Wait 12 seconds."), failure.Failure);
    }

    [Fact]
    public void AFailureWithoutParameterAndHintKeepsThemNull()
    {
        DiscussionReply failure = DiscussionWire.ParseReply(DiscussionWire.EncodeReply(DiscussionReply.Fail(DiscussionErrorCodes.HubReset, "Restarted.")));

        Assert.Null(failure.Failure!.Parameter);
        Assert.Null(failure.Failure.Hint);
    }

    // ---- Refused lines ----------------------------------------------------------------------------------------------------------------

    private static DiscussionWireProblem ProblemOf(Action action) => Assert.Throws<DiscussionWireException>(action).Problem;

    [Theory]
    [InlineData("{not json", "Malformed")]
    [InlineData("[1,2]", "Malformed")]
    [InlineData("""{"op":"read","args":{}}""", "Malformed")]
    [InlineData("""{"wire":"1","op":"read","args":{}}""", "Malformed")]
    [InlineData("""{"wire":2,"op":"read","args":{}}""", "VersionMismatch")]
    [InlineData("""{"wire":1,"args":{}}""", "Malformed")]
    [InlineData("""{"wire":1,"op":"read"}""", "Malformed")]
    [InlineData("""{"wire":1,"op":"read","args":[]}""", "Malformed")]
    public void ABadRequestLineIsRefusedWithItsKind(string line, string expected)
    {
        Assert.Equal(Enum.Parse<DiscussionWireProblem>(expected), ProblemOf(() => DiscussionWire.ParseRequest(Encoding.UTF8.GetBytes(line))));
    }

    [Theory]
    [InlineData("""{"wire":1,"ok":true}""", "Malformed")]
    [InlineData("""{"wire":1,"ok":true,"result":[]}""", "Malformed")]
    [InlineData("""{"wire":1,"ok":false}""", "Malformed")]
    [InlineData("""{"wire":1,"ok":false,"error":{"code":"X"}}""", "Malformed")]
    [InlineData("""{"wire":1,"ok":"yes","result":{}}""", "Malformed")]
    [InlineData("""{"wire":9,"ok":true,"result":{}}""", "VersionMismatch")]
    [InlineData("not json at all", "Malformed")]
    public void ABadReplyLineIsRefusedWithItsKind(string line, string expected)
    {
        Assert.Equal(Enum.Parse<DiscussionWireProblem>(expected), ProblemOf(() => DiscussionWire.ParseReply(Encoding.UTF8.GetBytes(line))));
    }

    // ---- Reading a line ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALineEndsAtTheFirstLineFeedAndWhatFollowsIsLeftAlone()
    {
        using MemoryStream stream = Stream("first\nsecond\n");

        byte[]? line = await DiscussionWire.ReadLineAsync(stream, 100, CancellationToken.None);

        Assert.Equal("first", Encoding.UTF8.GetString(line!));
    }

    [Fact]
    public async Task ALineAtTheLimitIsAcceptedAndOneByteMoreIsTooLong()
    {
        using MemoryStream exact = Stream(new string('a', 20000) + "\n");
        using MemoryStream over = Stream(new string('a', 20001) + "\n");
        using MemoryStream noFeed = Stream(new string('a', 20001));

        byte[]? line = await DiscussionWire.ReadLineAsync(exact, 20000, CancellationToken.None);
        var tooLong = await Assert.ThrowsAsync<DiscussionWireException>(() => DiscussionWire.ReadLineAsync(over, 20000, CancellationToken.None));
        var tooLongWithoutFeed = await Assert.ThrowsAsync<DiscussionWireException>(() => DiscussionWire.ReadLineAsync(noFeed, 20000, CancellationToken.None));

        Assert.Equal(20000, line!.Length);
        Assert.Equal(DiscussionWireProblem.TooLong, tooLong.Problem);
        Assert.Equal(DiscussionWireProblem.TooLong, tooLongWithoutFeed.Problem);
    }

    [Fact]
    public async Task AnEmptyPipeIsNullAndAPipeThatEndsInTheMiddleOfALineIsTruncated()
    {
        using MemoryStream empty = Stream(string.Empty);
        using MemoryStream half = Stream("no line feed yet");

        Assert.Null(await DiscussionWire.ReadLineAsync(empty, 100, CancellationToken.None));
        var truncated = await Assert.ThrowsAsync<DiscussionWireException>(() => DiscussionWire.ReadLineAsync(half, 100, CancellationToken.None));
        Assert.Equal(DiscussionWireProblem.Truncated, truncated.Problem);
    }

    [Fact]
    public async Task ALineLongerThanOneChunkIsReadWhole()
    {
        string text = new('x', 3 * 8192 + 5);
        using MemoryStream stream = Stream(text + "\n");

        byte[]? line = await DiscussionWire.ReadLineAsync(stream, 1_000_000, CancellationToken.None);

        Assert.Equal(text, Encoding.UTF8.GetString(line!));
    }

    [Fact]
    public async Task ACancelledTokenStopsTheRead()
    {
        using MemoryStream stream = Stream("line\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscussionWire.ReadLineAsync(stream, 100, cancelled.Token));
    }

    // ---- The status from two parts ------------------------------------------------------------------------------------------------

    private const string HubPart = """
        {"hubEpoch":"EPOCH","sessions":[{"id":"abc","title":"日本語 😀"}],"effectiveLimits":{"maxWaitSeconds":20},"audit":[{"n":1,"event":"x"}],"serverTimeUtc":"2026-11-07T03:00:00Z"}
        """;

    private static readonly CommandLogEntry Entry = new(1, new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero), "sa_discussion_post", new[] { "sessionId", "content" }, 120, "ok", 3, "7");

    [Fact]
    public void TheProcessPartIsAddedAsDataAndTheKeysAreInTheOrderOfTheSpecification()
    {
        string composed = DiscussionStatusComposer.Compose(HubPart.Trim(), new DiscussionProcessInfo("remote", true, "1a2b3c4d"), new[] { Entry });

        using JsonDocument status = JsonDocument.Parse(composed);
        Assert.Equal(
            new[] { "hubEpoch", "role", "sharing", "sharingKey", "sessions", "effectiveLimits", "audit", "commands", "serverTimeUtc" },
            status.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("remote", status.RootElement.GetProperty("role").GetString());
        Assert.True(status.RootElement.GetProperty("sharing").GetBoolean());
        Assert.Equal("1a2b3c4d", status.RootElement.GetProperty("sharingKey").GetString());
        Assert.Equal("sa_discussion_post", status.RootElement.GetProperty("commands")[0].GetProperty("tool").GetString());
        Assert.Equal("日本語 😀", status.RootElement.GetProperty("sessions")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void ASingleSessionKeepsItsName()
    {
        string hubPart = """{"hubEpoch":"E","session":{"id":"abc"},"effectiveLimits":{},"audit":[],"serverTimeUtc":"t"}""";

        using JsonDocument status = JsonDocument.Parse(DiscussionStatusComposer.Compose(hubPart, DiscussionProcessInfo.LocalOnly, Array.Empty<CommandLogEntry>()));

        Assert.True(status.RootElement.TryGetProperty("session", out _));
        Assert.False(status.RootElement.TryGetProperty("sessions", out _));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("sharingKey").ValueKind);
        Assert.False(status.RootElement.GetProperty("sharing").GetBoolean());
        Assert.Equal("hub", status.RootElement.GetProperty("role").GetString());
        Assert.Empty(status.RootElement.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public void NonAsciiTextOfTheBoardKeepsItsSizeInTheComposedStatus()
    {
        string composed = DiscussionStatusComposer.Compose(HubPart.Trim(), DiscussionProcessInfo.LocalOnly, Array.Empty<CommandLogEntry>());

        // Japanese text is written as it is (the relaxed encoder), not as \uXXXX; characters outside the basic plane are escaped by .NET in every case and read back the same.
        Assert.Contains("日本語", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void AHubPartWithoutARequiredValueIsAnErrorNotASilentGap()
    {
        Assert.Throws<InvalidOperationException>(() => DiscussionStatusComposer.Compose("""{"hubEpoch":"E"}""", DiscussionProcessInfo.LocalOnly, Array.Empty<CommandLogEntry>()));
    }

    [Fact]
    public void OnlyTheOperationsOfTheBoardTravelAndOnlyTheSafeOnesAreRepeated()
    {
        foreach (string operation in new[] { "create", "join", "leave", "post", "read", "status_hub", "close", "attach_precheck", "attach_commit", "get_analysis" })
        {
            Assert.True(DiscussionOperations.IsWireOperation(operation), operation);
        }

        Assert.False(DiscussionOperations.IsWireOperation("status"));
        Assert.False(DiscussionOperations.IsWireOperation("purge"));

        var repeatable = new List<string>();
        foreach (string operation in new[] { "create", "join", "leave", "post", "read", "status_hub", "close", "attach_precheck", "attach_commit", "get_analysis" })
        {
            if (DiscussionOperations.IsRepeatable(operation, hasClientRequestId: false))
            {
                repeatable.Add(operation);
            }
        }

        Assert.Equal(new[] { "read", "status_hub", "get_analysis" }, repeatable);
        Assert.True(DiscussionOperations.IsRepeatable("post", hasClientRequestId: true));
        Assert.False(DiscussionOperations.IsRepeatable("join", hasClientRequestId: true));
    }
}
