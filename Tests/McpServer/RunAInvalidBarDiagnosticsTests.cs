using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunAInvalidBarDiagnosticsTests : IDisposable
{
    private const string Conditions =
        """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}}""";

    private const string ParameterConditions =
        """{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}},"exitLong":{"left":{"price":"Close"},"comparison":"LessThan","right":{"indicator":"SMA","parameters":{"Period":{"$param":"period"}}}}}""";

    private const string ParameterRanges =
        """[{"name":"period","start":3,"end":5,"step":1}]""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, "8035-T", "8411-T", "9984-T", "AAPL");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private McpMessageDispatcher StartMcp(Action<IReadOnlyStockAnalyzerGateway>? captureGateway = null, params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(
            configuration,
            new LogSink(new StringWriter()),
            gatewayWrapper: captureGateway is null ? null : g =>
            {
                captureGateway(g);
                return g;
            });
        _runtimes.Add(runtime);
        runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
        return runtime.Dispatcher;
    }

    private string FixtureRoot => Path.GetDirectoryName(_fixture.DailyDirectory)!;

    private void SeedInvalid8035T()
    {
        McpTestSupport.WriteEquity(_fixture, "8035-T", "2022-05-10", 60);
        WriteSeriesWithSingleCorruptBar(
            "8035-T",
            new DateOnly(2022, 5, 17),
            open: 3821.333m,
            high: 3866.667m,
            low: 3770.667m,
            close: 3869.333m,
            volume: 1000L,
            validStart: new DateOnly(2022, 5, 10),
            validCount: 70);
    }

    private void SeedInvalid8411T()
    {
        McpTestSupport.WriteEquity(_fixture, "8411-T", "2022-05-10", 60);
        WriteSeriesWithSingleCorruptBar(
            "8411-T",
            new DateOnly(2022, 5, 17),
            open: 1500m,
            high: 1513.5m,
            low: 1487.5m,
            close: 1486.5m,
            volume: 2000L,
            validStart: new DateOnly(2022, 5, 10),
            validCount: 70);
    }

    private void WriteSeriesWithSingleCorruptBar(
        string symbol,
        DateOnly corruptDate,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        DateOnly validStart,
        int validCount)
    {
        var rows = new List<string>(validCount + 1);
        for (int i = 0; i < validCount; i++)
        {
            DateOnly day = validStart.AddDays(i);
            if (day == corruptDate)
            {
                rows.Add(FormattableString.Invariant(
                    $"(DATE '{corruptDate:yyyy-MM-dd}', CAST({open} AS DOUBLE), CAST({high} AS DOUBLE), CAST({low} AS DOUBLE), CAST({close} AS DOUBLE), CAST({volume} AS BIGINT))"));
            }
            else
            {
                decimal baseClose = 3800m + (i % 5);
                decimal o = baseClose - 1m;
                decimal h = baseClose + 10m;
                decimal l = baseClose - 10m;
                rows.Add(FormattableString.Invariant(
                    $"(DATE '{day:yyyy-MM-dd}', CAST({o} AS DOUBLE), CAST({h} AS DOUBLE), CAST({l} AS DOUBLE), CAST({baseClose} AS DOUBLE), CAST(1000 AS BIGINT))"));
            }
        }

        string query = $"""
            SELECT * FROM (VALUES
                {string.Join(",\n                ", rows)}
            ) AS t(date, open, high, low, close, volume)
            """;
        McpTestSupport.WriteParquet(Path.Combine(_fixture.DailyDirectory, symbol + ".parquet"), query);
    }

    private static void AssertDoesNotLeakFixturePath(string text, string rootPath)
    {
        Assert.DoesNotContain(rootPath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".parquet", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"startDate\":\"2022-06-01\",\"endDate\":\"2022-07-15\"")]
    public async Task SingleSymbolBacktestTools_RejectStoredInvalidBarWithSymbolDateOhlcvAndHint(string periodArgs)
    {
        SeedInvalid8035T();
        McpTestSupport.WriteEquity(_fixture, "9984-T", "2022-05-10", 70);
        McpMessageDispatcher dispatcher = StartMcp();

        using JsonDocument validResp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_run_backtest",
            $$"""{"symbol":"9984-T","conditions":{{Conditions}}{{periodArgs}}}"""))!;
        Assert.False(McpTestSupport.IsError(validResp), McpTestSupport.Structured(validResp).ToString());

        string[] toolCalls =
        {
            "sa_run_backtest",
            "sa_optimize_backtest",
            "sa_run_walk_forward_analysis",
        };

        string[] payloads =
        {
            $$"""{"symbol":"8035-T","conditions":{{Conditions}}{{periodArgs}}}""",
            $$"""{"symbol":"8035-T","conditions":{{ParameterConditions}},"parameterRanges":{{ParameterRanges}},"objective":"totalPnL"{{periodArgs}}}""",
            $$$"""{"symbol":"8035-T","conditions":{{{ParameterConditions}}},"parameterRanges":{{{ParameterRanges}}},"objective":"totalPnL","windows":{"generator":{"mode":"Rolling","inSampleBars":20,"outOfSampleBars":10,"stepBars":10} } }""",
        };

        for (int i = 0; i < toolCalls.Length; i++)
        {
            using JsonDocument errorResp = (await McpTestSupport.CallAsync(dispatcher, toolCalls[i], payloads[i]))!;
            Assert.True(McpTestSupport.IsError(errorResp), $"Expected error for {toolCalls[i]}");
            Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(errorResp));

            JsonElement structured = McpTestSupport.Structured(errorResp);
            string message = structured.GetProperty("message").GetString()!;
            string hint = structured.GetProperty("hint").GetString()!;
            Assert.Contains("8035-T", message, StringComparison.Ordinal);
            Assert.Contains("2022-05-17", message, StringComparison.Ordinal);
            Assert.Contains("Close 3869.333 > High 3866.667", message, StringComparison.Ordinal);
            Assert.Contains("O=3821.333", message, StringComparison.Ordinal);
            Assert.Contains("H=3866.667", message, StringComparison.Ordinal);
            Assert.Contains("L=3770.667", message, StringComparison.Ordinal);
            Assert.Contains("C=3869.333", message, StringComparison.Ordinal);
            Assert.Contains("V=1000", message, StringComparison.Ordinal);
            Assert.Contains("sa_get_symbol_coverage", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PortfolioBacktest_EnumeratesAllInvalidSymbolsWithDateAndViolation()
    {
        SeedInvalid8035T();
        SeedInvalid8411T();
        McpTestSupport.WriteEquity(_fixture, "9984-T", "2022-05-10", 70);
        McpMessageDispatcher dispatcher = StartMcp();

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_run_portfolio_backtest",
            $$"""{"symbols":["8035-T","8411-T","9984-T"],"conditions":{{Conditions}},"startDate":"2022-06-01","endDate":"2022-07-15"}"""))!;

        Assert.True(McpTestSupport.IsError(resp));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(resp));

        JsonElement structured = McpTestSupport.Structured(resp);
        string message = structured.GetProperty("message").GetString()!;
        string hint = structured.GetProperty("hint").GetString()!;
        Assert.Contains("8035-T", message, StringComparison.Ordinal);
        Assert.Contains("Close 3869.333 > High 3866.667", message, StringComparison.Ordinal);
        Assert.Contains("8411-T", message, StringComparison.Ordinal);
        Assert.Contains("Close 1486.5 < Low 1487.5", message, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", message, StringComparison.Ordinal);
        Assert.Contains("sa_get_symbol_coverage", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("9984-T", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortfolioRlPreflight_EnrichesReadErrorAndPopulatesNoUsableDataSymbolDetailsWithoutPaths()
    {
        SeedInvalid8035T();
        SeedInvalid8411T();
        McpTestSupport.WriteEquity(_fixture, "9984-T", "2022-01-01", 600);
        McpTestSupport.WriteEquity(_fixture, "AAPL", "2022-01-01", 600);
        McpMessageDispatcher dispatcher = StartMcp();

        // 1) Normal branch with explicit dates: ReadError on 8035-T gets CandleInvariant detail; every item has path-free detail.
        using JsonDocument explicitResp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_check_portfolio_rl_preflight",
            """{"symbols":["8035-T","9984-T","AAPL"],"startDate":"2022-05-10","endDateExclusive":"2023-06-01","mode":"Standard"}"""))!;
        Assert.False(McpTestSupport.IsError(explicitResp), McpTestSupport.Structured(explicitResp).ToString());

        JsonElement explicitStructured = McpTestSupport.Structured(explicitResp);
        JsonElement explicitDetails = explicitStructured.GetProperty("symbolDetails");
        Assert.Equal(3, explicitDetails.GetArrayLength());

        JsonElement? entry8035Explicit = null;
        foreach (JsonElement item in explicitDetails.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("detail", out JsonElement detailProp));
            Assert.Equal(JsonValueKind.String, detailProp.ValueKind);
            string detail = detailProp.GetString()!;
            AssertDoesNotLeakFixturePath(detail, FixtureRoot);

            if (item.GetProperty("symbol").GetString() == "8035-T")
            {
                entry8035Explicit = item;
            }
        }

        Assert.NotNull(entry8035Explicit);
        Assert.Equal("ReadError", entry8035Explicit.Value.GetProperty("status").GetString());
        string explicitDetail8035 = entry8035Explicit.Value.GetProperty("detail").GetString()!;
        Assert.Contains("8035-T", explicitDetail8035, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", explicitDetail8035, StringComparison.Ordinal);
        Assert.Contains("Close 3869.333 > High 3866.667", explicitDetail8035, StringComparison.Ordinal);

        // 2) NoUsableData branch (only 1 equity per currency so SuggestStart returns null):
        //    symbolDetails MUST NOT be empty and MUST diagnose stored invalid bars for 8035-T.
        await _fixture.SeedMetadataAsync("8035-T", TickerMetadata.Unknown with
        {
            Currency = "JPY",
            QuoteType = "EQUITY",
            ExchangeTimezoneName = "Asia/Tokyo",
        });

        using JsonDocument autoResp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_check_portfolio_rl_preflight",
            """{"symbols":["8035-T","AAPL"],"mode":"Standard"}"""))!;
        Assert.False(McpTestSupport.IsError(autoResp), McpTestSupport.Structured(autoResp).ToString());

        JsonElement autoStructured = McpTestSupport.Structured(autoResp);
        Assert.False(autoStructured.GetProperty("canStart").GetBoolean());
        Assert.False(autoStructured.GetProperty("feasible").GetBoolean());
        Assert.Equal("NoUsableData", autoStructured.GetProperty("blocker").GetString());
        JsonElement autoDetails = autoStructured.GetProperty("symbolDetails");
        Assert.Equal(2, autoDetails.GetArrayLength());

        JsonElement? entry8035Auto = null;
        JsonElement? entryAaplAuto = null;
        foreach (JsonElement item in autoDetails.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("detail", out JsonElement detailProp));
            Assert.Equal(JsonValueKind.String, detailProp.ValueKind);
            string detail = detailProp.GetString()!;
            AssertDoesNotLeakFixturePath(detail, FixtureRoot);

            string? symName = item.GetProperty("symbol").GetString();
            if (symName == "8035-T")
            {
                entry8035Auto = item;
            }
            else if (symName == "AAPL")
            {
                entryAaplAuto = item;
            }
        }

        Assert.NotNull(entry8035Auto);
        Assert.Equal("ReadError", entry8035Auto.Value.GetProperty("status").GetString());
        string autoDetail8035 = entry8035Auto.Value.GetProperty("detail").GetString()!;
        Assert.Contains("8035-T", autoDetail8035, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", autoDetail8035, StringComparison.Ordinal);
        Assert.Contains("Close 3869.333 > High 3866.667", autoDetail8035, StringComparison.Ordinal);

        Assert.NotNull(entryAaplAuto);
        Assert.False(string.IsNullOrWhiteSpace(entryAaplAuto.Value.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task RlPlaybackPreflight_EnrichesInvalidOhlcvSourceUnavailableWithoutLeakingPaths()
    {
        SeedInvalid8035T();
        IReadOnlyStockAnalyzerGateway? gateway = null;
        StartMcp(g => gateway = g);
        Assert.NotNull(gateway);

        var registry = new StubRlRegistry("m1", "8035-T");
        var dataService = new FailingRlDialogDataService(
            registry,
            _ => Task.FromException<RlAnchorRange>(
                new RlExternalReplayException(RlExternalReplayRejection.SourceUnavailable, "Invalid local source OHLCV; no repair is permitted.")));
        var symbols = new SymbolResolver(gateway!);
        var sanitizer = new ErrorMessageSanitizer(new[] { FixtureRoot });

        var tool = new CheckRlPlaybackPreflightTool(
            symbols,
            registry,
            dataService,
            sourceFolders: null,
            sanitizer: sanitizer,
            gateway: gateway);

        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool });
        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_check_rl_playback_preflight",
            """{"modelId":"m1","symbol":"8035-T"}"""))!;

        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());
        JsonElement structured = McpTestSupport.Structured(resp);
        Assert.False(structured.GetProperty("canReplay").GetBoolean());

        string reason = structured.GetProperty("reason").GetString()!;
        Assert.Contains("8035-T", reason, StringComparison.Ordinal);
        Assert.Contains("2022-05-17", reason, StringComparison.Ordinal);
        Assert.Contains("Close 3869.333 > High 3866.667", reason, StringComparison.Ordinal);
        AssertDoesNotLeakFixturePath(reason, FixtureRoot);
    }

    [Fact]
    public async Task OlderNonPositivePriceBarBeforeMaxCandlesPerRequest_IsRejectedByBacktestPreCheck()
    {
        // High=10, Low=0, Open=5, Close=0 satisfies CandleData.IsValid(), failing only NonPositivePrice on 2022-05-17
        // which lies older than the 40-bar tail loaded by MaxCandlesPerRequest=40 across a 100-day series.
        WriteSeriesWithSingleCorruptBar(
            "AAPL",
            new DateOnly(2022, 5, 17),
            open: 5m,
            high: 10m,
            low: 0m,
            close: 0m,
            volume: 1000L,
            validStart: new DateOnly(2022, 5, 10),
            validCount: 100);
        McpTestSupport.WriteEquity(_fixture, "9984-T", "2022-05-10", 100);

        McpMessageDispatcher dispatcher = StartMcp(
            limitOverrides: new[]
            {
                ("MaxCandlesPerRequest", "40"),
                ("DefaultCandleCount", "40"),
                ("MaxOutputTailBars", "40"),
                ("DefaultOutputTailBars", "40"),
            });

        var calls = new (string Tool, string Args)[]
        {
            ("sa_run_backtest", $$"""{"symbol":"AAPL","conditions":{{Conditions}},"startDate":"2022-05-10","endDate":"2022-08-17"}"""),
            ("sa_run_portfolio_backtest", $$"""{"symbols":["AAPL","9984-T"],"conditions":{{Conditions}},"startDate":"2022-05-10","endDate":"2022-08-17"}"""),
            ("sa_optimize_backtest", $$"""{"symbol":"AAPL","conditions":{{ParameterConditions}},"parameterRanges":{{ParameterRanges}},"objective":"totalPnL","startDate":"2022-05-10","endDate":"2022-08-17"}"""),
            ("sa_run_walk_forward_analysis", $$"""{"symbol":"AAPL","conditions":{{ParameterConditions}},"parameterRanges":{{ParameterRanges}},"objective":"totalPnL","windows":{"list":[{"inSampleStart":"2022-05-10","inSampleEnd":"2022-07-01","outOfSampleStart":"2022-07-02","outOfSampleEnd":"2022-08-17"}] } }"""),
        };

        foreach ((string tool, string args) in calls)
        {
            using JsonDocument resp = (await McpTestSupport.CallAsync(dispatcher, tool, args))!;
            Assert.True(McpTestSupport.IsError(resp), $"{tool} should fail upfront on NonPositivePrice bar 2022-05-17.");
            Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(resp));
            string message = McpTestSupport.Structured(resp).GetProperty("message").GetString()!;
            Assert.Contains("AAPL 2022-05-17", message, StringComparison.Ordinal);
            Assert.Contains("Non-positive price", message, StringComparison.Ordinal);
        }
    }

    private sealed class StubRlRegistry(string modelId, string symbol) : IRlModelGenerationRegistry
    {
        private readonly RlGeneration _generation = new(
            modelId,
            symbol,
            "rev-1",
            "hash-1",
            "eval-1",
            0.1m,
            0.05m,
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        public string? RlActiveId => modelId;
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<RlGeneration> ListRl() => new[] { _generation };
        public Task<bool> ActivateRlAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ModelGenerationLease? AcquireRl(string id) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string id) => id == modelId ? _generation : null;
    }

    private sealed class FailingRlDialogDataService(
        IRlModelGenerationRegistry registry,
        Func<CancellationToken, Task<RlAnchorRange>> getAnchorRange)
        : RlDialogDataService(registry, null!)
    {
        public override Task<RlAnchorRange> GetAnchorRangeAsync(string modelId, string symbol, CancellationToken ct)
            => getAnchorRange(ct);
    }
}
