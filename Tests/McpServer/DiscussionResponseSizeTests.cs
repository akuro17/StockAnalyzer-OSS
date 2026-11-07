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
}
