using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_run_backtest through the real composition on a throw-away data tree. The stored series alternates up (+1.0) and down (-0.5) closes from
/// 2024-01-01, so "close above the previous close" opens a position every other day and "close below the previous close" closes it.
/// The parity test runs the same inputs through the application's preparation and evaluation services directly.
/// </summary>
public sealed class RunBacktestToolTests : IDisposable
{
    private const int Days = 400;
    private const string Entry = """{"left":{"price":"Close"},"comparison":"GreaterThan","right":{"price":"Close","offset":1}}""";
    private const string Exit = """{"left":{"price":"Close"},"comparison":"LessThan","right":{"price":"Close","offset":1}}""";
    private const string Period = ",\"startDate\":\"2024-02-01\",\"endDate\":\"2024-12-31\"";

    private static readonly string Conditions = $$"""{"entryLong":{{Entry}},"exitLong":{{Exit}}}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "7203.T");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in overrides)
        {
            values[key.Contains(':') ? key : $"AiIntegration:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] overrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(overrides), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RunAsync(McpMessageDispatcher dispatcher, string conditions, string extra = "", string symbol = "MSFT")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", $$"""{"symbol":"{{symbol}}","conditions":{{conditions}}{{extra}}}"""))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string Code(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    private static string Param(JsonDocument response) => McpTestSupport.Structured(response).GetProperty("param").GetString()!;

    private static string RuleOf(string comparison, int offset = 0, string right = "1") => $$"""{"left":{"price":"Close","offset":{{offset}}},"comparison":"{{comparison}}","right":{{right}}}""";

    // ---- result contract -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARunReturnsTheReportTheTradesTheFingerprintAndEveryEffectiveSetting()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, Conditions, Period));

        Assert.Equal("MSFT", result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal("Legacy", result.GetProperty("executionModel").GetString());
        Assert.Equal("Completed", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("isInsufficientData").GetBoolean());
        Assert.Equal("2024-02-01", result.GetProperty("period").GetProperty("startDate").GetString());
        Assert.False(result.GetProperty("period").GetProperty("datesFromStoredHistory").GetBoolean());
        Assert.True(result.GetProperty("period").GetProperty("warmUpBarCount").GetInt32() > 0);
        Assert.True(result.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32() > 0);

        JsonElement fingerprint = result.GetProperty("runFingerprint");
        Assert.True(fingerprint.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(64, fingerprint.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(64, result.GetProperty("reproducibilityHash").GetString()!.Length);

        JsonElement report = result.GetProperty("report");
        Assert.Equal("Valid", report.GetProperty("totalPnL").GetProperty("status").GetString());
        Assert.Equal("Currency", report.GetProperty("totalPnL").GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Number, report.GetProperty("totalPnL").GetProperty("value").ValueKind);
        Assert.True(report.GetProperty("totalTrades").GetInt32() > 0);
        Assert.Equal("D1", report.GetProperty("frame").GetString());
        Assert.False(report.TryGetProperty("runFingerprint", out _));

        JsonElement account = result.GetProperty("effectiveSettings").GetProperty("account");
        Assert.Equal(BacktestDefaults.InitialCapital, account.GetProperty("initialCapital").GetDecimal());
        Assert.Equal("FixedQuantity", account.GetProperty("sizingModel").GetString());
        Assert.Equal(BacktestDefaults.LiquidationPenaltyRatio, account.GetProperty("liquidationPenaltyRatio").GetDecimal());
        Assert.Equal(252, result.GetProperty("effectiveSettings").GetProperty("report").GetProperty("annualPeriods").GetInt32());
        Assert.Equal(BacktestDefaults.InitialCapital, result.GetProperty("equity").GetProperty("initial").GetDecimal());

        JsonElement roots = result.GetProperty("conditionRoots");
        Assert.Equal(new[] { "EntryLong", "ExitLong" }, roots.EnumerateArray().Select(r => r.GetProperty("root").GetString()).ToArray());
        Assert.Equal("Close > Close [1 bars back]", roots[0].GetProperty("text").GetString());
        Assert.False(result.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task TheResultEqualsTheApplicationsPreparationAndEvaluationServicesCalledDirectly()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await RunAsync(dispatcher, Conditions, Period, " msft "));

        using var db = new DuckDBConnectionManager();
        IOptions<MarketDataSettings> options = Options.Create(_fixture.Settings);
        var preparation = new BacktestRunPreparationService(new ParquetDataService(db, options));
        var evaluation = new BacktestEvaluationService(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy());
        BacktestConditionSide Close(int offset) => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, OutputName = IndicatorResult.MainSeriesName, Offset = offset };
        BacktestConditionGroup Group(ComparisonOperator op) => new(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(
            new BacktestConditionEntry { Left = Close(0), Operator = op, TargetMode = RightHandTargetMode.Indicator, Right = Close(1) })));
        var tree = new BacktestConditionTree(
            Group(ComparisonOperator.GreaterThan), BacktestConditionGroup.EmptyAnd, Group(ComparisonOperator.LessThan),
            BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);
        var snapshot = new BacktestRunSnapshot(
            "MSFT", TimeFrame.D1, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            new BacktestConfiguration
            {
                InitialCapital = BacktestDefaults.InitialCapital,
                CommissionFlat = BacktestDefaults.CommissionFlat,
                CommissionPerUnit = BacktestDefaults.CommissionPerUnit,
                SlippageRatio = BacktestDefaults.SlippageRatio,
                SizingModel = BacktestDefaults.SizingModel,
                SizingParameter = BacktestDefaults.SizingParameter,
                InitialMarginRatio = BacktestDefaults.InitialMarginRatio,
                MaintenanceMarginRatio = BacktestDefaults.MaintenanceMarginRatio,
                LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio,
            },
            ImmutableArray<BacktestConditionEntry>.Empty, null, ImmutableArray<StrategyIndicatorRequest>.Empty, BacktestReportDefaults.BuiltIn,
            BacktestDefaults.BootstrapSeed, BacktestDefaults.BootstrapIterations, tree);
        PreparedBacktestRun prepared = await preparation.PrepareAsync(snapshot, CancellationToken.None);
        BacktestEvaluationArtifact direct = evaluation.Evaluate(prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions);

        Assert.Equal(direct.RunFingerprint.Sha256, result.GetProperty("runFingerprint").GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexString(direct.Result.ReproducibilityHash), result.GetProperty("reproducibilityHash").GetString());
        Assert.Equal(direct.Report!.TotalTrades, result.GetProperty("report").GetProperty("totalTrades").GetInt32());
        Assert.Equal(direct.Report.TotalPnL.Value, result.GetProperty("report").GetProperty("totalPnL").GetProperty("value").GetDecimal());
        Assert.Equal(direct.Result.Trades.Length, result.GetProperty("trades").GetProperty("totalTrades").GetInt32());
        Assert.Equal(direct.Result.EquityPoints[^1].Equity, result.GetProperty("equity").GetProperty("final").GetDecimal());
    }

    [Fact]
    public async Task OmittedDatesAreTheStoredHistoryAndAreEchoed()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, Conditions));

        JsonElement period = result.GetProperty("period");
        Assert.True(period.GetProperty("datesFromStoredHistory").GetBoolean());
        Assert.Equal("2024-01-01", period.GetProperty("startDate").GetString());
        Assert.Equal(new DateTime(2024, 1, 1).AddDays(Days - 1).ToString("yyyy-MM-dd"), period.GetProperty("endDate").GetString());
        Assert.Equal(0, period.GetProperty("warmUpBarCount").GetInt32());
    }

    [Fact]
    public async Task TheTradesAreListedInOrderAndCappedByMaxReturnedTrades()
    {
        var dispatcher = await StartAsync();

        JsonElement all = Result(await RunAsync(dispatcher, Conditions, Period)).GetProperty("trades");
        JsonElement one = Result(await RunAsync(dispatcher, Conditions, Period + ",\"maxReturnedTrades\":1")).GetProperty("trades");
        JsonElement none = Result(await RunAsync(dispatcher, Conditions, Period + ",\"maxReturnedTrades\":0")).GetProperty("trades");

        Assert.False(all.GetProperty("truncated").GetBoolean());
        Assert.Equal(all.GetProperty("totalTrades").GetInt32(), all.GetProperty("items").GetArrayLength());
        Assert.True(all.GetProperty("totalTrades").GetInt32() > 2);
        Assert.True(one.GetProperty("truncated").GetBoolean());
        Assert.Equal(all.GetProperty("totalTrades").GetInt32(), one.GetProperty("totalTrades").GetInt32());
        Assert.Equal(all.GetProperty("items")[0].GetRawText(), one.GetProperty("items")[0].GetRawText());
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
        JsonElement first = all.GetProperty("items")[0];
        Assert.Equal("Long", first.GetProperty("side").GetString());
        Assert.Equal(JsonValueKind.Number, first.GetProperty("closedNet").ValueKind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", first.GetProperty("entryDate").GetString());
    }

    [Fact]
    public async Task AMaxReturnedTradesAboveTheLimitIsRefusedAndNegativeIsInvalid()
    {
        var dispatcher = await StartAsync(("MaxReturnedTrades", "5"));

        using JsonDocument above = await RunAsync(dispatcher, Conditions, Period + ",\"maxReturnedTrades\":6");
        using JsonDocument negative = await RunAsync(dispatcher, Conditions, Period + ",\"maxReturnedTrades\":-1");

        Assert.Equal("LIMIT_EXCEEDED", Code(above));
        Assert.Equal("maxReturnedTrades", Param(above));
        Assert.Equal("INVALID_ARGUMENT", Code(negative));
    }

    [Fact]
    public async Task EngineWarningsAreCountedByCodeAndListedUpToTheLimit()
    {
        var dispatcher = await StartAsync(("MaxReturnedWarnings", "1"));

        JsonElement warnings = Result(await RunAsync(dispatcher, Conditions, Period)).GetProperty("engineWarnings");

        int total = warnings.GetProperty("total").GetInt32();
        Assert.Equal(total, warnings.GetProperty("byCode").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.True(warnings.GetProperty("items").GetArrayLength() <= 1);
    }

    // ---- account, risk, report ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountRiskAndReportSettingsAreAppliedAndEchoed()
    {
        var dispatcher = await StartAsync();

        JsonElement baseline = Result(await RunAsync(dispatcher, Conditions, Period));
        JsonElement result = Result(await RunAsync(
            dispatcher,
            Conditions,
            Period + ",\"account\":{\"initialCapital\":250000,\"commissionFlat\":1.5,\"slippageRatio\":0.001,\"sizingModel\":\"PercentOfEquity\",\"sizingParameter\":0.5}," +
            "\"riskManagement\":{\"stopLossRatio\":0.03,\"takeProfitRatio\":0.06}," +
            "\"report\":{\"annualRiskFreeRate\":0.02,\"annualMar\":0.01}"));

        JsonElement settings = result.GetProperty("effectiveSettings");
        Assert.Equal(250000m, settings.GetProperty("account").GetProperty("initialCapital").GetDecimal());
        Assert.Equal(1.5m, settings.GetProperty("account").GetProperty("commissionFlat").GetDecimal());
        Assert.Equal("PercentOfEquity", settings.GetProperty("account").GetProperty("sizingModel").GetString());
        Assert.Equal(0.5m, settings.GetProperty("account").GetProperty("sizingParameter").GetDecimal());
        Assert.Equal(0.03m, settings.GetProperty("riskManagement").GetProperty("stopLossRatio").GetDecimal());
        Assert.Equal(0.06m, settings.GetProperty("riskManagement").GetProperty("takeProfitRatio").GetDecimal());
        Assert.Equal(0.02m, settings.GetProperty("report").GetProperty("annualRiskFreeRate").GetDecimal());
        Assert.Equal(0.01m, settings.GetProperty("report").GetProperty("annualMar").GetDecimal());
        Assert.Equal(250000m, result.GetProperty("equity").GetProperty("initial").GetDecimal());
        Assert.NotEqual(baseline.GetProperty("runFingerprint").GetProperty("sha256").GetString(), result.GetProperty("runFingerprint").GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("""{"initialCapital":0}""", "account.initialCapital")]
    [InlineData("""{"initialCapital":-5}""", "account.initialCapital")]
    [InlineData("""{"sizingParameter":0.5}""", "account.sizingParameter")]
    [InlineData("""{"sizingModel":"PercentOfEquity","sizingParameter":1.5}""", "account.sizingParameter")]
    [InlineData("""{"slippageRatio":1}""", "account.slippageRatio")]
    [InlineData("""{"initialMarginRatio":0.1,"maintenanceMarginRatio":0.2}""", "account.maintenanceMarginRatio")]
    [InlineData("""{"commissionFlat":-1}""", "account.commissionFlat")]
    [InlineData("""{"sizingModel":"Kelly"}""", "account.sizingModel")]
    [InlineData("""{"initialCapital":"1000"}""", "account.initialCapital")]
    [InlineData("""{"leverage":2}""", "account.leverage")]
    public async Task AnInvalidAccountIsRejectedNamingTheValue(string account, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Conditions, Period + ",\"account\":" + account);

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Theory]
    [InlineData("""{"stopLossRatio":1}""", "riskManagement.stopLossRatio")]
    [InlineData("""{"takeProfitRatio":-0.1}""", "riskManagement.takeProfitRatio")]
    [InlineData("""{"stopLossRatio":"5%"}""", "riskManagement.stopLossRatio")]
    [InlineData("""{"trailing":0.1}""", "riskManagement.trailing")]
    public async Task AnInvalidRiskSettingIsRejectedNamingTheValue(string risk, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Conditions, Period + ",\"riskManagement\":" + risk);

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Theory]
    [InlineData("""{"annualRiskFreeRate":-1}""")]
    [InlineData("""{"annualMar":-2}""")]
    [InlineData("""{"annualRiskFreeRate":"x"}""")]
    [InlineData("""{"rate":1}""")]
    public async Task AnInvalidReportSettingIsRejected(string report)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Conditions, Period + ",\"report\":" + report);

        Assert.Equal("INVALID_ARGUMENT", Code(response));
    }

    // ---- report: annual periods and bootstrap (the Backtest settings window's values) ----------------------------------------------------

    [Fact]
    public async Task OmittingTheReportValuesEqualsGivingTheFactoryDefaultsAndTheEchoShowsThem()
    {
        var dispatcher = await StartAsync();

        JsonElement omitted = Result(await RunAsync(dispatcher, Conditions, Period));
        JsonElement explicitDefaults = Result(await RunAsync(
            dispatcher, Conditions, Period + $",\"report\":{{\"annualPeriodsD1\":252,\"bootstrapSeed\":{BacktestDefaults.BootstrapSeed},\"bootstrapIterations\":{BacktestDefaults.BootstrapIterations}}}"));

        JsonElement report = omitted.GetProperty("effectiveSettings").GetProperty("report");
        Assert.Equal(252, report.GetProperty("annualPeriods").GetInt32());
        Assert.Equal(BacktestDefaults.BootstrapSeed, report.GetProperty("bootstrapSeed").GetInt32());
        Assert.Equal(BacktestDefaults.BootstrapIterations, report.GetProperty("bootstrapIterations").GetInt32());
        Assert.Equal(omitted.GetProperty("reproducibilityHash").GetString(), explicitDefaults.GetProperty("reproducibilityHash").GetString());
        Assert.Equal(omitted.GetProperty("runFingerprint").GetProperty("sha256").GetString(), explicitDefaults.GetProperty("runFingerprint").GetProperty("sha256").GetString());
        Assert.Equal(omitted.GetProperty("report").GetRawText(), explicitDefaults.GetProperty("report").GetRawText());
    }

    [Fact]
    public async Task TheAnnualPeriodsAndTheBootstrapValuesAreAppliedAndEchoed()
    {
        var dispatcher = await StartAsync();

        JsonElement baseline = Result(await RunAsync(dispatcher, Conditions, Period));
        JsonElement result = Result(await RunAsync(dispatcher, Conditions, Period + ",\"report\":{\"annualPeriodsD1\":365,\"bootstrapSeed\":7,\"bootstrapIterations\":1500}"));

        JsonElement report = result.GetProperty("effectiveSettings").GetProperty("report");
        Assert.Equal(365, report.GetProperty("annualPeriods").GetInt32());
        Assert.Equal(7, report.GetProperty("bootstrapSeed").GetInt32());
        Assert.Equal(1500, report.GetProperty("bootstrapIterations").GetInt32());
        Assert.NotEqual(baseline.GetProperty("report").GetRawText(), result.GetProperty("report").GetRawText()); // the annualised metrics use the period count
    }

    [Theory]
    [InlineData("""{"annualPeriodsD1":0}""", "report")]
    [InlineData("""{"annualPeriodsD1":367}""", "report")]
    [InlineData("""{"annualPeriodsD1":1.5}""", "report.annualPeriodsD1")]
    [InlineData("""{"annualPeriodsD1":"252"}""", "report.annualPeriodsD1")]
    [InlineData("""{"bootstrapSeed":1.5}""", "report.bootstrapSeed")]
    [InlineData("""{"bootstrapSeed":99999999999}""", "report.bootstrapSeed")]
    [InlineData("""{"bootstrapIterations":999}""", "report.bootstrapIterations")]
    [InlineData("""{"bootstrapIterations":"2000"}""", "report.bootstrapIterations")]
    public async Task AnInvalidAnnualPeriodOrBootstrapValueIsRejectedByTheEnginesOwnRule(string report, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, Conditions, Period + ",\"report\":" + report);

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Equal(parameter, Param(response));
    }

    // ---- arguments ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SupportedTimeFramesAreExecutedAndUnsupportedAreRefused()
    {
        var dispatcher = await StartAsync();

        using JsonDocument daily = await RunAsync(dispatcher, Conditions, Period + ",\"timeFrame\":\"D1\"");
        using JsonDocument weekly = await RunAsync(dispatcher, Conditions, Period + ",\"timeFrame\":\"W1\"");
        using JsonDocument monthly = await RunAsync(dispatcher, Conditions, Period + ",\"timeFrame\":\"MN1\"");
        using JsonDocument hourly = await RunAsync(dispatcher, Conditions, Period + ",\"timeFrame\":\"H1\"");

        Assert.Equal("D1", Result(daily).GetProperty("timeFrame").GetString());
        Assert.Equal("W1", Result(weekly).GetProperty("timeFrame").GetString());
        Assert.Equal(52, Result(weekly).GetProperty("report").GetProperty("annualPeriods").GetInt32());
        Assert.Equal("MN1", Result(monthly).GetProperty("timeFrame").GetString());
        Assert.Equal(12, Result(monthly).GetProperty("report").GetProperty("annualPeriods").GetInt32());

        Assert.Equal("INVALID_ARGUMENT", Code(hourly));
        Assert.Equal("timeFrame", Param(hourly));
    }

    [Theory]
    [InlineData("""{"symbol":"NOPE","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}}}""", "SYMBOL_NOT_FOUND", "symbol")]
    [InlineData("""{"symbol":"..\\evil","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}}}""", "INVALID_ARGUMENT", "symbol")]
    [InlineData("""{"symbol":"MSFT"}""", "INVALID_ARGUMENT", "conditions")]
    [InlineData("""{"conditions":{}}""", "INVALID_ARGUMENT", "symbol")]
    [InlineData("""{"symbol":"MSFT","conditions":{},"startDate":"2024-02-01"}""", "INVALID_ARGUMENT", "conditions")]
    [InlineData("""{"symbol":"MSFT","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}},"startDate":"01/02/2024"}""", "INVALID_ARGUMENT", "startDate")]
    [InlineData("""{"symbol":"MSFT","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}},"startDate":"2024-12-31","endDate":"2024-02-01"}""", "INVALID_ARGUMENT", "startDate")]
    [InlineData("""{"symbol":"MSFT","conditions":{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}},"mode":"fast"}""", "INVALID_ARGUMENT", "mode")]
    public async Task ARequestThatCannotRunIsRefusedNamingTheArgument(string arguments, string code, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", arguments))!;

        Assert.Equal(code, Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task APeriodWithoutStoredBarsRunsEmptyAndSaysSo()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, Conditions, ",\"startDate\":\"2030-01-01\",\"endDate\":\"2030-12-31\""));

        Assert.Equal(0, result.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32());
        Assert.Equal(0, result.GetProperty("trades").GetProperty("totalTrades").GetInt32());
        Assert.Equal("InsufficientData", result.GetProperty("report").GetProperty("totalPnL").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("report").GetProperty("totalPnL").GetProperty("value").ValueKind);
        Assert.Contains("nothing was evaluated", result.GetProperty("warnings")[0].GetString());
    }

    // ---- conditions -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IndicatorConditionsWithParametersAndSeriesRunLikeTheWindowsConditions()
    {
        var dispatcher = await StartAsync();
        const string conditions = """
            {"entryLong":{"operator":"And","children":[
                {"left":{"indicator":"RSI","parameters":{"Period":5}},"comparison":"GreaterThan","right":50},
                {"left":{"price":"Close"},"comparison":"GreaterThan","right":{"indicator":"SMA","parameters":{"Period":10}}}]},
             "exitLong":{"left":{"indicator":"RSI","parameters":{"Period":5}},"comparison":"LessThan","right":50},
             "entryShort":{"left":{"indicator":"MACD","outputSeriesName":"Signal"},"comparison":"GreaterThanOrEqual","right":{"indicator":"MACD","outputSeriesName":"Signal","offset":1}},
             "exitShort":{"left":{"price":"High","offset":2},"comparison":"NotEqual","right":0},
             "reverseLong":{"left":{"price":"Low"},"comparison":"Equal","right":{"price":"Open"}}}
            """;

        JsonElement result = Result(await RunAsync(dispatcher, conditions, Period));

        string[] roots = result.GetProperty("conditionRoots").EnumerateArray().Select(r => r.GetProperty("root").GetString()!).ToArray();
        Assert.Equal(new[] { "EntryLong", "EntryShort", "ExitLong", "ExitShort", "ReverseLong" }, roots);
        JsonElement entryLong = result.GetProperty("conditionRoots")[0];
        Assert.Equal(2, entryLong.GetProperty("comparisonCount").GetInt32());
        Assert.Equal("RSI(Period=5) > 50 And Close > SMA(Period=10)", entryLong.GetProperty("text").GetString());
        Assert.Contains(").Signal", result.GetProperty("conditionRoots")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ARunWithoutAnEntryConditionSaysSoAndTradesNothing()
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, $$"""{"exitLong":{{Exit}}}""", Period));

        Assert.Equal(0, result.GetProperty("trades").GetProperty("totalTrades").GetInt32());
        Assert.Contains("No entry condition", result.GetProperty("warnings")[0].GetString());
    }

    [Theory]
    [InlineData("""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1},"entryMiddle":{}}""", "INVALID_ARGUMENT", "conditions.entryMiddle")]
    [InlineData("""{"entryLong":{"operator":"And","children":[]}}""", "INVALID_ARGUMENT", "conditions.entryLong.children")]
    [InlineData("""{"entryLong":{"operator":"Xor","children":[{"left":{"price":"Close"},"comparison":"GreaterThan","right":1}]}}""", "INVALID_ARGUMENT", "conditions.entryLong.operator")]
    [InlineData("""{"entryLong":{"left":{"price":"Close"},"comparison":"Contains","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.comparison")]
    [InlineData("""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":"high"}}""", "INVALID_ARGUMENT", "conditions.entryLong.right")]
    [InlineData("""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan"}}""", "INVALID_ARGUMENT", "conditions.entryLong.right")]
    [InlineData("""{"entryLong":{"left":{"price":"Close"},"comparison":"GreaterThan","right":1,"role":"Both"}}""", "INVALID_ARGUMENT", "conditions.entryLong.role")]
    [InlineData("""{"entryLong":{"left":{"price":"Close","indicator":"RSI"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left")]
    [InlineData("""{"entryLong":{"left":{"offset":1},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left")]
    [InlineData("""{"entryLong":{"left":{"column":"Sector"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.column")]
    [InlineData("""{"entryLong":{"left":{"price":"Close","frame":"W1"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.frame")]
    [InlineData("""{"entryLong":{"left":{"price":"Typo"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.price")]
    [InlineData("""{"entryLong":{"left":{"price":"Close","parameters":{"Period":1}},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left")]
    [InlineData("""{"entryLong":{"left":{"price":"Close","offset":-1},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.offset")]
    [InlineData("""{"entryLong":{"left":{"price":"Close","offset":1.5},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.offset")]
    [InlineData("""{"entryLong":{"left":{"indicator":"NoSuchThing"},"comparison":"GreaterThan","right":1}}""", "UNKNOWN_INDICATOR", "conditions.entryLong.left.indicator")]
    [InlineData("""{"entryLong":{"left":{"indicator":"RSI","parameters":{"Window":3}},"comparison":"GreaterThan","right":1}}""", "INVALID_INDICATOR_PARAMETER", "conditions.entryLong.left.parameters.Window")]
    [InlineData("""{"entryLong":{"left":{"indicator":"RSI","parameters":{"Period":0}},"comparison":"GreaterThan","right":1}}""", "INVALID_INDICATOR_PARAMETER", "conditions.entryLong.left.parameters.Period")]
    [InlineData("""{"entryLong":{"left":{"indicator":"RSI","outputSeriesName":"Nope"},"comparison":"GreaterThan","right":1}}""", "INVALID_OUTPUT_SERIES", "conditions.entryLong.left.outputSeriesName")]
    [InlineData("""{"entryLong":{"left":{"indicator":"ZigZag"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.indicator")]
    [InlineData("""{"entryLong":{"left":{"indicator":"Ichimoku","outputSeriesName":"ChikouSpan"},"comparison":"GreaterThan","right":1}}""", "INVALID_ARGUMENT", "conditions.entryLong.left.indicator")]
    [InlineData("""[1]""", "INVALID_ARGUMENT", "conditions")]
    public async Task AnInvalidConditionIsRejectedNamingTheNode(string conditions, string code, string parameter)
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await RunAsync(dispatcher, conditions, Period);

        Assert.Equal(code, Code(response));
        Assert.Equal(parameter, Param(response));
    }

    [Fact]
    public async Task ACrossTickerIndicatorIsRefused()
    {
        var dispatcher = await StartAsync();
        string crossTicker = Factory.GetRegisteredTypes().First(type => Factory.Create(type) is ICrossTickerIndicator).ToString();

        using JsonDocument response = await RunAsync(dispatcher, "{\"entryLong\":{\"left\":{\"indicator\":\"" + crossTicker + "\"},\"comparison\":\"GreaterThan\",\"right\":1}}", Period);

        Assert.Equal("INVALID_ARGUMENT", Code(response));
        Assert.Contains("two symbols", McpTestSupport.Structured(response).GetProperty("message").GetString());
    }

    private static readonly IndicatorFactory Factory = new();

    [Fact]
    public async Task TheTreeLimitsAreTheApplicationsBacktestSettings()
    {
        string deep = "{\"entryLong\":{\"operator\":\"And\",\"children\":[{\"operator\":\"Or\",\"children\":[" + Entry + "]}]}}";
        string wide = "{\"entryLong\":{\"operator\":\"And\",\"children\":[" + Entry + "," + Exit + "," + Entry + "]}}";
        string offset = """{"entryLong":{"left":{"price":"Close","offset":11},"comparison":"GreaterThan","right":1}}""";

        var depthDispatcher = await StartAsync(("Backtest:MaxConditionTreeDepth", "1"));
        using JsonDocument tooDeep = await RunAsync(depthDispatcher, deep, Period);
        var nodeDispatcher = await StartAsync(("Backtest:MaxConditionTreeNodes", "3"), ("Backtest:MaxConditionTreeDepth", "3"));
        using JsonDocument tooWide = await RunAsync(nodeDispatcher, wide, Period);
        var offsetDispatcher = await StartAsync(("Backtest:MaxConditionOffset", "10"));
        using JsonDocument tooFar = await RunAsync(offsetDispatcher, offset, Period);

        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(tooDeep));
        Assert.Contains("Backtest:MaxConditionTreeDepth", McpTestSupport.Structured(tooDeep).GetProperty("hint").GetString());
        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(tooWide));
        Assert.Contains("Backtest:MaxConditionTreeNodes", McpTestSupport.Structured(tooWide).GetProperty("hint").GetString());
        Assert.Equal("CONDITION_LIMIT_EXCEEDED", Code(tooFar));
        Assert.Contains("Backtest:MaxConditionOffset", McpTestSupport.Structured(tooFar).GetProperty("hint").GetString());
        Assert.Equal("conditions.entryLong.left.offset", Param(tooFar));
    }

    [Theory]
    [InlineData("GreaterThan")]
    [InlineData("GreaterThanOrEqual")]
    [InlineData("LessThan")]
    [InlineData("LessThanOrEqual")]
    [InlineData("Equal")]
    [InlineData("NotEqual")]
    public async Task EveryNamedComparisonIsAcceptedByTheApplicationsValidator(string comparison)
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await RunAsync(dispatcher, $$"""{"entryLong":{{RuleOf(comparison, right: "100")}}}""", Period));

        Assert.Equal("Completed", result.GetProperty("status").GetString());
    }

    // ---- read-only guarantee, schema, report writer ---------------------------------------------------------------------------------

    [Fact]
    public async Task ARunChangesNothingInTheDataTree()
    {
        var dispatcher = await StartAsync();
        var before = _fixture.Snapshot();

        Result(await RunAsync(dispatcher, Conditions, Period));
        using JsonDocument failed = await RunAsync(dispatcher, """{"entryLong":{"left":{"indicator":"ZigZag"},"comparison":"GreaterThan","right":1}}""", Period);

        Assert.Equal(before, _fixture.Snapshot());
    }

    [Fact]
    public async Task TheToolIsListedAsAHeavyReadOnlyToolWithItsRequiredArguments()
    {
        var dispatcher = await StartAsync();

        using JsonDocument listing = JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_run_backtest");

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(new[] { "symbol" }, tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("entryLong", tool.GetProperty("description").GetString());
    }

    [Fact]
    public void EveryPropertyOfTheReportIsWrittenOrExcludedOnPurpose()
    {
        PropertyInfo[] properties = typeof(BacktestReport).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, property => Assert.True(BacktestReportJson.CanWrite(property), $"BacktestReport.{property.Name} has a type the writer does not know."));
        Assert.Equal(
            properties.Select(p => p.Name).Except(BacktestReportJson.ExcludedProperties).OrderBy(n => n, StringComparer.Ordinal),
            BacktestReportJson.WrittenProperties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { nameof(BacktestReport.RunFingerprint) }, BacktestReportJson.ExcludedProperties.ToArray());
    }

    [Fact]
    public async Task TheWrittenReportCarriesEveryPropertyThatHasAValue()
    {
        var dispatcher = await StartAsync();
        JsonElement report = Result(await RunAsync(dispatcher, Conditions, Period)).GetProperty("report");

        using var db = new DuckDBConnectionManager();
        var preparation = new BacktestRunPreparationService(new ParquetDataService(db, Options.Create(_fixture.Settings)));
        var snapshot = new BacktestRunSnapshot(
            "MSFT", TimeFrame.D1, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital, SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter },
            ImmutableArray<BacktestConditionEntry>.Empty, null, ImmutableArray<StrategyIndicatorRequest>.Empty, BacktestReportDefaults.BuiltIn,
            BacktestDefaults.BootstrapSeed, BacktestDefaults.BootstrapIterations, BacktestConditionTree.Empty);
        PreparedBacktestRun prepared = await preparation.PrepareAsync(snapshot, CancellationToken.None);
        BacktestReport direct = new BacktestEvaluationService(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy())
            .Evaluate(prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions).Report!;

        var written = new HashSet<string>(report.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        foreach (string name in BacktestReportJson.WrittenProperties)
        {
            PropertyInfo property = typeof(BacktestReport).GetProperty(name)!;
            bool hasValue = property.GetValue(direct) is not null;
            Assert.True(!hasValue || written.Any(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase)), $"{name} has a value but is not in the JSON.");
        }

        Assert.Contains("cagr", written);
        Assert.Contains("sqn", written);
        Assert.Contains("annualizedSharpe", written);
        Assert.Contains("totalTrades", written);
    }
}
