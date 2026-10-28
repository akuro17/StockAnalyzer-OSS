using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_check_portfolio_rl_preflight reads the symbols from the configured MarketData folders (not only the fixed Data folders), exactly like the
/// other data tools of the same server.
/// </summary>
public sealed class CheckPortfolioRlPreflightToolSourceFoldersTests : IDisposable
{
    private const int StoredDays = 1_400;
    private const string FirstDay = "2000-01-01";
    private const string EndDateExclusive = "2003-11-01";   // 1,400 days after the first day
    private const string MissingFileStatus = "MissingFile";
    private const string ReadyStatus = "Ready";
    private static readonly string[] Symbols = { "PRA", "PRB" };
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1, Symbols);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SymbolsStoredInTheConfiguredFoldersAreReadNotReportedAsMissingFiles()
    {
        foreach (string symbol in Symbols) McpTestSupport.WriteEquity(_fixture, symbol, FirstDay, StoredDays);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();

        using McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument response = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_check_portfolio_rl_preflight",
            $$"""{"symbols":["PRA","PRB"],"startDate":"{{FirstDay}}","endDateExclusive":"{{EndDateExclusive}}","mode":"ShortHistory"}"""))!;

        Assert.False(McpTestSupport.IsError(response));
        JsonElement details = McpTestSupport.Structured(response).GetProperty("symbolDetails");
        Assert.Equal(Symbols.Length, details.GetArrayLength());
        foreach (JsonElement symbol in details.EnumerateArray())
        {
            string? status = symbol.GetProperty("status").GetString();
            Assert.NotEqual(MissingFileStatus, status);
            Assert.Equal(ReadyStatus, status);
            Assert.Equal(StoredDays, symbol.GetProperty("barsInPeriod").GetInt32());
        }
    }
}
