using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_get_active_workspace and the resource stockanalyzer://workspace/active (owner decision U-10) through the real composition: the workspace and the registered indicators are saved by the
/// application's own services, the answer is read back, and its <c>request</c> is sent to sa_calculate_indicators unchanged.
/// </summary>
public sealed class ActiveWorkspaceToolTests : IDisposable
{
    private static readonly IndicatorFactory Factory = new();
    private static readonly ScreenerCatalogProvider CatalogProvider = new();

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(300, "7203.T", "MSFT");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_active_workspace_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();

    public ActiveWorkspaceToolTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string WorkspacePath => Path.Combine(_root, "default_workspace.json");

    private string SourcesPath => Path.Combine(_root, "source_indicators.json");

    private string DriversPath => Path.Combine(_root, "dynamic_period_drivers.json");

    private sealed class UnavailableWorkspaceSource : IReadOnlyWorkspaceSource
    {
        public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => throw new DataUnavailableException("being replaced");
    }

    private static CoreIndicatorSettings Indicator(IndicatorType type, bool enabled = true, Action<object>? shape = null)
    {
        CoreIndicatorSettings settings = CatalogProvider.GetDefaultSettings(type, Factory)!;
        settings.IsEnabled = enabled;
        shape?.Invoke(settings.ParameterObject!);
        return settings;
    }

    private static void Set(object parameters, string name, object value)
        => parameters.GetType().GetProperty(name)!.SetValue(parameters, value);

