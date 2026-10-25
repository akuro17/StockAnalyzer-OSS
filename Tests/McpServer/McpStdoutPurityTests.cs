using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Run 9: stdout purity over the whole catalogue, in a real child process. A session calls every tool (successes and refusals), the resources
/// and a hostile symbol, and sends a malformed line and an unknown method; stdout must then hold exactly one JSON-RPC object per request that
/// needs an answer and nothing else (no log text, no BOM, no partial line), while the log goes to stderr. The calls are chosen to give the same
/// protocol shape on any machine: they use a symbol that is not stored, so the result depends on the user's data only for the listings.
/// </summary>
public sealed class McpStdoutPurityTests
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(90);
    private static readonly string[] Tools =
    {
        "sa_list_tickers", "sa_list_ticker_groups", "sa_get_candles", "sa_get_ticker_metadata", "sa_get_capabilities", "sa_get_settings",
        "sa_calculate_indicators", "sa_run_screener", "sa_list_templates", "sa_run_backtest", "sa_calculate_reverse_watch",
    };

    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Indicators = """[{"id":"r","type":"RSI"}]""";

    [Fact]
    public async Task ChildProcess_EveryToolAndResourceAnswersWithOneCleanJsonLineAndTheLogStaysOnStderr()
    {
        var requests = new List<(int Id, string Line)>
        {
            (10, McpTestSupport.CallMessage(10, "sa_list_tickers", """{"limit":3}""")),
            (11, McpTestSupport.CallMessage(11, "sa_get_candles", """{"symbol":"NOSUCH","timeFrame":"D1"}""")),
            (12, McpTestSupport.CallMessage(12, "sa_get_ticker_metadata", """{"symbols":["NOSUCH"]}""")),
            (13, McpTestSupport.CallMessage(13, "sa_get_capabilities", """{"indicatorTypeFilter":["RSI"],"includeScreenerCatalog":true}""")),
            (14, McpTestSupport.CallMessage(14, "sa_get_settings", "{}")),
            (15, McpTestSupport.CallMessage(15, "sa_calculate_indicators", """{"symbol":"NOSUCH","timeFrame":"D1","indicators":""" + Indicators + "}")),
            (16, McpTestSupport.CallMessage(16, "sa_run_screener", """{"templateId":"00000000-0000-0000-0000-000000000000"}""")),
            (17, McpTestSupport.CallMessage(17, "sa_list_templates", "{}")),
            (18, McpTestSupport.CallMessage(18, "sa_run_backtest", """{"symbol":"NOSUCH","conditions":{"entryLong":""" + Entry + "}}")),
            (19, McpTestSupport.CallMessage(19, "sa_calculate_reverse_watch", """{"symbol":"NOSUCH","timeFrame":"D1"}""")),
            (20, McpTestSupport.CallMessage(20, "sa_get_candles", """{"symbol":"..\\..\\Windows","timeFrame":"D1"}""")),
            (21, McpTestSupport.CallMessage(21, "sa_get_candles", """{"symbol":"MSFT","timeFrame":"D1","count":99999}""")),
            (22, McpTestSupport.CallMessage(22, "sa_nothing", "{}")),
            (23, McpTestSupport.CallMessage(23, "sa_list_ticker_groups", "{}")),
            (30, """{"jsonrpc":"2.0","id":30,"method":"resources/list"}"""),
            (31, """{"jsonrpc":"2.0","id":31,"method":"resources/read","params":{"uri":"stockanalyzer://catalog/indicators"}}"""),
            (32, """{"jsonrpc":"2.0","id":32,"method":"resources/read","params":{"uri":"stockanalyzer://catalog/screener"}}"""),
            (33, """{"jsonrpc":"2.0","id":33,"method":"resources/read","params":{"uri":"stockanalyzer://settings/current"}}"""),
            (34, """{"jsonrpc":"2.0","id":34,"method":"resources/read","params":{"uri":"file:///C:/Windows/win.ini"}}"""),
            (40, """{"jsonrpc":"2.0","id":40,"method":"no/such/method"}"""),
            (41, """{"jsonrpc":"2.0","id":41,"method":"tools/list"}"""),
        };
        const int ParseErrorAnswers = 1;
        Assert.All(Tools, tool => Assert.Contains(requests, request => request.Line.Contains($"\"name\":\"{tool}\"", StringComparison.Ordinal)));

        using Process process = McpServerHostTests.StartServer();
        try
        {
            using var cancellation = new CancellationTokenSource(SessionTimeout);
            var stdin = process.StandardInput;
            var stdout = process.StandardOutput;

            await stdin.WriteLineAsync(McpTestSupport.InitializeMessage);
            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await stdin.WriteLineAsync("{not json");
            foreach ((_, string line) in requests)
            {
                await stdin.WriteLineAsync(line);
            }

            int expected = 1 + ParseErrorAnswers + requests.Count; // initialize + the malformed line + every request
            var lines = new List<string>();
            while (lines.Count < expected)
            {
                string? line = await stdout.ReadLineAsync(cancellation.Token);
                Assert.NotNull(line);
                lines.Add(line!);
            }

            stdin.Close();
            Assert.True(process.WaitForExit((int)SessionTimeout.TotalMilliseconds), "the server must exit when stdin closes");
            Assert.Equal(0, process.ExitCode);
            Assert.Null(await stdout.ReadLineAsync(cancellation.Token));

            var ids = new List<int>();
            string[]? listedTools = null;
            foreach (string line in lines)
            {
                Assert.DoesNotContain('\r', line);
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
                Assert.True(root.TryGetProperty("result", out _) ^ root.TryGetProperty("error", out _), "exactly one of result and error: " + line);
                JsonElement id = root.GetProperty("id");
                if (id.ValueKind == JsonValueKind.Number)
                {
                    ids.Add(id.GetInt32());
                    if (id.GetInt32() == 41)
                    {
                        listedTools = root.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
                    }
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, id.ValueKind);
                    Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
                }
            }

            Assert.Equal(Tools.OrderBy(n => n, StringComparer.Ordinal), listedTools!.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { 0 }.Concat(requests.Select(r => r.Id)).OrderBy(i => i), ids.OrderBy(i => i));

            string errorOutput = await process.StandardError.ReadToEndAsync(cancellation.Token);
            Assert.Contains("Started", errorOutput);
            Assert.DoesNotContain(lines, line => line.Contains("Started", StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
