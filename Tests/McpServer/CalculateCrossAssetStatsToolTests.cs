using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class CalculateCrossAssetStatsToolTests : IDisposable
{
    private const int StoredDays = 200;
    private const string Sym1 = "MSFT";
    private const string Sym2 = "AAPL";
    private const string Sym3 = "GOOGL";
    private const string Tool = "sa_calculate_cross_asset_stats";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, "msft", "aapl", "googl");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] limitOverrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(limitOverrides), new LogSink(new StringWriter()), _fixture.DailyDirectory);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonElement> CallAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static async Task<JsonElement> CallExpectingErrorAsync(McpMessageDispatcher dispatcher, string arguments)
    {
        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, Tool, arguments))!;
        Assert.True(McpTestSupport.IsError(response), "Expected tool error but call succeeded");
        return McpTestSupport.Structured(response).Clone();
    }

    [Fact]
    public async Task BasicCalculation_SucceedsWithCorrelationAndBeta()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"benchmark":"{{Sym1}}","timeFrame":"D1"}""";
        JsonElement result = await CallAsync(dispatcher, args);

        Assert.True(result.TryGetProperty("symbols", out JsonElement symbolsElem));
        Assert.Equal(2, symbolsElem.GetArrayLength());
        Assert.Equal(Sym1, symbolsElem[0].GetString());
        Assert.Equal(Sym2, symbolsElem[1].GetString());

        Assert.True(result.TryGetProperty("commonDateCount", out JsonElement countElem));
        Assert.True(countElem.GetInt32() >= 3);

        Assert.Equal("SimpleReturn", result.GetProperty("returnBasis").GetString());
        Assert.Equal("Sample(N-1)", result.GetProperty("covarianceDegreesOfFreedom").GetString());

        // Correlation matrix: 2x2
        JsonElement corr = result.GetProperty("correlation");
        Assert.Equal(2, corr.GetArrayLength());
        Assert.Equal(1.0, corr[0][0].GetDouble(), 6);
        Assert.Equal(1.0, corr[1][1].GetDouble(), 6);
        double r = corr[0][1].GetDouble();
        Assert.True(r >= -1.0 && r <= 1.0);
        Assert.Equal(r, corr[1][0].GetDouble(), 6);

        // Covariance is omitted by default
        Assert.False(result.TryGetProperty("covariance", out _));

        // BetaAlpha
        JsonElement betaAlpha = result.GetProperty("betaAlpha");
        Assert.Equal(2, betaAlpha.GetArrayLength());
        // First entry is benchmark itself
        Assert.Equal(Sym1, betaAlpha[0].GetProperty("symbol").GetString());
        Assert.Equal(1.0, betaAlpha[0].GetProperty("beta").GetDouble(), 6);
        Assert.Equal(0.0, betaAlpha[0].GetProperty("alpha").GetDouble(), 6);
        Assert.Equal(1.0, betaAlpha[0].GetProperty("rSquared").GetDouble(), 6);
    }

    [Fact]
    public async Task IncludeCovariance_PopulatesCovarianceMatrix()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"includeCovariance":true}""";
        JsonElement result = await CallAsync(dispatcher, args);

        Assert.True(result.TryGetProperty("covariance", out JsonElement cov));
        Assert.Equal(2, cov.GetArrayLength());
        Assert.True(cov[0][0].GetDouble() > 0);
        Assert.True(cov[1][1].GetDouble() > 0);
        Assert.Equal(cov[0][1].GetDouble(), cov[1][0].GetDouble(), 9);
    }

    [Fact]
    public async Task LogReturnBasis_AppliesLogReturnCalculation()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"returnBasis":"LogReturn"}""";
        JsonElement result = await CallAsync(dispatcher, args);

        Assert.Equal("LogReturn", result.GetProperty("returnBasis").GetString());
    }

    [Fact]
    public async Task MutuallyExclusiveSymbolsAndGroupId_Rejected()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"groupId":"00000000-0000-0000-0000-000000000000"}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);
        Assert.Equal(ToolErrorCodes.InvalidArgument, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FewerThanTwoSymbols_Rejected()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}"]}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);
        Assert.Equal(ToolErrorCodes.InvalidArgument, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExceedingMaxCrossAssetSymbols_Rejected()
    {
        // Configure limit to 2
        McpMessageDispatcher dispatcher = await StartAsync(("MaxCrossAssetSymbols", "2"));
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}","OTHER"]}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);
        Assert.Equal(ToolErrorCodes.LimitExceeded, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownSymbol_ProducesSymbolNotFound()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","NOSUCH"]}""";
        JsonElement error = await CallExpectingErrorAsync(dispatcher, args);
        Assert.Equal(ToolErrorCodes.SymbolNotFound, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EndDateInvariance_BarsBeyondEndDateDoNotAffectCalculation()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        // Run with endDate midway through stored days (e.g. 100 days ago)
        DateTime cutoff = DateTime.UtcNow.Date.AddDays(-50);
        string endDateStr = cutoff.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture);

        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"endDate":"{{endDateStr}}","includeCovariance":true}""";
        JsonElement firstRun = await CallAsync(dispatcher, args);

        // Run again with the same endDate
        JsonElement secondRun = await CallAsync(dispatcher, args);

        Assert.Equal(firstRun.GetProperty("commonDateCount").GetInt32(), secondRun.GetProperty("commonDateCount").GetInt32());
        Assert.Equal(firstRun.GetProperty("startDate").GetString(), secondRun.GetProperty("startDate").GetString());
        Assert.Equal(firstRun.GetProperty("endDate").GetString(), secondRun.GetProperty("endDate").GetString());

        double corr1 = firstRun.GetProperty("correlation")[0][1].GetDouble();
        double corr2 = secondRun.GetProperty("correlation")[0][1].GetDouble();
        Assert.Equal(corr1, corr2, 9);
    }

    [Fact]
    public async Task ExternalBenchmark_ComputesBetaAlpha_WithoutAddingToSymbolsOrCorrelationMatrix()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        // Universe has Sym1 (MSFT) and Sym2 (AAPL), benchmark is external Sym3 (GOOGL)
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"benchmark":"{{Sym3}}","includeCovariance":true}""";
        JsonElement result = await CallAsync(dispatcher, args);

        JsonElement symbolsElem = result.GetProperty("symbols");
        Assert.Equal(2, symbolsElem.GetArrayLength());
        Assert.Equal(Sym1, symbolsElem[0].GetString());
        Assert.Equal(Sym2, symbolsElem[1].GetString());

        // Correlation and Covariance matrices MUST be 2x2
        JsonElement corr = result.GetProperty("correlation");
        Assert.Equal(2, corr.GetArrayLength());
        Assert.Equal(2, corr[0].GetArrayLength());
        Assert.Equal(2, corr[1].GetArrayLength());

        JsonElement cov = result.GetProperty("covariance");
        Assert.Equal(2, cov.GetArrayLength());
        Assert.Equal(2, cov[0].GetArrayLength());
        Assert.Equal(2, cov[1].GetArrayLength());

        // BetaAlpha has 2 entries (MSFT and AAPL vs GOOGL)
        JsonElement betaAlpha = result.GetProperty("betaAlpha");
        Assert.Equal(2, betaAlpha.GetArrayLength());
        Assert.Equal(Sym1, betaAlpha[0].GetProperty("symbol").GetString());
        Assert.True(betaAlpha[0].TryGetProperty("beta", out JsonElement b1) && b1.ValueKind == JsonValueKind.Number);
        Assert.Equal(Sym2, betaAlpha[1].GetProperty("symbol").GetString());
        Assert.True(betaAlpha[1].TryGetProperty("beta", out JsonElement b2) && b2.ValueKind == JsonValueKind.Number);
    }

    // ---- statistical functions plan, Run R8 (M2): includeDistributionStats ----------------------------------------------------------

    private static readonly string[] DefaultTopLevelKeys =
    {
        "symbols", "commonDateCount", "multiBarReturnCount", "startDate", "endDate", "returnBasis", "covarianceDegreesOfFreedom", "correlation", "betaAlpha", "excludedSymbols", "notes",
    };

    private static readonly string[] DefaultBetaAlphaKeys = { "symbol", "beta", "alpha", "rSquared" };

    [Fact]
    public async Task WithoutTheFlagTheOutputIsTheKnownShape_AndFalseEqualsOmitted()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"benchmark":"{{Sym1}}","timeFrame":"D1"}""";
        string argsFalse = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"benchmark":"{{Sym1}}","timeFrame":"D1","includeDistributionStats":false}""";

        JsonElement omitted = await CallAsync(dispatcher, args);
        JsonElement explicitFalse = await CallAsync(dispatcher, argsFalse);

        Assert.Equal(DefaultTopLevelKeys, omitted.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(DefaultBetaAlphaKeys, omitted.GetProperty("betaAlpha")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(omitted.GetRawText(), explicitFalse.GetRawText());
    }

    [Fact]
    public async Task WithTheFlagTheBetaEntriesAndASymbolStatsArrayAreAdded_AndEveryExistingFieldIsUnchanged()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string baseArgs = $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"benchmark":"{{Sym1}}","includeCovariance":true""";
        JsonElement plain = await CallAsync(dispatcher, baseArgs + "}");
        JsonElement rich = await CallAsync(dispatcher, baseArgs + ""","includeDistributionStats":true}""");

        // Existing content: the same values in the same order (the new keys are only added).
        foreach (string key in DefaultTopLevelKeys.Where(k => k != "betaAlpha"))
        {
            Assert.Equal(plain.GetProperty(key).GetRawText(), rich.GetProperty(key).GetRawText());
        }

        Assert.Equal(plain.GetProperty("covariance").GetRawText(), rich.GetProperty("covariance").GetRawText());
        JsonElement plainBeta = plain.GetProperty("betaAlpha");
        JsonElement richBeta = rich.GetProperty("betaAlpha");
        for (int i = 0; i < plainBeta.GetArrayLength(); i++)
        {
            foreach (string key in DefaultBetaAlphaKeys)
            {
                Assert.Equal(plainBeta[i].GetProperty(key).GetRawText(), richBeta[i].GetProperty(key).GetRawText());
            }

            Assert.Equal(DefaultBetaAlphaKeys.Concat(new[] { "downsideBeta", "upsideBeta" }).ToArray(), richBeta[i].EnumerateObject().Select(p => p.Name).ToArray());
        }

        // The benchmark's conditional betas on itself are 1.
        Assert.Equal(1.0, richBeta[0].GetProperty("downsideBeta").GetDouble(), 9);
        Assert.Equal(1.0, richBeta[0].GetProperty("upsideBeta").GetDouble(), 9);

        JsonElement stats = rich.GetProperty("symbolStats");
        Assert.Equal(2, stats.GetArrayLength());
        Assert.Equal(Sym1, stats[0].GetProperty("symbol").GetString());
        Assert.Equal(Sym2, stats[1].GetProperty("symbol").GetString());
        Assert.Equal(
            new[] { "symbol", "mean", "stdDev", "downsideDeviation", "skewness", "excessKurtosis", "jarqueBeraStat", "jarqueBeraPValue", "cornishFisherVar95" },
            stats[0].EnumerateObject().Select(p => p.Name).ToArray());

        // The sample standard deviation is the square root of the diagonal of the sample covariance matrix of the same result (divisor N - 1).
        JsonElement cov = rich.GetProperty("covariance");
        for (int i = 0; i < 2; i++)
        {
            double sd = stats[i].GetProperty("stdDev").GetDouble();
            Assert.Equal(cov[i][i].GetDouble(), sd * sd, 12);
            Assert.True(stats[i].GetProperty("downsideDeviation").GetDouble() >= 0.0);
            double p = stats[i].GetProperty("jarqueBeraPValue").GetDouble();
            Assert.InRange(p, 0.0, 1.0);
            Assert.Equal(Math.Exp(-stats[i].GetProperty("jarqueBeraStat").GetDouble() / 2.0), p, 12);
        }
    }

    [Fact]
    public async Task WithoutABenchmarkTheFlagStillAddsSymbolStats_AndNoBetaEntries()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        JsonElement result = await CallAsync(dispatcher, $$"""{"symbols":["{{Sym1}}","{{Sym2}}","{{Sym3}}"],"includeDistributionStats":true}""");

        Assert.False(result.TryGetProperty("betaAlpha", out _));
        JsonElement stats = result.GetProperty("symbolStats");
        Assert.Equal(new[] { Sym1, Sym2, Sym3 }, stats.EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!).ToArray());
    }

    [Fact]
    public async Task TheStatsFollowTheReturnBasis()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string Args(string basis) => $$"""{"symbols":["{{Sym1}}","{{Sym2}}"],"returnBasis":"{{basis}}","includeDistributionStats":true}""";

        JsonElement simple = await CallAsync(dispatcher, Args("SimpleReturn"));
        JsonElement log = await CallAsync(dispatcher, Args("LogReturn"));

        Assert.Equal("LogReturn", log.GetProperty("returnBasis").GetString());
        Assert.NotEqual(simple.GetProperty("symbolStats")[0].GetProperty("mean").GetRawText(), log.GetProperty("symbolStats")[0].GetProperty("mean").GetRawText());
    }

    [Fact]
    public async Task TheSchemaAndTheDescriptionNameTheFlagAndTheSignOfTheVar()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using var list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", CancellationToken.None))!);

        JsonElement tool = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == Tool);
        JsonElement flag = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("includeDistributionStats");
        Assert.Equal("boolean", flag.GetProperty("type").GetString());
        Assert.Contains("negative", flag.GetProperty("description").GetString());
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("includeDistributionStats", description);
        Assert.Contains("valueAtRisk", description);
        Assert.Contains("stockanalyzer://docs/tools/sa_calculate_cross_asset_stats", description, StringComparison.Ordinal);
        string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "docs", "MCP", "tools", "sa_calculate_cross_asset_stats.md"));
        Assert.Contains("LOSS IS NEGATIVE", doc);
        Assert.Contains("valueAtRisk", doc);
    }
}
