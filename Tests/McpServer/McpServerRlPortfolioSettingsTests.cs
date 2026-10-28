using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The server reads the same "RlPortfolio" section as the GUI and validates it at start-up (hard band stops the start, standard band is logged);
/// the portfolio preflight and the execution gateway's portfolio runner use those settings, not the built-in defaults.
/// </summary>
public sealed class McpServerRlPortfolioSettingsTests : IDisposable
{
    private const int StoredDays = 1_400;
    private const string FirstDay = "2000-01-01";
    private const string EndDateExclusive = "2003-11-01";   // 1,400 days after the first day
    private const string SectionPrefix = RlPortfolioSettings.SectionName + ":";
    private static readonly string[] Symbols = { "PRA", "PRB", "PRC" };
    private static readonly string SymbolsJson = JsonSerializer.Serialize(Symbols);
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1, Symbols);

    public void Dispose() => _fixture.Dispose();

    private void WriteEquity(string symbol) => McpTestSupport.WriteEquity(_fixture, symbol, FirstDay, StoredDays);

    private IConfiguration Configuration(params (string Key, string Value)[] portfolio)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in portfolio) values[SectionPrefix + key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>The structured result of the portfolio preflight for the fixture's symbols.</summary>
    private static async Task<JsonDocument> PreflightAsync(McpServerRuntime runtime)
    {
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        JsonDocument response = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_check_portfolio_rl_preflight",
            $$"""{"symbols":{{SymbolsJson}},"startDate":"{{FirstDay}}","endDateExclusive":"{{EndDateExclusive}}","mode":"ShortHistory"}"""))!;
        Assert.False(McpTestSupport.IsError(response));
        return response;
    }

    private static async Task<bool> FeasibleAsync(McpServerRuntime runtime)
    {
        using JsonDocument response = await PreflightAsync(runtime);
        return McpTestSupport.Structured(response).GetProperty("feasible").GetBoolean();
    }

    /// <summary>More bars than the 3:1:1 train share of the stored period holds, and still inside the hard band.</summary>
    private static string TrainBarsBeyondThePeriod => (StoredDays * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task PortfolioPreflight_UsesTheConfiguredSection_NotTheDefaults()
    {
        foreach (string symbol in Symbols) WriteEquity(symbol);

        using (McpServerRuntime defaults = McpServerComposition.Create(Configuration(), new LogSink(new StringWriter())))
        {
            Assert.True(await FeasibleAsync(defaults));
        }

        using McpServerRuntime configured = McpServerComposition.Create(
            Configuration((nameof(RlPortfolioSettings.MinimumTrainBars), TrainBarsBeyondThePeriod)), new LogSink(new StringWriter()));
        Assert.False(await FeasibleAsync(configured));
    }

    [Fact]
    public async Task PortfolioPreflight_StartGateUsesTheConfiguredSymbolMaximum()
    {
        foreach (string symbol in Symbols) WriteEquity(symbol);
        string tooMany = nameof(RlPanelStartBlocker.TooManySymbols);
        string none = nameof(RlPanelStartBlocker.None);

        using (McpServerRuntime defaults = McpServerComposition.Create(Configuration(), new LogSink(new StringWriter())))
        using (JsonDocument response = await PreflightAsync(defaults))
        {
            JsonElement result = McpTestSupport.Structured(response);
            Assert.Equal(none, result.GetProperty("blocker").GetString());
            Assert.True(result.GetProperty("canStart").GetBoolean());
        }

        using McpServerRuntime configured = McpServerComposition.Create(
            Configuration((nameof(RlPortfolioSettings.MaxSymbols), (Symbols.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new LogSink(new StringWriter()));
        using JsonDocument limited = await PreflightAsync(configured);
        JsonElement gated = McpTestSupport.Structured(limited);
        Assert.Equal(tooMany, gated.GetProperty("blocker").GetString());
        Assert.False(gated.GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public async Task ExecutionPortfolioRunner_UsesTheGivenSettings_NotTheDefaults()
    {
        foreach (string symbol in Symbols) WriteEquity(symbol);
        var folders = RlLocalSourceFolders.FromSettings(_fixture.Settings);
        DateOnly start = DateOnly.Parse(FirstDay);
        DateOnly end = DateOnly.Parse(EndDateExclusive);

        PanelPreflight byDefault = await new EphemeralPortfolioRlRunner(null, folders)
            .PreflightAsync(Symbols, start, end, holdoutRequested: false, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        PanelPreflight configured = await new EphemeralPortfolioRlRunner(
                new RlPortfolioSettings { MinimumTrainBars = StoredDays * 2 }, folders)
            .PreflightAsync(Symbols, start, end, holdoutRequested: false, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);

        Assert.True(byDefault.Feasible);
        Assert.False(configured.Feasible);
    }

    [Fact]
    public void ValueOutsideTheHardBand_StopsTheStartUpAndNamesTheKey()
    {
        var error = Assert.Throws<ExecutionLimitsException>(() => McpServerComposition.Create(
            Configuration((nameof(RlPortfolioSettings.MaxSymbols), "1")), new LogSink(new StringWriter())));

        Assert.Contains(SectionPrefix + nameof(RlPortfolioSettings.MaxSymbols), error.Message);
    }

    [Fact]
    public void ValueOutsideTheStandardBand_RunsAndIsLoggedToTheSink()
    {
        var log = new StringWriter();

        using McpServerRuntime runtime = McpServerComposition.Create(
            Configuration((nameof(RlPortfolioSettings.HoldoutPercent), RlPortfolioLimits.HoldoutPercent.HardMax.ToString())), new LogSink(log));

        Assert.Contains(SectionPrefix + nameof(RlPortfolioSettings.HoldoutPercent), log.ToString());
    }

    [Fact]
    public void DefaultSection_LogsNoPortfolioWarning()
    {
        var log = new StringWriter();

        using McpServerRuntime runtime = McpServerComposition.Create(Configuration(), new LogSink(log));

        Assert.DoesNotContain(SectionPrefix, log.ToString());
    }
}
