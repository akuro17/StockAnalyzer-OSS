using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunETasksTests : IDisposable
{
    private const int SrDays = 100;
    private const string SrSymbol = "SRTEST";
    private const string Msft = "MSFT";
    private const string Aapl = "AAPL";
    private const string Nvda = "NVDA";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(SrDays, Msft, Aapl, Nvda, SrSymbol);
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly string _workspacePath;

    public RunETasksTests()
    {
        string configDir = Path.Combine(_fixture.Root, "Config");
        Directory.CreateDirectory(configDir);
        _workspacePath = Path.Combine(configDir, LayoutConstants.DefaultWorkspaceFileName);

        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Msft, new[] { 100m, 110m });
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Aapl, new[] { 200m, 190m });
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Nvda, new[] { 50m, 55m });

        var srCloses = Enumerable.Range(0, SrDays).Select(i => 100m + (decimal)(Math.Sin(i / 5.0) * 10.0)).ToList();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, SrSymbol, srCloses);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        await _fixture.SeedMetadataAsync(Msft, new TickerMetadata { ShortName = "Microsoft", Sector = "Technology", Tag = "AI, Cloud" });
        await _fixture.SeedMetadataAsync(Aapl, new TickerMetadata { ShortName = "Apple", Sector = "Technology", Tag = "Hardware" });
        await _fixture.SeedMetadataAsync(Nvda, new TickerMetadata { ShortName = "Nvidia", Sector = "Semiconductors", Tag = "AI" });
        await _fixture.SeedMetadataAsync(SrSymbol, new TickerMetadata { ShortName = "SR Test", Sector = "Finance", Tag = "Value" });

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture))
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(
            configuration,
            new LogSink(new StringWriter()),
            workspaceSource: new FileReadOnlyWorkspaceSource(_workspacePath));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    // ---- E1: sa_filter_tickers Screener Operator Aliases -------------------------------------------------------

    [Theory]
    [InlineData("Close", "Equal", "110", new[] { Msft })]
    [InlineData("Close", "equal", "110", new[] { Msft })]
    [InlineData("Close", "NotEqual", "110", new[] { Aapl, Nvda, SrSymbol })]
    [InlineData("Sector", "Equal", "Technology", new[] { Msft, Aapl })]
    [InlineData("Sector", "NotEqual", "Technology", new[] { Nvda, SrSymbol })]
    [InlineData("Sector", "Contains", "Tech", new[] { Msft, Aapl })]
    [InlineData("Sector", "DoesNotContain", "Tech", new[] { Nvda, SrSymbol })]
    [InlineData("Tag", "DoesNotContain", "AI", new[] { Aapl, SrSymbol })]
    [InlineData("Tag", "Equal", "AI", new[] { Nvda })]
    public async Task FilterTickers_AcceptsScreenerOperatorSpellings(string field, string op, string value, string[] expectedSymbols)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string args = $$"""{"rules":[{"field":"{{field}}","operator":"{{op}}","value":"{{value}}"}]}""";

        using JsonDocument? response = await McpTestSupport.CallAsync(dispatcher, "sa_filter_tickers", args);
        Assert.NotNull(response);
        Assert.False(McpTestSupport.IsError(response!), McpTestSupport.Structured(response!).ToString());

        string[] actual = McpTestSupport.Structured(response!)
            .GetProperty("matches")
            .EnumerateArray()
            .Select(m => m.GetProperty("symbol").GetString()!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedSymbols.OrderBy(s => s, StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact]
    public async Task FilterTickers_InvalidOperatorHintIncludesScreenerSpellings()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument? textResp = await McpTestSupport.CallAsync(dispatcher, "sa_filter_tickers", """{"rules":[{"field":"Sector","operator":"BAD","value":"Tech"}]}""");
        using JsonDocument? numResp = await McpTestSupport.CallAsync(dispatcher, "sa_filter_tickers", """{"rules":[{"field":"Close","operator":"BAD","value":"100"}]}""");

        Assert.True(McpTestSupport.IsError(textResp!));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(textResp!));
        string textMsg = McpTestSupport.Structured(textResp!).GetProperty("message").GetString()!;
        Assert.Contains("DoesNotContain", textMsg);
        Assert.Contains("Equal", textMsg);
        Assert.Contains("NotEqual", textMsg);

        Assert.True(McpTestSupport.IsError(numResp!));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(numResp!));
        string numMsg = McpTestSupport.Structured(numResp!).GetProperty("message").GetString()!;
        Assert.Contains("Equal", numMsg);
        Assert.Contains("NotEqual", numMsg);
    }

    // ---- E2: FindSupportResistance active reservation & maxLevels <= contract ---------------------------------

    private static SsaSupportResistanceLevel Level(double price, double strength, bool isResistance)
        => new(price, Hits: 3, StrengthScore: strength, LatestIndex: 10, TargetTime: null, IsResistance: isResistance);

    [Fact]
    public void SelectLevels_MaxLevelsOne_TieBreakerStep1CloserDistanceWinsOverStrength()
    {
        var activeSup = Level(98.0, strength: 10.0, isResistance: false);
        var activeRes = Level(105.0, strength: 90.0, isResistance: true);
        var sorted = new[] { activeRes, activeSup };

        var selected = FindSupportResistanceTool.SelectLevels(sorted, activeSup, activeRes, maxLevels: 1, currentPrice: 100m);

        Assert.Single(selected);
        Assert.Same(activeSup, selected[0]);
    }

    [Fact]
    public void SelectLevels_MaxLevelsOne_TieBreakerStep2StrengthWinsOnEqualDistance()
    {
        var activeSup = Level(95.0, strength: 40.0, isResistance: false);
        var activeRes = Level(105.0, strength: 85.0, isResistance: true);
        var sorted = new[] { activeRes, activeSup };

        var selected = FindSupportResistanceTool.SelectLevels(sorted, activeSup, activeRes, maxLevels: 1, currentPrice: 100m);

        Assert.Single(selected);
        Assert.Same(activeRes, selected[0]);
    }

    [Fact]
    public void SelectLevels_MaxLevelsOne_TieBreakerStep3SupportWinsOnDoubleTie()
    {
        var activeSup = Level(95.0, strength: 50.0, isResistance: false);
        var activeRes = Level(105.0, strength: 50.0, isResistance: true);
        var sorted = new[] { activeRes, activeSup };

        var selected = FindSupportResistanceTool.SelectLevels(sorted, activeSup, activeRes, maxLevels: 1, currentPrice: 100m);

        Assert.Single(selected);
        Assert.Same(activeSup, selected[0]);
    }

    [Fact]
    public void SelectLevels_MaxLevelsOne_CurrentPriceZeroSkipsDistanceUsesStrengthThenSupport()
    {
        var activeSup = Level(98.0, strength: 40.0, isResistance: false);
        var activeRes = Level(120.0, strength: 90.0, isResistance: true);
        var sorted = new[] { activeRes, activeSup };

        var winnerStrength = FindSupportResistanceTool.SelectLevels(sorted, activeSup, activeRes, maxLevels: 1, currentPrice: 0m);
        Assert.Single(winnerStrength);
        Assert.Same(activeRes, winnerStrength[0]);

        var tiedRes = Level(120.0, strength: 40.0, isResistance: true);
        var winnerSupport = FindSupportResistanceTool.SelectLevels(new[] { tiedRes, activeSup }, activeSup, tiedRes, maxLevels: 1, currentPrice: 0m);
        Assert.Single(winnerSupport);
        Assert.Same(activeSup, winnerSupport[0]);
    }

    [Fact]
    public void SelectLevels_MaxLevelsTwo_ReservesBothActiveBeforeStrongerInactive()
    {
        var strongerInactive = Level(130.0, strength: 99.0, isResistance: true);
        var activeSup = Level(95.0, strength: 20.0, isResistance: false);
        var activeRes = Level(105.0, strength: 30.0, isResistance: true);
        var sorted = new[] { strongerInactive, activeRes, activeSup };

        var selected = FindSupportResistanceTool.SelectLevels(sorted, activeSup, activeRes, maxLevels: 2, currentPrice: 100m);

        Assert.Equal(2, selected.Count);
        Assert.Contains(activeSup, selected);
        Assert.Contains(activeRes, selected);
        Assert.DoesNotContain(strongerInactive, selected);
    }

    [Fact]
    public void SelectLevels_BothActiveNullOrLargeMaxLevels_RespectsCapAndOrder()
    {
        var l1 = Level(120.0, strength: 80.0, isResistance: true);
        var l2 = Level(90.0, strength: 60.0, isResistance: false);
        var sorted = new[] { l1, l2 };

        var capped = FindSupportResistanceTool.SelectLevels(sorted, activeSup: null, activeRes: null, maxLevels: 1, currentPrice: 100m);
        Assert.Single(capped);
        Assert.Same(l1, capped[0]);

        var all = FindSupportResistanceTool.SelectLevels(sorted, activeSup: null, activeRes: null, maxLevels: 20, currentPrice: 100m);
        Assert.Equal(2, all.Count);
        Assert.Same(l1, all[0]);
        Assert.Same(l2, all[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public async Task FindSupportResistance_EndToEnd_RespectsMaxLevelsAndPreservesActiveWhenSlotsAllow(int maxLevels)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using JsonDocument? response = await McpTestSupport.CallAsync(
            dispatcher,
            "sa_find_support_resistance",
            $$"""{"symbol":"{{SrSymbol}}","timeFrame":"D1","maxLevels":{{maxLevels}}}""");

        Assert.NotNull(response);
        Assert.False(McpTestSupport.IsError(response!), McpTestSupport.Structured(response!).ToString());
        JsonElement result = McpTestSupport.Structured(response!);

        JsonElement[] levels = result.GetProperty("levels").EnumerateArray().ToArray();
        Assert.InRange(levels.Length, 0, maxLevels);

        bool hasSup = result.GetProperty("activeSupport").ValueKind != JsonValueKind.Null;
        bool hasRes = result.GetProperty("activeResistance").ValueKind != JsonValueKind.Null;
        if (maxLevels >= 2)
        {
            if (hasSup)
            {
                decimal sup = result.GetProperty("activeSupport").GetDecimal();
                Assert.Contains(levels, l => l.GetProperty("kind").GetString() == "Support" && l.GetProperty("price").GetDecimal() == sup);
            }

            if (hasRes)
            {
                decimal res = result.GetProperty("activeResistance").GetDecimal();
                Assert.Contains(levels, l => l.GetProperty("kind").GetString() == "Resistance" && l.GetProperty("price").GetDecimal() == res);
            }
        }
    }

    // ---- E3: sa_get_capabilities registeredOnly ---------------------------------------------------------------

    [Fact]
    public async Task GetCapabilities_RegisteredOnlyFalseMatchesOmittedAndTrueFiltersUnregistered()
    {
        var factory = new IndicatorFactory();
        var screenerProvider = new ScreenerCatalogProvider();
        var catalog = new IndicatorCatalog(factory, screenerProvider);
        var screenerCatalog = new ScreenerCatalog(screenerProvider, factory);
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { new GetCapabilitiesTool(catalog, screenerCatalog) });

        using JsonDocument? omittedResp = await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", "{}");
        using JsonDocument? falseResp = await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"registeredOnly":false}""");
        using JsonDocument? trueResp = await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", """{"registeredOnly":true}""");

        Assert.Equal(
            McpTestSupport.Structured(omittedResp!).GetRawText(),
            McpTestSupport.Structured(falseResp!).GetRawText());

        JsonElement trueRoot = McpTestSupport.Structured(trueResp!);
        JsonElement[] indicators = trueRoot.GetProperty("indicators").EnumerateArray().ToArray();
        Assert.Equal(indicators.Length, trueRoot.GetProperty("indicatorCount").GetInt32());
        Assert.All(indicators, i => Assert.True(i.GetProperty("isRegistered").GetBoolean()));

        int expectedRegistered = catalog.Entries.Count(e => e.IsRegistered);
        Assert.Equal(expectedRegistered, indicators.Length);
        Assert.True(McpTestSupport.Structured(omittedResp!).GetProperty("indicatorCount").GetInt32() >= expectedRegistered);
    }
}
