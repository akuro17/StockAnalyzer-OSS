using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
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
/// D-11 B: the same backtest, once as sa_run_backtest (with conditions, and with a saved template) and once as the Backtest window runs it, must give
/// the same result, compared exactly (tolerance 0). The window side is built the way <c>BacktestWindowViewModel.BuildRunSnapshot</c> builds its
/// snapshot (UI date normalisation, the persisted condition tree checked with the configured limits, an owned copy of the risk settings, validated report
/// defaults) from explicit values, and is run through the same preparation and evaluation services the window calls. The MCP server uses the
/// factory defaults and the window uses saved values, so every setting here is written out and none is read from a saved file.
/// Not covered: the view model's own property wiring (no UI is created here).
/// </summary>
public sealed class BacktestGuiMcpEquivalenceTests : IDisposable
{
    private const int Days = 400;
    private const string StartDate = "2024-02-01";
    private const string EndDate = "2024-12-31";
    private const int SmaPeriod = 5;

    private const decimal InitialCapital = 500_000m;
    private const decimal CommissionFlat = 1.25m;
    private const decimal CommissionPerUnit = 0.01m;
    private const decimal SlippageRatio = 0.0005m;
    private const decimal SizingParameter = 3m;
    private const decimal StopLossRatio = 0.05m;
    private const decimal TakeProfitRatio = 0.10m;
    private const decimal AnnualRiskFreeRate = 0.02m;
    private const decimal AnnualMar = 0.01m;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft");
    private readonly string _root = TestDataIsolationBootstrap.NewTempDirectory("McpBacktestEquivalence");
    private readonly List<McpServerRuntime> _runtimes = new();

    private string TemplateFolder => Path.Combine(_root, "BacktestCondition");

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    // ---- the one condition tree, as persisted data (what the window saves and a template stores) ---------------------------------------

