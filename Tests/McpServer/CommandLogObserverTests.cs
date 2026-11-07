using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Task T6 of the discussion board, server side: the executor's observer feeds the Core command log, and nothing but names, sizes and outcomes gets there. The
/// scenario carries a token-like value and a message body in the arguments and proves that neither appears in the ring or in the file (matrix T020).
/// </summary>
public sealed class CommandLogObserverTests : IDisposable
{
    private const string SecretToken = "abcdef.SECRET-TOKEN-0123456789";
    private const string SecretBody = "private market opinion that must never be logged";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dhub-observer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private async Task<(McpMessageDispatcher Dispatcher, CommandLog Log)> StartAsync(IEnumerable<IMcpTool> tools, McpLimits? limits = null)
    {
        var settings = new DiscussionSettings { CommandLogToFile = true };
        var log = new CommandLog(settings, folder: _folder);
        var sink = new LogSink(new StringWriter());
        var executor = new ToolExecutor(limits ?? new McpLimits(), new ErrorMessageSanitizer(Array.Empty<string?>()), sink, new CommandLogObserver(log));
        var dispatcher = new McpMessageDispatcher(sink, new McpToolRegistry(tools), executor);
        await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return (dispatcher, log);
    }

    private static string Schema() => ToolSchema.Object()
        .String("participantToken", "t")
        .String("content", "c")
        .Build();

    [Fact]
    public async Task EveryCallIsLoggedWithNamesSizeAndOutcomeButNeverAValue()
    {
        var ok = new FakeTool("sa_discussion_post", (_, _) => Task.FromResult("{}"), Schema());
        var failing = new FakeTool("sa_discussion_read", (_, _) => throw new ToolException(ToolErrorCodes.LimitExceeded, "too big"), Schema());
        (McpMessageDispatcher dispatcher, CommandLog log) = await StartAsync(new IMcpTool[] { ok, failing });
        using (log)
        {
            string arguments = $$"""{"participantToken":"{{SecretToken}}","content":"{{SecretBody}}"}""";

            (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_post", arguments, id: 7))!.Dispose();
            (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_read", arguments, id: 8))!.Dispose();
            (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_post", """{"unknown":"x"}""", id: 9))!.Dispose(); // refused by the schema

            IReadOnlyList<CommandLogEntry> entries = log.Tail(10);
            Assert.Equal(new[] { "sa_discussion_post", "sa_discussion_read", "sa_discussion_post" }, entries.Select(e => e.Tool));
            Assert.Equal(new[] { ToolCallOutcomes.Ok, ToolErrorCodes.LimitExceeded, ToolErrorCodes.InvalidArgument }, entries.Select(e => e.Outcome));
            Assert.Equal(new[] { "7", "8", "9" }, entries.Select(e => e.RequestId));
            Assert.Equal(new[] { "participantToken", "content" }, entries[0].ArgKeys);
            Assert.Equal(Encoding.UTF8.GetByteCount(arguments), entries[0].ArgBytes);
            Assert.All(entries, e => Assert.True(e.Milliseconds >= 0));

            string ring = string.Join("\n", entries.Select(e => e.ToString()));
            string file = ReadWhileOpen(Assert.Single(Directory.GetFiles(_folder)));
            foreach (string text in new[] { ring, file })
            {
                Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
                Assert.DoesNotContain("SECRET", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(SecretBody, text, StringComparison.Ordinal);
                Assert.DoesNotContain("private market", text, StringComparison.Ordinal);
            }

            Assert.Equal(3, file.TrimEnd('\n').Split('\n').Length);
        }
    }

    [Fact]
    public async Task AnObserverThatCannotWriteItsFileDoesNotChangeTheToolResult()
    {
        File.WriteAllText(_folder, "a file where the command folder should be");
        try
        {
            var ok = new FakeTool("sa_discussion_post", (_, _) => Task.FromResult("""{"value":1}"""), Schema());
            (McpMessageDispatcher dispatcher, CommandLog log) = await StartAsync(new IMcpTool[] { ok });
            using (log)
            {
                using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_post", """{"content":"x"}"""))!;

                Assert.False(McpTestSupport.IsError(response));
                Assert.Equal(1, McpTestSupport.Structured(response).GetProperty("value").GetInt32());
                Assert.Single(log.Tail(5));
                Assert.False(log.FileModeActive);
            }
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}
