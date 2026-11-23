using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunFProjectionEmptyReasonTests : IDisposable
{
    private const int Days = 60;
    private const string SineSymbol = "PROJ_OK";
    private const string ZeroCloseSymbol = "PROJ_ZERO";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "proj_ok", "proj_zero");
    private readonly List<McpServerRuntime> _runtimes = new();

    public RunFProjectionEmptyReasonTests()
    {
        List<decimal> closes = Enumerable.Range(0, Days)
            .Select(i => 100m + (i * 0.5m) + (decimal)(Math.Sin(2.0 * Math.PI * i / 12.0) * 5.0))
            .ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, SineSymbol, closes);

        var rows = new List<string>(20);
        var start = new DateOnly(2024, 1, 1);
        for (int i = 0; i < 20; i++)
        {
            DateOnly day = start.AddDays(i);
            rows.Add(FormattableString.Invariant(
                $"(DATE '{day:yyyy-MM-dd}', CAST(100.0 AS DOUBLE), CAST(105.0 AS DOUBLE), CAST(0.0 AS DOUBLE), CAST(0.0 AS DOUBLE), CAST(1000 AS BIGINT))"));
        }

        string query = $"""
            SELECT * FROM (VALUES
                {string.Join(",\n                ", rows)}
            ) AS t(date, open, high, low, close, volume)
            """;
        McpTestSupport.WriteParquet(Path.Combine(_fixture.DailyDirectory, ZeroCloseSymbol + ".parquet"), query);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartMcpAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture))
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    [Theory]
    [InlineData("Arima", """{"symbol":"PROJ_OK","timeFrame":"D1","method":"Arima","lookbackBars":2,"arimaD":2}""")]
    [InlineData("Fft", """{"symbol":"PROJ_OK","timeFrame":"D1","method":"Fft","lookbackBars":3}""")]
    [InlineData("Ssa", """{"symbol":"PROJ_OK","timeFrame":"D1","method":"Ssa","lookbackBars":3}""")]
    [InlineData("Hmm", """{"symbol":"PROJ_OK","timeFrame":"D1","method":"Hmm","lookbackBars":9}""")]
    public async Task EmptyProjectionBelowMinimumBars_EmitsInsufficientBarsReason(string _, string argumentsJson)
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();
        using JsonDocument resp = (await McpTestSupport.CallAsync(dispatcher, "sa_project_price_path", argumentsJson))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.True(root.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(ProjectionEmptyReason.InsufficientBarsCode, root.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()));
    }

    [Theory]
    [InlineData("Hmm_NonPositiveSeries", """{"symbol":"PROJ_ZERO","timeFrame":"D1","method":"Hmm","lookbackBars":15}""")]
    [InlineData("Arima_UnfittableOrder", """{"symbol":"PROJ_OK","timeFrame":"D1","method":"Arima","lookbackBars":3,"arimaP":5,"arimaD":1,"arimaQ":5}""")]
    public async Task EmptyProjectionAtOrAboveMinimumBars_EmitsProjectionUnavailableReason(string _, string argumentsJson)
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();
        using JsonDocument resp = (await McpTestSupport.CallAsync(dispatcher, "sa_project_price_path", argumentsJson))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.True(root.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(ProjectionEmptyReason.ProjectionUnavailableCode, root.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()));
    }

    [Theory]
    [InlineData("Arima")]
    [InlineData("Fft")]
    [InlineData("Ssa")]
    [InlineData("Hmm")]
    public async Task NonEmptyProjection_OmitsReasonCodeAndReasonKeys(string method)
    {
        McpMessageDispatcher dispatcher = await StartMcpAsync();
        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_project_price_path",
            $$"""{"symbol":"PROJ_OK","timeFrame":"D1","method":"{{method}}","lookbackBars":40,"horizonBars":5}"""))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.False(root.GetProperty("isEmpty").GetBoolean());
        Assert.True(root.GetProperty("projectedPath").GetArrayLength() > 0);
        Assert.False(root.TryGetProperty("reasonCode", out _));
        Assert.False(root.TryGetProperty("reason", out _));
    }

    [Fact]
    public void Classify_UsesCoreMinimumConstantsAtExactBoundaries()
    {
        Assert.Equal(ProjectionEmptyReason.InsufficientBarsCode, ProjectionEmptyReason.Classify("Fft", FftProjectionAnalysis.MinSampleCount - 1, 1));
        Assert.Equal(ProjectionEmptyReason.ProjectionUnavailableCode, ProjectionEmptyReason.Classify("Fft", FftProjectionAnalysis.MinSampleCount, 1));

        Assert.Equal(ProjectionEmptyReason.InsufficientBarsCode, ProjectionEmptyReason.Classify("Ssa", SsaProjectionAnalysis.MinSampleCount - 1, 1));
        Assert.Equal(ProjectionEmptyReason.ProjectionUnavailableCode, ProjectionEmptyReason.Classify("Ssa", SsaProjectionAnalysis.MinSampleCount, 1));

        Assert.Equal(ProjectionEmptyReason.InsufficientBarsCode, ProjectionEmptyReason.Classify("Hmm", HmmProjectionAnalysis.MinSampleCount - 1, 1));
        Assert.Equal(ProjectionEmptyReason.ProjectionUnavailableCode, ProjectionEmptyReason.Classify("Hmm", HmmProjectionAnalysis.MinSampleCount, 1));

        Assert.Equal(ProjectionEmptyReason.InsufficientBarsCode, ProjectionEmptyReason.Classify("Arima", 2, arimaD: 2));
        Assert.Equal(ProjectionEmptyReason.ProjectionUnavailableCode, ProjectionEmptyReason.Classify("Arima", 3, arimaD: 2));
    }
}
