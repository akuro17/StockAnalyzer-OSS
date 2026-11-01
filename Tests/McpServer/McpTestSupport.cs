using System.IO;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.TestSupport;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>A tool whose behaviour a test supplies (a real implementation of the tool interface, not a mock of project data types).</summary>
internal sealed class FakeTool : IMcpTool
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _invoke;

    public FakeTool(string name, Func<JsonElement, CancellationToken, Task<string>> invoke, string? schemaJson = null, bool isHeavy = false, bool isExecution = false)
    {
        Name = name;
        _invoke = invoke;
        InputSchemaJson = schemaJson ?? ToolSchema.Object().Build();
        IsHeavy = isHeavy;
        IsExecution = isExecution;
    }

    public bool IsExecution { get; }

    public string Name { get; }

    public string Description => "Test tool.";

    public string InputSchemaJson { get; }

    public bool IsHeavy { get; }

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => _invoke(arguments, cancellationToken);
}

internal static class McpTestSupport
{
    /// <summary>A configuration whose data folders do not exist (the server then reports an incomplete data folder and serves no data), with optional <c>AiIntegration</c> limit overrides.</summary>
    public static IConfiguration EmptyDataConfiguration(string emptyRoot, params (string Key, int Value)[] limits)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = Path.Combine(emptyRoot, "Daily"),
            ["MarketDataSettings:WeeklyDataPath"] = Path.Combine(emptyRoot, "Weekly"),
            ["MarketDataSettings:MonthlyDataPath"] = Path.Combine(emptyRoot, "Monthly"),
            ["MarketDataSettings:MetadataPath"] = Path.Combine(emptyRoot, "Metadata"),
            ["MarketDataSettings:TickerListPath"] = Path.Combine(emptyRoot, "Tickers.csv"),
        };
        foreach ((string key, int value) in limits)
        {
            values[$"AiIntegration:{key}"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public const string InitializeMessage = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    /// <summary>The MarketData folders of a throw-away data tree as configuration entries, the way appsettings.json names them.</summary>
    public static Dictionary<string, string?> DataTreeConfiguration(ReadOnlyDataTreeFixture fixture) => new()
    {
        ["MarketDataSettings:DailyDataPath"] = fixture.Settings.DailyDataPath,
        ["MarketDataSettings:WeeklyDataPath"] = fixture.Settings.WeeklyDataPath,
        ["MarketDataSettings:MonthlyDataPath"] = fixture.Settings.MonthlyDataPath,
        ["MarketDataSettings:MetadataPath"] = fixture.Settings.MetadataPath,
        ["MarketDataSettings:TickerListPath"] = fixture.Settings.TickerListPath,
    };

    /// <summary>Writes a real parquet file from a DuckDB query (setup only).</summary>
    public static void WriteParquet(string path, string query)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY ({query}) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    /// <summary>The equity files of the layout the RL panels read: flat prices on consecutive days from <paramref name="firstDay"/>, USD, UTC, with its metadata.</summary>
    public static void WriteEquity(ReadOnlyDataTreeFixture fixture, string symbol, string firstDay, int days)
    {
        WriteParquet(Path.Combine(fixture.DailyDirectory, symbol + ".parquet"),
            $"SELECT DATE '{firstDay}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({days})");
        WriteParquet(Path.Combine(fixture.MetadataDirectory, symbol + ".meta.parquet"),
            "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    public static McpMessageDispatcher CreateDispatcher(IEnumerable<IMcpTool> tools, McpLimits? limits = null, IEnumerable<string?>? roots = null, StringWriter? log = null)
    {
        limits ??= new McpLimits();
        var sink = new LogSink(log ?? new StringWriter());
        var executor = new ToolExecutor(limits, new ErrorMessageSanitizer(roots ?? Array.Empty<string?>()), sink);
        return new McpMessageDispatcher(sink, new McpToolRegistry(tools), executor);
    }

    public static async Task<McpMessageDispatcher> InitializedAsync(IEnumerable<IMcpTool> tools, McpLimits? limits = null, IEnumerable<string?>? roots = null)
    {
        McpMessageDispatcher dispatcher = CreateDispatcher(tools, limits, roots);
        await dispatcher.DispatchAsync(InitializeMessage, CancellationToken.None);
        return dispatcher;
    }

    public static string CallMessage(int id, string tool, string argumentsJson = "{}")
        => $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argumentsJson}}}}}""";

    /// <summary>Calls a tool and returns the whole JSON-RPC response; null when the call was cancelled.</summary>
    public static async Task<JsonDocument?> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson = "{}", int id = 1)
    {
        string? response = await dispatcher.DispatchAsync(CallMessage(id, tool, argumentsJson), CancellationToken.None);
        return response is null ? null : JsonDocument.Parse(response);
    }

    public static JsonElement Result(JsonDocument response) => response.RootElement.GetProperty("result");

    public static JsonElement Structured(JsonDocument response) => Result(response).GetProperty("structuredContent");

    public static bool IsError(JsonDocument response) => Result(response).GetProperty("isError").GetBoolean();

    public static string ErrorCodeOf(JsonDocument response) => Structured(response).GetProperty("code").GetString()!;

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 30)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "condition not reached in time");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// A no-op implementation of <see cref="IReadOnlyStockAnalyzerGateway"/> that returns empty/null results or throws for unused members.
/// Can be derived or used directly in MCP tool tests.
/// </summary>
internal class EmptyReadOnlyStockAnalyzerGateway : IReadOnlyStockAnalyzerGateway
{
    public virtual Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public virtual Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());

    public virtual Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());

    public virtual Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<TickerMetadata?>(null);

    public virtual Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<DateTimeOffset?>(null);

    public virtual Task<SymbolCoverage> GetCoverageAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult(new SymbolCoverage(symbol, false, false, null, Array.Empty<TimeFrameCoverage>()));

    public virtual Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
        => Task.FromResult(new Dictionary<string, IIndicatorResult>());

    public virtual ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0)
        => null;

    public virtual Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public virtual Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ScreenerTemplateListing(Array.Empty<ScreenerIndicatorTemplate>(), 0));

    public virtual Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<ScreenerIndicatorTemplate?>(null);

    public virtual Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new BacktestConditionTemplateListing(Array.Empty<BacktestConditionTemplate>(), 0));

    public virtual Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<BacktestConditionTemplate?>(null);

    public virtual Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public virtual Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