    private static BacktestConditionSideDto Close(TimeFrame? frame = null) => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, OutputName = IndicatorResult.MainSeriesName, Frame = frame };

    private static BacktestConditionSideDto Sma(TimeFrame? frame = null) => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = SmaPeriod }, OutputName = IndicatorResult.MainSeriesName, Frame = frame };

    private static BacktestConditionNodeDto Rule(ComparisonOperator op, TimeFrame? sideFrame = null) => new()
    {
        Kind = BacktestConditionNodeKind.Group,
        Operator = LogicalOperator.And,
        Children = new List<BacktestConditionNodeDto>
        {
            new()
            {
                Kind = BacktestConditionNodeKind.Leaf,
                Comparison = new BacktestConditionEntryDto { Left = Close(sideFrame), Operator = op, TargetMode = RightHandTargetMode.Indicator, Right = Sma(sideFrame) },
            },
        },
    };

    private static BacktestConditionTreeDto TreeDto(TimeFrame? sideFrame = null)
        => new() { EntryLong = Rule(ComparisonOperator.GreaterThan, sideFrame), ExitLong = Rule(ComparisonOperator.LessThan, sideFrame) };

    private static string N(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string SmaSide(string frameKey) => "{\"indicator\":\"SMA\",\"parameters\":{\"Period\":" + SmaPeriod + "}" + frameKey + "}";

    private static string Conditions(string frameKey)
        => "{\"entryLong\":{\"left\":{\"price\":\"Close\"" + frameKey + "},\"comparison\":\"GreaterThan\",\"right\":" + SmaSide(frameKey) + "},"
        + "\"exitLong\":{\"left\":{\"price\":\"Close\"" + frameKey + "},\"comparison\":\"LessThan\",\"right\":" + SmaSide(frameKey) + "}}";

    private static readonly string ConditionsJson = Conditions(string.Empty);

    /// <summary>The same conditions with every side on W1 (the side key the plan 05 / T1 binder accepts).</summary>
    private static readonly string WeeklySideConditionsJson = Conditions(",\"frame\":\"W1\"");

    private static readonly string SettingsJson =
        ",\"account\":{\"initialCapital\":" + N(InitialCapital) + ",\"commissionFlat\":" + N(CommissionFlat) + ",\"commissionPerUnit\":" + N(CommissionPerUnit)
        + ",\"slippageRatio\":" + N(SlippageRatio) + ",\"sizingModel\":\"" + BacktestDefaults.SizingModel + "\",\"sizingParameter\":" + N(SizingParameter) + "}"
        + ",\"report\":{\"annualRiskFreeRate\":" + N(AnnualRiskFreeRate) + ",\"annualMar\":" + N(AnnualMar) + "}";

    private static readonly string RiskJson = ",\"riskManagement\":{\"stopLossRatio\":" + N(StopLossRatio) + ",\"takeProfitRatio\":" + N(TakeProfitRatio) + "}";

    private static readonly string Period = $",\"startDate\":\"{StartDate}\",\"endDate\":\"{EndDate}\"";

    // ---- hosting ------------------------------------------------------------------------------------------------------------------------

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
        ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
        ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
        ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
        ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
    }).Build();

    private async Task<McpMessageDispatcher> StartAsync()
    {
        McpServerRuntime runtime = McpServerComposition.Create(Configuration(), new LogSink(new StringWriter()), templatesDirectory: Path.Combine(_root, "Screener"), backtestTemplatesDirectory: TemplateFolder);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private Guid SaveTemplate()
    {
        var template = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Equivalence",
            ConditionTree = TreeDto(),
            RiskManagement = new BacktestRiskManagementSettingsDto { StopLossPercent = StopLossRatio, TakeProfitPercent = TakeProfitRatio },
        };
        Directory.CreateDirectory(TemplateFolder);
        File.WriteAllText(Path.Combine(TemplateFolder, $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        return template.Id;
    }

    private static async Task<JsonElement> CallAsync(McpMessageDispatcher dispatcher, string body)
    {
        JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_run_backtest", "{\"symbol\":\"MSFT\"," + body + "}"))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    // ---- the window's side --------------------------------------------------------------------------------------------------------------

    /// <summary>The snapshot the window builds (BuildRunSnapshot), from the same explicit values.</summary>
    private BacktestRunSnapshot WindowSnapshot(int? annualPeriodsD1 = null, int? bootstrapSeed = null, int? bootstrapIterations = null)
        => WindowSnapshot(TimeFrame.D1, annualPeriodsD1, bootstrapSeed, bootstrapIterations);

    private BacktestRunSnapshot WindowSnapshot(TimeFrame frame, int? annualPeriodsD1 = null, int? bootstrapSeed = null, int? bootstrapIterations = null, TimeFrame? sideFrame = null)
    {
        IStockAnalyzerSettings settings = new ServiceCollection().AddStockAnalyzerReadOnlyCore(Configuration()).BuildServiceProvider().GetRequiredService<IStockAnalyzerSettings>();

        // The dates come from date pickers: a value without a kind is taken as that calendar day in UTC (NormalizeUiDate).
        static DateTime NormalizeUiDate(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

        var configuration = new BacktestConfiguration
        {
            ExecutionModel = ExecutionModel.Legacy,
            InitialCapital = InitialCapital,
            CommissionFlat = CommissionFlat,
            CommissionPerUnit = CommissionPerUnit,
            SlippageRatio = SlippageRatio,
            SizingModel = BacktestDefaults.SizingModel,
            SizingParameter = SizingParameter,
            InitialMarginRatio = BacktestDefaults.InitialMarginRatio,
            MaintenanceMarginRatio = BacktestDefaults.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio,
        };
        configuration.Validate();

        BacktestConditionTree tree = BacktestConditionValidator.SnapshotTree(
            BacktestConditionTreeDtoMapper.ToTree(TreeDto(sideFrame), settings.BacktestMaxConditionTreeDepth),
            settings.BacktestMaxConditionOffset,
            settings.BacktestMaxConditionTreeDepth,
            settings.BacktestMaxConditionTreeNodes,
            settings.BacktestMaxConditionGroupNameLength);

        var risk = new BacktestRiskManagementSettings { StopLossPercent = StopLossRatio, TakeProfitPercent = TakeProfitRatio };
        BacktestConditionValidator.ValidateRiskManagement(risk);
        var ownedRisk = new BacktestRiskManagementSettings { StopLossPercent = risk.StopLossPercent, TakeProfitPercent = risk.TakeProfitPercent };

        BacktestReportDefaults reportDefaults = BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
        {
            SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
            AnnualRiskFreeRate = AnnualRiskFreeRate,
            AnnualMAR = AnnualMar,
            AnnualPeriodsByFrame = BacktestReportDefaults.BuiltIn.AnnualPeriodsByFrame
                .ToDictionary(entry => entry.Key, entry => entry.Key == TimeFrame.D1 && annualPeriodsD1 is { } periods ? periods : entry.Value),
        });

        return new BacktestRunSnapshot(
            "MSFT",
            frame,
            NormalizeUiDate(new DateTime(2024, 2, 1)),
            NormalizeUiDate(new DateTime(2024, 12, 31)),
            configuration,
            ImmutableArray<BacktestConditionEntry>.Empty,
            ownedRisk,
            ImmutableArray<StrategyIndicatorRequest>.Empty,
            reportDefaults,
            bootstrapSeed ?? BacktestDefaults.BootstrapSeed,
            bootstrapIterations ?? BacktestDefaults.BootstrapIterations,
            tree);
    }

    /// <summary>What the window's RunBacktestAsync does with the snapshot: PrepareAsync, then Evaluate.</summary>
    private Task<BacktestEvaluationArtifact> RunLikeTheWindowAsync(int? annualPeriodsD1, int? bootstrapSeed = null, int? bootstrapIterations = null)
        => RunLikeTheWindowAsync(TimeFrame.D1, annualPeriodsD1, bootstrapSeed, bootstrapIterations);

    private async Task<BacktestEvaluationArtifact> RunLikeTheWindowAsync(TimeFrame frame = TimeFrame.D1, int? annualPeriodsD1 = null, int? bootstrapSeed = null, int? bootstrapIterations = null, TimeFrame? sideFrame = null)
    {
        using var db = new DuckDBConnectionManager();
        var rawDataService = new ParquetDataService(db, Options.Create(_fixture.Settings));
        var aggregatingDataService = new AggregatingDataService(rawDataService, new TimeFrameManager(rawDataService));
        var preparation = new BacktestRunPreparationService(aggregatingDataService);
        var evaluation = new BacktestEvaluationService(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy());
        PreparedBacktestRun prepared = await preparation.PrepareAsync(WindowSnapshot(frame, annualPeriodsD1, bootstrapSeed, bootstrapIterations, sideFrame), CancellationToken.None);
        return evaluation.Evaluate(prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions);
    }

    // ---- the comparison: exact, every trade and every report field ----------------------------------------------------------------

    private static void AssertSamePercent(decimal? expected, JsonElement actual)
    {
        if (expected is { } value)
        {
            Assert.Equal(value, actual.GetDecimal());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, actual.ValueKind);
        }
    }

    private static void AssertSameResult(BacktestEvaluationArtifact window, JsonElement mcp)
    {
        BacktestResult result = window.Result;
        Assert.Equal("Completed", mcp.GetProperty("status").GetString());
        Assert.Equal(result.Status.ToString(), mcp.GetProperty("status").GetString());

        Assert.Equal(Convert.ToHexString(result.ReproducibilityHash), mcp.GetProperty("reproducibilityHash").GetString());

        JsonElement fingerprint = mcp.GetProperty("runFingerprint");
        Assert.Equal(window.RunFingerprint.IsAvailable, fingerprint.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(window.RunFingerprint.Sha256, fingerprint.GetProperty("sha256").GetString());
        Assert.Equal(window.RunFingerprint.SchemaVersion, fingerprint.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(window.RunFingerprint.ExecutionSemanticsVersion, fingerprint.GetProperty("executionSemanticsVersion").GetInt32());

        JsonElement trades = mcp.GetProperty("trades");
        Assert.True(result.Trades.Length > 0, "the scenario must trade, or the comparison proves nothing");
        Assert.Equal(result.Trades.Length, trades.GetProperty("totalTrades").GetInt32());
        Assert.False(trades.GetProperty("truncated").GetBoolean());
        JsonElement[] items = trades.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(result.Trades.Length, items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            BacktestTrade trade = result.Trades[i];
            JsonElement item = items[i];
            Assert.Equal(trade.TradeId, item.GetProperty("tradeId").GetInt32());
            Assert.Equal(trade.Side.ToString(), item.GetProperty("side").GetString());
            Assert.Equal(ToolJson.FormatDate(trade.EntryTime), item.GetProperty("entryDate").GetString());
            Assert.Equal(trade.EntryPrice, item.GetProperty("entryPrice").GetDecimal());
            Assert.Equal(ToolJson.FormatDate(trade.ExitTime), item.GetProperty("exitDate").GetString());
            Assert.Equal(trade.ExitPrice, item.GetProperty("exitPrice").GetDecimal());
            Assert.Equal(trade.Quantity, item.GetProperty("quantity").GetDecimal());
            Assert.Equal(trade.ClosedGross, item.GetProperty("closedGross").GetDecimal());
            Assert.Equal(trade.ClosedNet, item.GetProperty("closedNet").GetDecimal());
            Assert.Equal(trade.EntryFee, item.GetProperty("entryFee").GetDecimal());
            Assert.Equal(trade.ExitFee, item.GetProperty("exitFee").GetDecimal());
            Assert.Equal(trade.HoldingBars, item.GetProperty("holdingBars").GetInt32());
            Assert.Equal(trade.IsForcedLiquidation, item.GetProperty("isForcedLiquidation").GetBoolean());
            Assert.Equal(trade.EntrySignal, item.GetProperty("entrySignal").GetString());
            Assert.Equal(trade.ExitReason, item.GetProperty("exitReason").GetString());
            Assert.Equal(trade.MfePrice, item.GetProperty("mfePrice").GetDecimal());
            Assert.Equal(trade.MaePrice, item.GetProperty("maePrice").GetDecimal());
            AssertSamePercent(trade.MfePercent, item.GetProperty("mfePercent"));
            AssertSamePercent(trade.MaePercent, item.GetProperty("maePercent"));
        }

        Assert.Equal(result.EquityPoints[^1].Equity, mcp.GetProperty("equity").GetProperty("final").GetDecimal());
        Assert.Equal(window.Metadata.HistoryStartIndex, mcp.GetProperty("period").GetProperty("warmUpBarCount").GetInt32());
        Assert.Equal(window.Metadata.SampleCount, mcp.GetProperty("period").GetProperty("evaluatedBarCount").GetInt32());
        Assert.Equal(result.Warnings.Length, mcp.GetProperty("engineWarnings").GetProperty("total").GetInt32());

        // Every report field, with the writer the tool uses, compared as parsed JSON (numbers by value, no tolerance).
        string expectedReport = ToolJson.Build(writer => BacktestReportJson.Write(writer, window.Report!));
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedReport), JsonNode.Parse(mcp.GetProperty("report").GetRawText())),
            "The report differs.\nwindow: " + expectedReport + "\nmcp:    " + mcp.GetProperty("report").GetRawText());
    }

    // ---- tests --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMcpRunWithConditionsEqualsTheRunTheWindowWouldMake()
    {
        var dispatcher = await StartAsync();

        JsonElement mcp = await CallAsync(dispatcher, $"\"conditions\":{ConditionsJson}{Period}{SettingsJson}{RiskJson}");
        BacktestEvaluationArtifact window = await RunLikeTheWindowAsync();

        AssertSameResult(window, mcp);
    }

    [Fact]
    public async Task TheAnnualPeriodsAndBootstrapValuesOfTheSettingsPageGiveTheSameResultInBothPaths()
    {
        const int annualPeriods = 365;
        const int seed = 7;
        const int iterations = 1500;
        var dispatcher = await StartAsync();
        string report = SettingsJson.Replace(
            "\"report\":{",
            $"\"report\":{{\"annualPeriodsD1\":{annualPeriods},\"bootstrapSeed\":{seed},\"bootstrapIterations\":{iterations},");

        JsonElement mcp = await CallAsync(dispatcher, $"\"conditions\":{ConditionsJson}{Period}{report}{RiskJson}");
        BacktestEvaluationArtifact window = await RunLikeTheWindowAsync(annualPeriods, seed, iterations);

        AssertSameResult(window, mcp);
        JsonElement echoed = mcp.GetProperty("effectiveSettings").GetProperty("report");
        Assert.Equal(annualPeriods, echoed.GetProperty("annualPeriods").GetInt32());
        Assert.Equal(seed, echoed.GetProperty("bootstrapSeed").GetInt32());
        Assert.Equal(iterations, echoed.GetProperty("bootstrapIterations").GetInt32());
        Assert.Equal(annualPeriods, window.Metadata.AnnualPeriods);
    }

    [Fact]
    public async Task TheMcpRunWithASavedTemplateEqualsTheRunTheWindowWouldMake()
    {
        Guid id = SaveTemplate();
        var dispatcher = await StartAsync();

        JsonElement mcp = await CallAsync(dispatcher, $"\"templateId\":\"{id}\"{Period}{SettingsJson}");
        BacktestEvaluationArtifact window = await RunLikeTheWindowAsync();

        Assert.Equal("template", mcp.GetProperty("conditionSource").GetString());
        AssertSameResult(window, mcp);
    }

    [Fact]
    public async Task TheMcpRunWithWeeklyFrameEqualsTheRunTheWindowWouldMake()
    {
        var dispatcher = await StartAsync();

        JsonElement mcp = await CallAsync(dispatcher, $"\"conditions\":{ConditionsJson}{Period}{SettingsJson}{RiskJson},\"timeFrame\":\"W1\"");
        BacktestEvaluationArtifact window = await RunLikeTheWindowAsync(TimeFrame.W1);

        Assert.Equal("W1", mcp.GetProperty("timeFrame").GetString());
        AssertSameResult(window, mcp);
    }

    [Fact]
    public async Task TheMcpRunWithWeeklySidesEqualsTheRunTheWindowWouldMakeWithWeeklySideFrames()
    {
        var dispatcher = await StartAsync();

        JsonElement mcp = await CallAsync(dispatcher, $"\"conditions\":{WeeklySideConditionsJson}{Period}{SettingsJson}{RiskJson}");
        BacktestEvaluationArtifact window = await RunLikeTheWindowAsync(sideFrame: TimeFrame.W1);
        BacktestEvaluationArtifact daily = await RunLikeTheWindowAsync();

        AssertSameResult(window, mcp);
        Assert.NotEqual(daily.RunFingerprint.Sha256, window.RunFingerprint.Sha256);
    }

    [Fact]
    public async Task ConditionsAndTheSameTemplateGiveTheSameMcpResult()
    {
        Guid id = SaveTemplate();
        var dispatcher = await StartAsync();

        JsonElement viaConditions = await CallAsync(dispatcher, $"\"conditions\":{ConditionsJson}{Period}{SettingsJson}{RiskJson}");
        JsonElement viaTemplate = await CallAsync(dispatcher, $"\"templateId\":\"{id}\"{Period}{SettingsJson}");

        Assert.Equal(viaConditions.GetProperty("reproducibilityHash").GetString(), viaTemplate.GetProperty("reproducibilityHash").GetString());
        Assert.Equal(viaConditions.GetProperty("runFingerprint").GetProperty("sha256").GetString(), viaTemplate.GetProperty("runFingerprint").GetProperty("sha256").GetString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(viaConditions.GetProperty("trades").GetRawText()), JsonNode.Parse(viaTemplate.GetProperty("trades").GetRawText())));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(viaConditions.GetProperty("report").GetRawText()), JsonNode.Parse(viaTemplate.GetProperty("report").GetRawText())));
    }
}
