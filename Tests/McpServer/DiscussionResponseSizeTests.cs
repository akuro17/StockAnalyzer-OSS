using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.CommandLine;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T10: the size of a full <c>sa_discussion_read</c> page as the client receives it (the executor's envelope holds the JSON twice and escapes the text block once more),
/// against <c>MaxResponseBytes</c>. The figures go to the test output and to <c>SA_AI_INTEGRATION.md</c> (block 0018, <c>measurements</c>).
/// </summary>
public sealed class DiscussionResponseSizeTests : IDisposable
{
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "sa_discussion_size_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly ITestOutputHelper _output;
    private int _requestId = 100;

    public DiscussionResponseSizeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        if (Directory.Exists(_emptyRoot))
        {
            Directory.Delete(_emptyRoot, recursive: true);
        }
    }

    private async Task<string> CallRawAsync(McpServerRuntime runtime, string tool, string argumentsJson)
        => (await runtime.Dispatcher.DispatchAsync(McpTestSupport.CallMessage(Interlocked.Increment(ref _requestId), tool, argumentsJson), CancellationToken.None))!;

    [Theory]
    [InlineData("ascii", 50, false)]
    [InlineData("japanese", 50, false)]
    [InlineData("emoji", 20, false)]
    [InlineData("emoji", 50, true)]
    public async Task AFullPageIsMeasuredAsTheClientReceivesItAndOnlyEmojiOnlyMessagesOfAFullPageExceedTheLimit(string kind, int limit, bool exceeds)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxPostsPerMinute", 120)), new LogSink(new StringWriter()), enableDiscussion: true);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        string unit = kind switch { "ascii" => "a", "japanese" => "あ", _ => "😀" };
        string content = string.Concat(Enumerable.Repeat(unit, 16 * 1024 / Encoding.UTF8.GetByteCount(unit)));
        using JsonDocument created = JsonDocument.Parse(await CallRawAsync(runtime, "sa_discussion_create", """{"title":"size"}"""));
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument joined = JsonDocument.Parse(await CallRawAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"writer"}"""));
        string token = McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;
        for (int i = 0; i < limit; i++)
        {
            string posted = await CallRawAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"{{content}}"}""");
            Assert.DoesNotContain("\"isError\":true", posted, StringComparison.Ordinal);
        }

        string line = await CallRawAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","afterSequence":0,"limit":{{limit}}}""");
        using JsonDocument response = JsonDocument.Parse(line);

        int bytes = Encoding.UTF8.GetByteCount(line);
        _output.WriteLine($"{kind} x {limit}: payload {limit * 16} KiB; response line {bytes / 1024} KiB of {runtime.Limits.MaxResponseBytes / 1024} KiB; error = {McpTestSupport.IsError(response)}");
        Assert.Equal(exceeds, McpTestSupport.IsError(response));
        if (exceeds)
        {
            Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        }
        else
        {
            Assert.True(bytes <= runtime.Limits.MaxResponseBytes);
        }
    }

    // ---- Run A of the discussion optimization: legacy form against compact + minimal header (two-stage acceptance, spec section 5A) -----------

    // Stage 2: the sizes of the new form as measured by this test (the printed "[size]" lines), and the exact size of the unchanged legacy form.
    // An answer of the new form may grow at most 10 % over its recorded size; the legacy form must not change by a single byte.
    private const int F1LegacyBytes = 1404;
    private const int F1NewBytes = 618;
    private const int F2LegacyBytes = 22444;
    private const int F2NewBytes = 15880;

    // F3 (Run B): the answer of get_analysis with three fields against the whole result of a real attached tool. The legacy size follows the tool registry (it grows with every
    // registered indicator), so only the new size is recorded and the legacy size is compared by ratio.
    private const int F3NewBytes = 2334;

    // F4 (Run C): the 20-message page of F2 (20 posts of 200 characters) read as a newest-messages summary, against the legacy page.
    private const int F4LegacyBytes = 22444;
    private const int F4NewBytes = 13406;

    private async Task<(string SessionId, string Alice, string Bob, McpServerRuntime Runtime)> OpenFixtureAsync()
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot, ("Discussion:MaxPostsPerMinute", 120)), new LogSink(new StringWriter()), enableDiscussion: true);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument created = JsonDocument.Parse(await CallRawAsync(runtime, "sa_discussion_create", """{"title":"size-fixture"}"""));
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        string alice = await JoinFixtureAsync(runtime, sessionId, "Alice");
        string bob = await JoinFixtureAsync(runtime, sessionId, "Bob");
        return (sessionId, alice, bob, runtime);
    }

    private async Task<string> JoinFixtureAsync(McpServerRuntime runtime, string sessionId, string name)
    {
        using JsonDocument joined = JsonDocument.Parse(await CallRawAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"{{name}}"}"""));
        return McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;
    }

    private void AssertTwoStage(string fixture, int legacy, int fresh, int recordedLegacy, int recordedNew)
    {
        _output.WriteLine($"[size] {fixture} legacy={legacy} new={fresh} ratio={((double)fresh / legacy).ToString("F3", CultureInfo.InvariantCulture)}");
        Assert.True(fresh < legacy, $"{fixture}: the new form ({fresh} bytes) must be smaller than the legacy form ({legacy} bytes).");
        if (recordedLegacy >= 0)
        {
            Assert.Equal(recordedLegacy, legacy);
            Assert.True(fresh <= recordedNew + (recordedNew / 10), $"{fixture}: {fresh} bytes exceeds the recorded {recordedNew} bytes by more than 10 %.");
        }
    }

    [Fact]
    public async Task F1_ATimedOutReadIsSmallerWithCompactAndAMinimalHeader()
    {
        (string sessionId, string alice, _, McpServerRuntime runtime) = await OpenFixtureAsync();
        string common = $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","afterSequence":2,"waitSeconds":1""";

        string legacy = await CallRawAsync(runtime, "sa_discussion_read", common + "}");
        string fresh = await CallRawAsync(runtime, "sa_discussion_read", common + ""","compact":true,"headerMode":"minimal"}""");

        Assert.Contains("\"timedOut\":true", legacy.Replace("\\\"", "\""), StringComparison.Ordinal);
        AssertTwoStage("F1", Encoding.UTF8.GetByteCount(legacy), Encoding.UTF8.GetByteCount(fresh), F1LegacyBytes, F1NewBytes);
    }

    [Fact]
    public async Task F2_ATwentyMessagePageIsSmallerWithCompactAndAMinimalHeader()
    {
        (string sessionId, string alice, string bob, McpServerRuntime runtime) = await OpenFixtureAsync();
        string content = new('a', 200);
        for (int i = 0; i < 20; i++)
        {
            string posted = await CallRawAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","content":"{{content}}"}""");
            Assert.DoesNotContain("\"isError\":true", posted, StringComparison.Ordinal);
        }

        string common = $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}","afterSequence":0,"limit":20""";
        string legacy = await CallRawAsync(runtime, "sa_discussion_read", common + "}");
        string fresh = await CallRawAsync(runtime, "sa_discussion_read", common + ""","compact":true,"headerMode":"minimal"}""");

        AssertTwoStage("F2", Encoding.UTF8.GetByteCount(legacy), Encoding.UTF8.GetByteCount(fresh), F2LegacyBytes, F2NewBytes);
    }

    [Fact]
    public async Task F3_AnAnalysisAnswerWithThreeFieldsIsMuchSmallerThanTheWholeResult()
    {
        (string sessionId, string alice, _, McpServerRuntime runtime) = await OpenFixtureAsync();
        string attached = await CallRawAsync(runtime, "sa_discussion_attach_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","tool":"sa_get_capabilities"}""");
        Assert.DoesNotContain("\"isError\":true", attached, StringComparison.Ordinal);

        string common = $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","contextId":1""";
        string legacy = await CallRawAsync(runtime, "sa_discussion_get_analysis", common + "}");
        string fresh = await CallRawAsync(runtime, "sa_discussion_get_analysis", common + ""","paths":["periodUnit","supportedTimeFrames[0]","compactDefaults"]}""");

        int legacyBytes = Encoding.UTF8.GetByteCount(legacy);
        int freshBytes = Encoding.UTF8.GetByteCount(fresh);
        _output.WriteLine($"[size] F3 legacy={legacyBytes} new={freshBytes} ratio={((double)freshBytes / legacyBytes).ToString("F4", CultureInfo.InvariantCulture)}");
        Assert.True(freshBytes * 10 < legacyBytes, $"F3: {freshBytes} bytes is not below a tenth of the whole result ({legacyBytes} bytes).");
        if (F3NewBytes >= 0)
        {
            Assert.True(freshBytes <= F3NewBytes + (F3NewBytes / 10), $"F3: {freshBytes} bytes exceeds the recorded {F3NewBytes} bytes by more than 10 %.");
        }
    }

    [Fact]
    public async Task F4_ASummaryOfTheNewestMessagesIsSmallerThanTheLegacyPage()
    {
        (string sessionId, string alice, string bob, McpServerRuntime runtime) = await OpenFixtureAsync();
        string content = new('a', 200);
        for (int i = 0; i < 20; i++)
        {
            string posted = await CallRawAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","content":"{{content}}"}""");
            Assert.DoesNotContain("\"isError\":true", posted, StringComparison.Ordinal);
        }

        string common = $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}",""";
        string legacy = await CallRawAsync(runtime, "sa_discussion_read", common + "\"afterSequence\":0,\"limit\":20}");
        string fresh = await CallRawAsync(runtime, "sa_discussion_read", common + "\"tail\":20,\"excludeEvents\":true,\"view\":\"summary\",\"compact\":true,\"headerMode\":\"minimal\"}");

        AssertTwoStage("F4", Encoding.UTF8.GetByteCount(legacy), Encoding.UTF8.GetByteCount(fresh), F4LegacyBytes, F4NewBytes);
    }
}
