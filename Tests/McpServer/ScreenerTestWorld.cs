using System.IO;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>A throw-away data tree and a running MCP server over it; optionally the series are given and every close after a date is multiplied by ten.</summary>
internal sealed class ScreenerTestWorld : IDisposable
{
    /// <summary>Bars of the zig-zag tree and of UP / DOWN: one per calendar day from <see cref="FirstDay"/> (the last is 2025-02-03).</summary>
    public const int Days = 400;

    public static readonly DateTime FirstDay = new(2024, 1, 1);

    /// <summary>LATE has bars only from this day on (constant close 250).</summary>
    public static readonly DateTime LateFirstDay = new(2024, 12, 1);

    private const decimal Wild = 10m;

    /// <summary>The tree of the shared fixture: MSFT, AAPL and 7203.T with the zig-zag closes the golden answers were captured on.</summary>
    public static ScreenerTestWorld ZigZag() => new(new[] { "msft", "aapl", "7203.T" }, series: null, alterAfter: null, workspace: null);

    /// <summary>
    /// UP closes 100 + day index, DOWN 600 - day index (400 days from 2024-01-01), LATE a constant 250 from 2024-12-01. With <paramref name="alterAfter"/> every close dated
    /// after it is multiplied by ten in all three.
    /// </summary>
    public static ScreenerTestWorld Trend(DateOnly? alterAfter = null, WorkspaceSettings? workspace = null)
    {
        var series = new Dictionary<string, (DateTime First, List<decimal> Closes)>
        {
            ["UP"] = (FirstDay, Enumerable.Range(0, Days).Select(day => 100m + day).ToList()),
            ["DOWN"] = (FirstDay, Enumerable.Range(0, Days).Select(day => 600m - day).ToList()),
            ["LATE"] = (LateFirstDay, Enumerable.Repeat(250m, (new DateTime(2025, 2, 3) - LateFirstDay).Days + 1).ToList()),
        };
        return new ScreenerTestWorld(new[] { "UP", "DOWN", "LATE" }, series, alterAfter, workspace);
    }

    /// <summary>
    /// The Trend tree plus symbols whose weekly/monthly bars start on another day than UP's: GAPW is UP without Monday 2025-01-06, GAPM is UP without 2024-09-01 (the first
    /// day of September), STALE is UP stored only up to Wednesday 2025-01-22. A weekly/monthly bar is stamped with the FIRST stored day of its period, so these differ from UP's.
    /// </summary>
    public static ScreenerTestWorld TrendWithGaps(DateOnly? alterAfter = null)
    {
        List<decimal> UpCloses(int days) => Enumerable.Range(0, days).Select(day => 100m + day).ToList();
        var series = new Dictionary<string, (DateTime First, List<decimal> Closes)>
        {
            ["UP"] = (FirstDay, UpCloses(Days)),
            ["DOWN"] = (FirstDay, Enumerable.Range(0, Days).Select(day => 600m - day).ToList()),
            ["GAPW"] = (FirstDay, UpCloses(Days)),
            ["GAPM"] = (FirstDay, UpCloses(Days)),
            ["STALE"] = (FirstDay, UpCloses((new DateTime(2025, 1, 22) - FirstDay).Days + 1)),
        };
        var missing = new Dictionary<string, IReadOnlySet<DateOnly>>
        {
            ["GAPW"] = new HashSet<DateOnly> { new(2025, 1, 6) },
            ["GAPM"] = new HashSet<DateOnly> { new(2024, 9, 1) },
        };
        return new ScreenerTestWorld(new[] { "UP", "DOWN", "GAPW", "GAPM", "STALE" }, series, alterAfter, workspace: null, missing);
    }

    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly McpServerRuntime _runtime;
    private readonly ServiceProvider _services;
    private readonly string _templates;

    public ScreenerTestWorld(
        string[] tickers,
        Dictionary<string, (DateTime First, List<decimal> Closes)>? series,
        DateOnly? alterAfter,
        WorkspaceSettings? workspace,
        IReadOnlyDictionary<string, IReadOnlySet<DateOnly>>? missingDays = null)
    {
        _fixture = ReadOnlyDataTreeFixture.Create(series is null ? Days : 1, tickers);
        _templates = Path.Combine(TestDataIsolationBootstrap.NewTempDirectory("McpPointInTimeScreener"), "Screener");
        if (series is not null)
        {
            foreach ((string symbol, (DateTime first, List<decimal> closes)) in series)
            {
                List<decimal> written = closes.Select((close, index) => alterAfter is { } kept && DateOnly.FromDateTime(first.AddDays(index)) > kept ? close * Wild : close).ToList();
                File.Delete(Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet"));
                Seed(_fixture.DailyDirectory, symbol, first, written, missingDays is not null && missingDays.TryGetValue(symbol, out IReadOnlySet<DateOnly>? skipped) ? skipped : null);
            }
        }

        string workspacePath = Path.Combine(_fixture.Root, "Config", LayoutConstants.DefaultWorkspaceFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
        if (workspace is not null)
        {
            new WorkspaceSerializationService().SaveAsync(workspace, workspacePath).GetAwaiter().GetResult();
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        _runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()), _templates, workspaceSource: new FileReadOnlyWorkspaceSource(workspacePath));
        _runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
        _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
        Gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        DataService = _services.GetRequiredService<IDataService>();
    }

    public IReadOnlyStockAnalyzerGateway Gateway { get; }

    public IDataService DataService { get; }

    /// <summary>Writes a Screener template (a new id) the server can read; returns its id.</summary>
    public Guid SaveTemplate(params ScreenerIndicatorEntry[] entries) => SaveTemplate(Guid.NewGuid(), entries);

    public Guid SaveTemplate(Guid id, params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = id, Name = "PointInTime", Description = "PointInTime" };
        template.SetEntries(entries);
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        return id;
    }

    /// <summary>The structured answer of a tool call, as text; the call must succeed.</summary>
    public async Task<string> CallAsync(string tool, string argumentsJson)
    {
        using JsonDocument response = await CallRawAsync(tool, argumentsJson);
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetRawText();
    }

    public async Task<JsonDocument> CallRawAsync(string tool, string argumentsJson)
        => (await McpTestSupport.CallAsync(_runtime.Dispatcher, tool, argumentsJson))!;

    public async Task<JsonDocument> RequestAsync(string method)
    {
        string? response = await _runtime.Dispatcher.DispatchAsync($$$"""{"jsonrpc":"2.0","id":7,"method":"{{{method}}}","params":{}}""", CancellationToken.None);
        return JsonDocument.Parse(response!);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _services.Dispose();
        _fixture.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_templates)!, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>Writes a daily OHLCV parquet with the given closes, one bar per calendar day from <paramref name="first"/> (open/high/low derived as the shared fixture builder does).</summary>
    private static void Seed(string directory, string ticker, DateTime first, IReadOnlyList<decimal> closes, IReadOnlySet<DateOnly>? skippedDays = null)
    {
        string path = Path.Combine(directory, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        DateTime date = first;
        foreach (decimal close in closes)
        {
            if (skippedDays is null || !skippedDays.Contains(DateOnly.FromDateTime(date)))
            {
                rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {close - 0.2m}, {close + 0.5m}, {close - 0.5m}, {close}, 1000)"));
            }

            date = date.AddDays(1);
        }

        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY (SELECT * FROM (VALUES {string.Join(",", rows)}) AS t(date, open, high, low, close, volume)) TO '{path}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
}