    private async Task SaveWorkspaceAsync(string? timeframe, params CoreIndicatorSettings[] indicators)
    {
        var workspace = new WorkspaceSettings { SelectedTicker = "7203-T", SelectedTimeframe = timeframe, MaxCandleCount = 250, ChartType = "Candlestick" };
        workspace.Indicators.AddRange(indicators);
        using var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, WorkspacePath);
    }

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyWorkspaceSource? workspaceSource = null, params (string Key, string Value)[] limits)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limits)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            workspaceSource: workspaceSource ?? new FileReadOnlyWorkspaceSource(WorkspacePath),
            registeredIndicatorSource: new FileReadOnlyRegisteredIndicatorSource(SourcesPath, DriversPath));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    // ---- the answer and its request -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSavedChartSetUp_IsReturnedWithItsActiveIndicatorsAndAReadyRequestThatSa_Calculate_IndicatorsAccepts()
    {
        CoreIndicatorSettings sma = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 7));
        CoreIndicatorSettings rsi = Indicator(IndicatorType.RSI);
        CoreIndicatorSettings macd = Indicator(IndicatorType.MACD, enabled: false);
        await SaveWorkspaceAsync("Weekly", sma, rsi, macd);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).GetRawText());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("workspaceFound").GetBoolean());
        Assert.Equal("7203-T", result.GetProperty("symbol").GetString());
        Assert.Equal("Weekly", result.GetProperty("savedTimeFrame").GetString());
        Assert.Equal("W1", result.GetProperty("timeFrame").GetString());
        Assert.Equal(250, result.GetProperty("maxCandleCount").GetInt32());
        JsonElement[] active = result.GetProperty("activeIndicators").EnumerateArray().ToArray();
        Assert.Equal(new[] { "SMA", "RSI", "MACD" }, active.Select(a => a.GetProperty("type").GetString()));
        Assert.Equal(new[] { true, true, false }, active.Select(a => a.GetProperty("isEnabled").GetBoolean()));
        Assert.Equal(7, active[0].GetProperty("parameters").GetProperty("Period").GetInt32());
        Assert.All(active, a => Assert.True(a.GetProperty("isCalculable").GetBoolean()));
        Assert.Contains("saved", result.GetProperty("notes")[0].GetString(), StringComparison.OrdinalIgnoreCase);

        JsonElement request = result.GetProperty("request");
        Assert.Equal(new[] { sma.Id, rsi.Id }, request.GetProperty("indicators").EnumerateArray().Select(i => i.GetProperty("id").GetString())); // the disabled MACD is not requested
        Assert.False(request.GetProperty("indicators")[0].TryGetProperty("isEnabled", out _));

        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", request.GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
        JsonElement results = McpTestSupport.Structured(calculated).GetProperty("results");
        Assert.True(results.GetProperty(sma.Id).GetProperty("isSuccessful").GetBoolean());
        Assert.True(results.GetProperty(rsi.Id).GetProperty("isSuccessful").GetBoolean());
        Assert.Equal("W1", McpTestSupport.Structured(calculated).GetProperty("timeFrame").GetString());
    }

    [Fact]
    public async Task AnIndicatorThatReadsARegisteredSourceIndicator_BringsItIntoTheRequestFirst_WithTheSeriesTheApplicationReads()
    {
        CoreIndicatorSettings source = Indicator(IndicatorType.RSI, shape: p => Set(p, "Period", 5));
        using (var service = new SourceIndicatorService(SourcesPath))
        {
            await service.SaveSourceIndicatorAsync(source);
        }

        CoreIndicatorSettings downstream = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3));
        downstream.SourceIndicatorId = source.Id;
        await SaveWorkspaceAsync("Daily", downstream);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        JsonElement[] indicators = result.GetProperty("request").GetProperty("indicators").EnumerateArray().ToArray();
        Assert.Equal(new[] { source.Id, downstream.Id }, indicators.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(source.Id, indicators[1].GetProperty("sourceIndicatorId").GetString());
        Assert.False(indicators[1].TryGetProperty("sourceOutputSeriesName", out _)); // the application's "Main" is the main series: the argument is left out
        Assert.Equal(1, result.GetProperty("activeIndicators").GetArrayLength()); // the registered one is not a chart indicator

        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", result.GetProperty("request").GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
        Assert.True(McpTestSupport.Structured(calculated).GetProperty("results").GetProperty(downstream.Id).GetProperty("isSuccessful").GetBoolean());
    }

    [Fact]
    public async Task AnUpstreamIndicatorThatHasSelectedAnotherOutputSeries_NamesItInTheRequest()
    {
        CoreIndicatorSettings macd = Indicator(IndicatorType.MACD);
        macd.OutputSeriesName = "Signal";
        CoreIndicatorSettings downstream = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3));
        downstream.SourceIndicatorId = macd.Id;
        await SaveWorkspaceAsync("Daily", macd, downstream);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement[] indicators = McpTestSupport.Structured(response).GetProperty("request").GetProperty("indicators").EnumerateArray().ToArray();
        Assert.Equal(new[] { macd.Id, downstream.Id }, indicators.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal("Signal", indicators[1].GetProperty("sourceOutputSeriesName").GetString());
        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", McpTestSupport.Structured(response).GetProperty("request").GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
        Assert.True(McpTestSupport.Structured(calculated).GetProperty("results").GetProperty(downstream.Id).GetProperty("isSuccessful").GetBoolean());
    }

    [Fact]
    public async Task AnIndicatorWhoseInputCannotBeFound_IsLeftOutOfTheRequestWithANote_AndTheOthersStay()
    {
        CoreIndicatorSettings orphan = Indicator(IndicatorType.SMA);
        orphan.SourceIndicatorId = Guid.NewGuid().ToString();
        CoreIndicatorSettings fine = Indicator(IndicatorType.RSI);
        await SaveWorkspaceAsync("Daily", orphan, fine);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(new[] { fine.Id }, result.GetProperty("request").GetProperty("indicators").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(orphan.Id, StringComparison.Ordinal));
        Assert.Equal(2, result.GetProperty("activeIndicators").GetArrayLength());
    }

    [Fact]
    public async Task ASavedTimeFrameTheToolsDoNotServe_HasNoRequestAndSaysSo()
    {
        await SaveWorkspaceAsync("H1", Indicator(IndicatorType.RSI));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("timeFrame").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("request").ValueKind);
        Assert.Equal("H1", result.GetProperty("savedTimeFrame").GetString());
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("H1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoreIndicatorsThanOneCallTakes_HaveNoRequest_ButAreListed()
    {
        await SaveWorkspaceAsync("Daily", Indicator(IndicatorType.RSI), Indicator(IndicatorType.SMA));
        McpMessageDispatcher dispatcher = await StartAsync(limits: ("MaxIndicatorsPerCall", "1"));

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("request").ValueKind);
        Assert.Equal(2, result.GetProperty("activeIndicators").GetArrayLength());
    }

    [Fact]
    public async Task NoSavedWorkspace_IsWorkspaceFoundFalse_AndNothingIsCreated()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        Assert.False(McpTestSupport.Structured(response).GetProperty("workspaceFound").GetBoolean());
        Assert.False(File.Exists(WorkspacePath));
        Assert.False(File.Exists(SourcesPath));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AWorkspaceBeingReplacedOrADamagedRegisteredFile_IsDataUnavailable_AndNoFileIsCreatedOrQuarantined()
    {
        File.WriteAllText(SourcesPath, "{ this is not a list");
        await SaveWorkspaceAsync("Daily", Indicator(IndicatorType.RSI));
        McpMessageDispatcher damaged = await StartAsync();
        McpMessageDispatcher replaced = await StartAsync(new UnavailableWorkspaceSource());
        string[] before = Directory.GetFiles(_root).OrderBy(f => f).ToArray();

        using JsonDocument damagedResponse = await CallAsync(damaged, "sa_get_active_workspace");
        using JsonDocument replacedResponse = await CallAsync(replaced, "sa_get_active_workspace");

        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.ErrorCodeOf(damagedResponse));
        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.ErrorCodeOf(replacedResponse));
        Assert.Equal(before, Directory.GetFiles(_root).OrderBy(f => f).ToArray()); // no .corrupted copy
        Assert.Equal("{ this is not a list", File.ReadAllText(SourcesPath));
    }

    // ---- the Timeframe of an indicator (Indicator Timeframe, T7) -------------------------------------------------------------------

    private static CoreIndicatorSettings WithTimeframe(CoreIndicatorSettings settings, IndicatorTimeframe timeframe)
    {
        settings.Timeframe = timeframe;
        return settings;
    }

    private static JsonElement RequestEntry(JsonElement result, string id)
        => result.GetProperty("request").GetProperty("indicators").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    [Fact]
    public async Task AWeeklyIndicatorOnADailyWorkspace_CarriesItsOwnW1Frame_AndTheRequestIsAccepted()
    {
        CoreIndicatorSettings weekly = WithTimeframe(Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3)), IndicatorTimeframe.Weekly);
        CoreIndicatorSettings monthly = WithTimeframe(Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 2)), IndicatorTimeframe.Monthly);
        CoreIndicatorSettings same = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 5));
        await SaveWorkspaceAsync("Daily", weekly, monthly, same);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal("W1", RequestEntry(result, weekly.Id).GetProperty("timeFrame").GetString());
        Assert.Equal("MN1", RequestEntry(result, monthly.Id).GetProperty("timeFrame").GetString());
        Assert.False(RequestEntry(result, same.Id).TryGetProperty("timeFrame", out _));
        Assert.All(result.GetProperty("activeIndicators").EnumerateArray(), a => Assert.False(a.TryGetProperty("timeFrame", out _))); // the list of the workspace states the setting elsewhere

        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", result.GetProperty("request").GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
        JsonElement results = McpTestSupport.Structured(calculated).GetProperty("results");
        Assert.True(results.GetProperty(weekly.Id).GetProperty("isSuccessful").GetBoolean());
        Assert.Equal("W1", results.GetProperty(weekly.Id).GetProperty("timeFrame").GetString());
        Assert.Equal("MN1", results.GetProperty(monthly.Id).GetProperty("timeFrame").GetString());
        Assert.False(results.GetProperty(same.Id).TryGetProperty("alignment", out _));
    }

    [Theory]
    [InlineData("Weekly", IndicatorTimeframe.Weekly, null)]      // the frame of the call itself: no own frame
    [InlineData("Monthly", IndicatorTimeframe.Monthly, null)]
    [InlineData("Daily", IndicatorTimeframe.Weekly, "W1")]
    [InlineData("Daily", IndicatorTimeframe.Monthly, "MN1")]
    public async Task TheOwnFrameIsWrittenOnlyWhenItIsCoarserThanTheWorkspaceFrame(string workspaceFrame, IndicatorTimeframe timeframe, string? expected)
    {
        CoreIndicatorSettings sma = WithTimeframe(Indicator(IndicatorType.SMA), timeframe);
        await SaveWorkspaceAsync(workspaceFrame, sma);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement entry = RequestEntry(McpTestSupport.Structured(response), sma.Id);
        Assert.Equal(expected, entry.TryGetProperty("timeFrame", out JsonElement frame) ? frame.GetString() : null);
    }

    // Q-2: whatever the application's own time-frame rule refuses (IndicatorTimeframePlan.IsRefused) is not in the request, and a note carries the application's text.
    [Theory]
    [InlineData("Weekly", IndicatorTimeframe.Monthly, "Monthly indicator timeframe requires a Daily chart and cannot be calculated from Weekly bars.")]
    [InlineData("Monthly", IndicatorTimeframe.Weekly, "is finer than the chart timeframe MN1")]
    [InlineData("Monthly", IndicatorTimeframe.Monthly, null)] // the chart's own frame is not refused
    public async Task AnIndicatorTheApplicationRefusesOnTheWorkspaceFrame_IsLeftOutOfTheRequest_WithANote_AndTheRequestStillRuns(string workspaceFrame, IndicatorTimeframe timeframe, string? expectedNoteText)
    {
        CoreIndicatorSettings candidate = WithTimeframe(Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 2)), timeframe);
        CoreIndicatorSettings plain = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3));
        await SaveWorkspaceAsync(workspaceFrame, candidate, plain);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        string[] ids = result.GetProperty("request").GetProperty("indicators").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        string[] notes = result.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Contains(plain.Id, ids);
        Assert.Contains(result.GetProperty("activeIndicators").EnumerateArray(), a => a.GetProperty("id").GetString() == candidate.Id); // the list of the workspace still shows it
        if (expectedNoteText is null)
        {
            Assert.Contains(candidate.Id, ids);
            Assert.DoesNotContain(notes, n => n.Contains(candidate.Id));
        }
        else
        {
            Assert.DoesNotContain(candidate.Id, ids);
            Assert.Contains(notes, n => n.Contains(candidate.Id) && n.Contains("not in the request") && n.Contains(expectedNoteText));
        }

        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", result.GetProperty("request").GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
    }

    [Fact]
    public async Task APivotWhoseSourcePeriodIsFinerThanTheWorkspaceFrame_IsLeftOutLikeAnyOtherRefusedIndicator()
    {
        CoreIndicatorSettings pivot = Indicator(IndicatorType.ClassicPivotPoints, shape: p => Set(p, "SourcePeriod", PivotSourcePeriod.Week));
        CoreIndicatorSettings plain = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3));
        await SaveWorkspaceAsync("Monthly", pivot, plain);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        string[] ids = result.GetProperty("request").GetProperty("indicators").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(new[] { plain.Id }, ids);
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(pivot.Id) && n.GetString()!.Contains("not in the request"));
    }

    [Fact]
    public async Task WhenTheOnlyIndicatorIsRefused_TheRequestIsNull_AndANoteSaysWhy()
    {
        CoreIndicatorSettings monthly = WithTimeframe(Indicator(IndicatorType.SMA), IndicatorTimeframe.Monthly);
        await SaveWorkspaceAsync("Weekly", monthly);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("request").ValueKind);
        Assert.Contains(result.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(monthly.Id));
    }

    [Fact]
    public async Task PivotAndTwoSymbolIndicators_NeverCarryAnOwnFrame_AndThePivotKeepsItsSourcePeriodParameter()
    {
        CoreIndicatorSettings pivot = Indicator(IndicatorType.ClassicPivotPoints, shape: p => Set(p, "SourcePeriod", PivotSourcePeriod.Week));
        CoreIndicatorSettings correlation = WithTimeframe(Indicator(IndicatorType.Correlation), IndicatorTimeframe.Weekly);
        await SaveWorkspaceAsync("Daily", pivot, correlation);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        JsonElement pivotEntry = RequestEntry(result, pivot.Id);
        Assert.False(pivotEntry.TryGetProperty("timeFrame", out _));
        Assert.Equal("Week", pivotEntry.GetProperty("parameters").GetProperty("SourcePeriod").GetString());
        foreach (JsonElement entry in result.GetProperty("request").GetProperty("indicators").EnumerateArray())
        {
            Assert.False(entry.TryGetProperty("timeFrame", out _), entry.GetRawText());
        }
    }

    [Fact]
    public async Task AChainedIndicator_NeverCarriesAnOwnFrame()
    {
        CoreIndicatorSettings upstream = WithTimeframe(Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 3)), IndicatorTimeframe.Weekly);
        CoreIndicatorSettings downstream = Indicator(IndicatorType.EMA, shape: p => Set(p, "Period", 3));
        downstream.SourceIndicatorId = upstream.Id;
        downstream.Timeframe = IndicatorTimeframe.Weekly; // a hand-edited file: the chain wins
        await SaveWorkspaceAsync("Daily", upstream, downstream);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(RequestEntry(result, upstream.Id).TryGetProperty("timeFrame", out _));
        Assert.False(RequestEntry(result, downstream.Id).TryGetProperty("timeFrame", out _));
        using JsonDocument calculated = await CallAsync(dispatcher, "sa_calculate_indicators", result.GetProperty("request").GetRawText());
        Assert.False(McpTestSupport.IsError(calculated), McpTestSupport.Structured(calculated).GetRawText());
    }

    // ---- the resource --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheResource_ServesTheSameDocumentAsTheTool_AndIsListed()
    {
        await SaveWorkspaceAsync("Monthly", Indicator(IndicatorType.RSI));
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument list = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}""", CancellationToken.None))!);
        using JsonDocument read = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"stockanalyzer://workspace/active"}}""", CancellationToken.None))!);
        using JsonDocument tool = await CallAsync(dispatcher, "sa_get_active_workspace");

        Assert.Contains(list.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray(), r => r.GetProperty("uri").GetString() == "stockanalyzer://workspace/active");
        string text = read.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        Assert.Equal(McpTestSupport.Structured(tool).GetRawText(), text);
        Assert.Equal("MN1", JsonDocument.Parse(text).RootElement.GetProperty("timeFrame").GetString());
    }

    [Fact]
    public async Task TheResource_OfAnUnreadableWorkspace_IsARetryableProtocolError()
    {
        McpMessageDispatcher dispatcher = await StartAsync(new UnavailableWorkspaceSource());

        using JsonDocument read = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"stockanalyzer://workspace/active"}}""", CancellationToken.None))!);

        Assert.Equal(-32603, read.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(read.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task PerEdgeOutputSeriesNames_OverrideUpstreamOutputSeriesNameInWorkspaceRequest()
    {
        CoreIndicatorSettings macd = Indicator(IndicatorType.MACD);
        macd.OutputSeriesName = IndicatorResult.MainSeriesName;
        CoreIndicatorSettings consumer = Indicator(IndicatorType.SMA, shape: p => Set(p, "Period", 5));
        consumer.SourceIndicatorId = macd.Id;
        consumer.SourceOutputSeriesName = "Signal";
        consumer.DynamicPeriodIndicatorId = macd.Id;
        consumer.DynamicPeriodOutputSeriesName = "Histogram";

        await SaveWorkspaceAsync("Daily", macd, consumer);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_active_workspace");
        JsonElement result = McpTestSupport.Structured(response);
        JsonElement entry = RequestEntry(result, consumer.Id);

        Assert.Equal("Signal", entry.GetProperty("sourceOutputSeriesName").GetString());
        Assert.Equal("Histogram", entry.GetProperty("dynamicPeriodOutputSeriesName").GetString());
    }
}
