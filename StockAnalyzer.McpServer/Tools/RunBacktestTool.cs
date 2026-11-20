using StockAnalyzer.Core.Services;
using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_run_backtest: runs one backtest in memory on the stored daily bars of one symbol with the application's own engine, report generator and
/// run fingerprint (the Backtest window's path). The tool maps the request to the window's snapshot and the result to JSON; nothing is saved.
/// </summary>
internal sealed class RunBacktestTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    internal const string ConditionsArgument = "conditions";
    private const string TemplateIdArgument = "templateId";
    private const string MaxTradesArgument = "maxReturnedTrades";
    private const string EquityCurveArgument = BacktestDiagnostics.EquityCurveArgument;
    private const string DrawdownEpisodesArgument = BacktestDiagnostics.DrawdownEpisodesArgument;
    private const string PeriodicReturnsArgument = BacktestDiagnostics.PeriodicReturnsArgument;
    private const string CompactRowsArgument = "compactRows";

    private static readonly string[] TradeColumns =
    {
        "tradeId", "side", "entryDate", "entryPrice", "exitDate", "exitPrice", "quantity",
        "closedGross", "closedNet", "entryFee", "exitFee", "holdingBars", "isForcedLiquidation",
        "entrySignal", "exitReason", "stopLevelAtExit", "mfePrice", "maePrice", "mfePercent", "maePercent",
    };

    /// <summary>The saved template a run used (its name is echoed with its id).</summary>
    private sealed record TemplateReference(Guid Id, string Name);

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly BacktestConditionBinder _conditions;
    private readonly McpLimits _limits;
    private readonly bool _compactDefaults;

    public RunBacktestTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, BacktestConditionBinder conditions, McpLimits limits, bool compactDefaults = false)
    {
        _gateway = gateway;
        _symbols = symbols;
        _conditions = conditions;
        _limits = limits;
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_run_backtest";

    public string Description =>
        $"Executes an in-memory backtest on stored bars ({string.Join("/", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}, Legacy model) using conditions ({string.Join(", ", BacktestConditionBinder.RootKeys)}) or saved templateId. " +
        $"Call sa_get_capabilities first; supports {string.Join(", ", ConditionOperatorExpander.ExpandedComparisonNames)}, per-side frame, account, riskManagement, diagnostics, {BacktestReportJson.CompactReportArgument}, and {CompactRowsArgument}. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => true;

    public string? CompactUsageHint => "Pass compactReport: true, compactRows: true, maxReturnedTrades: 0, or omit includeEquityCurve.";

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"conditions\":{\"entryLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30},\"exitLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"GreaterThan\",\"right\":70}},\"maxReturnedTrades\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Symbol to test (from sa_list_tickers).", required: true)
        .FreeObject(ConditionsArgument, "Entry/exit/reverse conditions tree. Mutually exclusive with templateId (one required).")
        .String(TemplateIdArgument, "Saved backtestCondition template GUID. Mutually exclusive with conditions (one required).")
        .String(StartArgument, "First evaluated date (yyyy-MM-dd). Default: history start.")
        .String(EndArgument, "Last evaluated date (yyyy-MM-dd). Default: latest bar.")
        .String(TimeFrameArgument, $"Bar timeframe ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default {ReadOnlyGatewayContract.BacktestTimeFrame}.", allowedValues: ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Select(t => t.ToString()).ToArray())
        .FreeObject(BacktestSettingsBinder.AccountArgument, "Account capital, cost, sizing, margin, and execution-realism settings. Default: factory defaults.")
        .FreeObject(BacktestSettingsBinder.RiskArgument, "Stop-loss, take-profit, and exits: {stopLossRatio, takeProfitRatio, dynamicExits{maxHoldingBars, atrStop, atrTarget, trailingStop}}.")
        .FreeObject(BacktestSettingsBinder.ReportArgument, "Report config: {annualRiskFreeRate, annualMar, annualPeriodsD1, bootstrapSeed, bootstrapIterations}. Default: factory defaults.")
        .Integer(MaxTradesArgument, $"Max trades returned (0..{_limits.MaxReturnedTrades}; 0 lists none). Default {_limits.MaxReturnedTrades}.", defaultValue: _limits.MaxReturnedTrades, minimum: 0, maximum: _limits.MaxReturnedTrades)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Allow unregistered symbol with stored bars. Default false.")
        .String(EquityCurveArgument, $"Equity curve resolution (Bar LIMIT_EXCEEDED >{McpLimitsContract.EffectiveMaxEquityCurvePoints(_limits)} pts). Default None.", allowedValues: BacktestDiagnostics.CurveNames)
        .Integer(DrawdownEpisodesArgument, $"Deepest drawdown episodes count (0..{_limits.MaxDrawdownEpisodes}). Default 0.", defaultValue: 0, minimum: 0, maximum: _limits.MaxDrawdownEpisodes)
        .Boolean(PeriodicReturnsArgument, "Include monthly and yearly equity returns. Default false.")
        .Boolean(BacktestReportJson.CompactReportArgument, BacktestReportJson.CompactReportDescription)
        .Boolean(CompactRowsArgument, "true empties trades.items/equityCurve.points and emits columns/rows arrays. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = ResolveTimeFrame(arguments);
        DateOnly? requestedStart = ToolArgs.Date(arguments, StartArgument);
        DateOnly? requestedEnd = ToolArgs.Date(arguments, EndArgument);
        if (requestedStart is { } start && requestedEnd is { } end && start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        int maxTrades = ToolArgs.NonNegativeWithin(arguments, MaxTradesArgument, _limits.MaxReturnedTrades, _limits.MaxReturnedTrades);
        var diagnosticsRequest = new BacktestDiagnosticsRequest(
            ToolArgs.String(arguments, EquityCurveArgument) is { } curveName ? BacktestDiagnostics.ParseCurve(curveName) : null,
            ToolArgs.NonNegativeWithin(arguments, DrawdownEpisodesArgument, 0, _limits.MaxDrawdownEpisodes),
            ToolArgs.Boolean(arguments, PeriodicReturnsArgument) ?? false);
        BacktestDiagnostics.EnsureTimeFrameSupported(diagnosticsRequest, timeFrame);
        bool compactReport = ToolArgs.Boolean(arguments, BacktestReportJson.CompactReportArgument) ?? _compactDefaults;
        bool compactRows = ToolArgs.Boolean(arguments, CompactRowsArgument) ?? _compactDefaults;

        // Everything the client sent is checked before any data is read.
        string? templateText = ToolArgs.String(arguments, TemplateIdArgument);
        bool hasConditions = arguments.TryGetProperty(ConditionsArgument, out _);
        if (hasConditions == (templateText is not null))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "Give exactly one of conditions and templateId.", ConditionsArgument, "Use conditions for a new tree, or templateId for a saved template.");
        }

        if (templateText is not null && arguments.TryGetProperty(BacktestSettingsBinder.RiskArgument, out _))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "riskManagement cannot be combined with templateId: the template's own risk settings are used.",
                BacktestSettingsBinder.RiskArgument,
                "Omit riskManagement, or use conditions instead of templateId.");
        }

        BoundBacktestSettings settings = BacktestSettingsBinder.Bind(arguments, timeFrame);
        BoundBacktestConditions conditions;
        TemplateReference? template = null;
        if (templateText is null)
        {
            conditions = _conditions.Bind(arguments.GetProperty(ConditionsArgument), ConditionsArgument, timeFrame);
        }
        else
        {
            if (!Guid.TryParse(templateText, out Guid templateId))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "templateId must be a template id (a GUID) from sa_list_templates.", TemplateIdArgument);
            }

            BacktestConditionTemplate saved = await _gateway.GetBacktestConditionTemplateAsync(templateId, cancellationToken).ConfigureAwait(false)
                ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No backtest-condition template has the id {templateId}.", TemplateIdArgument, "Call sa_list_templates with templateType backtestCondition.");
            conditions = _conditions.FromTemplate(saved, TemplateIdArgument, timeFrame);
            settings = settings with { RiskManagement = BacktestSettingsBinder.FromTemplate(saved.RiskManagement) };
            template = new TemplateReference(saved.Id, saved.Name);
        }

        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = resolved.IsRegistered ? Array.Empty<string>() : new[] { SymbolResolver.OffListWarning(new[] { symbol }) };
        IReadOnlyList<CandleData> stored = await _gateway.GetCandlesAsync(symbol, timeFrame, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument, "Call sa_list_tickers to see the symbols that have data.");
        }

        DateOnly effectiveStart = requestedStart ?? DateOnly.FromDateTime(stored[0].Timestamp);
        DateOnly effectiveEnd = requestedEnd ?? DateOnly.FromDateTime(stored[^1].Timestamp);
        if (effectiveStart > effectiveEnd)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        if (diagnosticsRequest.Curve == EquityCurveResolution.Bar)
        {
            // The stored bars inside the period are a lower bound of the evaluated bars, so a refusal here is never wrong and saves the run.
            int barsInPeriod = stored.Count(bar => DateOnly.FromDateTime(bar.Timestamp) >= effectiveStart && DateOnly.FromDateTime(bar.Timestamp) <= effectiveEnd);
            BacktestDiagnostics.EnsureCurveFits(EquityCurveResolution.Bar, barsInPeriod, _limits);
        }

        var snapshot = new BacktestRunSnapshot(
            symbol,
            timeFrame,
            ToUtc(effectiveStart),
            ToUtc(effectiveEnd),
            settings.Configuration,
            ImmutableArray<BacktestConditionEntry>.Empty,
            settings.RiskManagement,
            ImmutableArray<StrategyIndicatorRequest>.Empty,
            settings.ReportDefaults,
            settings.BootstrapSeed,
            settings.BootstrapIterations,
            conditions.Tree);

        BacktestEvaluationArtifact artifact;
        try
        {
            artifact = await _gateway.RunBacktestAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OverflowException)
        {
            throw BacktestOverflowReport.ForSingleRun(snapshot.Symbol, snapshot.Configuration);
        }
        catch (ArgumentException error)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"The backtest input was rejected: {error.Message.Split(Environment.NewLine)[0]}", hint: "Check the dates and the conditions.");
        }

        BacktestDiagnostics? diagnostics = diagnosticsRequest.Any ? BacktestDiagnostics.Compute(artifact, diagnosticsRequest, _limits) : null;
        JsonElement? accountArguments = arguments.TryGetProperty(BacktestSettingsBinder.AccountArgument, out JsonElement accountElement) ? accountElement : null;
        return ToolJson.Build(writer => Write(writer, artifact, symbol, timeFrame, effectiveStart, effectiveEnd, requestedStart is null || requestedEnd is null, settings, accountArguments, conditions, maxTrades, template, symbolWarnings, diagnostics, compactReport, compactRows));
    }

    internal static DateTime ToUtc(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    internal static TimeFrame ResolveTimeFrame(JsonElement arguments)
    {
        string? name = ToolArgs.String(arguments, TimeFrameArgument);
        if (name is null)
        {
            return ReadOnlyGatewayContract.BacktestTimeFrame;
        }

        if (!Enum.TryParse<TimeFrame>(name, ignoreCase: true, out TimeFrame frame) ||
            !ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Contains(frame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"Time frame {name} is not supported by the backtest.",
                TimeFrameArgument,
                $"Supported: {string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}.");
        }

        return frame;
    }

    private void Write(
        System.Text.Json.Utf8JsonWriter writer,
        BacktestEvaluationArtifact artifact,
        string symbol,
        TimeFrame timeFrame,
        DateOnly start,
        DateOnly end,
        bool datesDefaulted,
        BoundBacktestSettings settings,
        JsonElement? accountArguments,
        BoundBacktestConditions conditions,
        int maxTrades,
        TemplateReference? template,
        IReadOnlyList<string> symbolWarnings,
        BacktestDiagnostics? diagnostics,
        bool compactReport,
        bool compactRows)
    {
        BacktestResult result = artifact.Result;
        BacktestEvaluationMetadata metadata = artifact.Metadata;

        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        writer.WriteString("executionModel", settings.Configuration.ExecutionModel.ToString());
        writer.WriteString("status", result.Status.ToString());
        writer.WriteBoolean("isInsufficientData", result.IsInsufficientData);
        writer.WriteString("strategy", result.StrategyName);
        writer.WriteString("conditionSource", template is null ? "conditions" : "template");
        if (template is { } used)
        {
            writer.WriteStartObject("template");
            writer.WriteString("id", used.Id.ToString());
            writer.WriteString("name", used.Name);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("period");
        writer.WriteString("startDate", start.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("endDate", end.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteBoolean("datesFromStoredHistory", datesDefaulted);
        writer.WriteNumber("warmUpBarCount", metadata.HistoryStartIndex);
        writer.WriteNumber("evaluatedBarCount", metadata.SampleCount);
        if (metadata.LastProcessedTimestamp is { } last)
        {
            writer.WriteString("lastProcessedBar", ToolJson.FormatDate(last));
        }

        writer.WriteEndObject();

        WriteSettings(writer, settings, accountArguments, metadata);
        WriteConditions(writer, conditions);

        writer.WriteStartObject("equity");
        writer.WriteNumber("initial", settings.Configuration.InitialCapital);
        if (!result.EquityPoints.IsEmpty)
        {
            writer.WriteNumber("final", result.EquityPoints[^1].Equity);
        }

        writer.WriteEndObject();

        writer.WriteString("reproducibilityHash", Convert.ToHexString(result.ReproducibilityHash));
        BacktestReportJson.WriteRunFingerprint(writer, artifact.RunFingerprint);

        if (artifact.Report is { } report)
        {
            writer.WritePropertyName("report");
            BacktestReportJson.Write(writer, report, compactReport);
        }

        WriteTrades(writer, result, maxTrades, compactRows);
        WriteEngineWarnings(writer, result);
        diagnostics?.Write(writer, compactRows);
        var notes = new List<string>(symbolWarnings);
        notes.AddRange(conditions.Warnings);
        if (metadata.SampleCount == 0)
        {
            notes.Add("No stored bar lies inside the period: nothing was evaluated, so every metric is undefined.");
        }

        if (notes.Count > 0)
        {
            writer.WriteStartArray("warnings");
            foreach (string note in notes)
            {
                writer.WriteStringValue(note);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteSettings(System.Text.Json.Utf8JsonWriter writer, BoundBacktestSettings settings, JsonElement? accountArguments, BacktestEvaluationMetadata metadata)
    {
        BacktestConfiguration configuration = settings.Configuration;
        writer.WriteStartObject("effectiveSettings");
        writer.WriteStartObject("account");
        writer.WriteNumber("initialCapital", configuration.InitialCapital);
        writer.WriteNumber("commissionFlat", configuration.CommissionFlat);
        writer.WriteNumber("commissionPerUnit", configuration.CommissionPerUnit);
        writer.WriteNumber("slippageRatio", configuration.SlippageRatio);
        writer.WriteString("sizingModel", configuration.SizingModel.ToString());
        writer.WriteNumber("sizingParameter", configuration.SizingParameter);
        writer.WriteNumber("initialMarginRatio", configuration.InitialMarginRatio);
        writer.WriteNumber("maintenanceMarginRatio", configuration.MaintenanceMarginRatio);
        writer.WriteNumber("liquidationPenaltyRatio", configuration.LiquidationPenaltyRatio);
        BacktestSettingsEcho.WriteExecutionRealism(writer, accountArguments, configuration);
        writer.WriteEndObject();

        writer.WriteStartObject("riskManagement");
        if (settings.RiskManagement?.StopLossPercent is { } stop)
        {
            writer.WriteNumber("stopLossRatio", stop);
        }

        if (settings.RiskManagement?.TakeProfitPercent is { } take)
        {
            writer.WriteNumber("takeProfitRatio", take);
        }

        if (settings.RiskManagement?.DynamicExits is { } dynamicExits)
        {
            writer.WriteStartObject("dynamicExits");
            if (dynamicExits.MaxHoldingBars is { } maxHoldingBars)
            {
                writer.WriteNumber("maxHoldingBars", maxHoldingBars);
            }
            if (dynamicExits.AtrStop is { } atrStop)
            {
                writer.WriteStartObject("atrStop");
                writer.WriteNumber("period", atrStop.Period);
                writer.WriteNumber("multiple", atrStop.Multiple);
                writer.WriteEndObject();
            }
            if (dynamicExits.AtrTarget is { } atrTarget)
            {
                writer.WriteStartObject("atrTarget");
                writer.WriteNumber("period", atrTarget.Period);
                writer.WriteNumber("multiple", atrTarget.Multiple);
                writer.WriteEndObject();
            }
            if (dynamicExits.TrailingStop is { } trailingStop)
            {
                writer.WriteStartObject("trailingStop");
                writer.WriteString("mode", trailingStop.Mode.ToString());
                if (trailingStop.Period is { } p) writer.WriteNumber("period", p);
                if (trailingStop.Multiple is { } m) writer.WriteNumber("multiple", m);
                if (trailingStop.Ratio is { } r) writer.WriteNumber("ratio", r);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        writer.WriteStartObject("report");
        writer.WriteNumber("annualRiskFreeRate", metadata.AnnualRiskFreeRate);
        writer.WriteNumber("annualMar", metadata.AnnualMAR);
        writer.WriteNumber("annualPeriods", metadata.AnnualPeriods);
        writer.WriteNumber("bootstrapSeed", metadata.BootstrapSeed);
        writer.WriteNumber("bootstrapIterations", metadata.BootstrapIterations);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteConditions(System.Text.Json.Utf8JsonWriter writer, BoundBacktestConditions conditions)
    {
        writer.WriteStartArray("conditionRoots");
        foreach (BacktestRootSummary root in conditions.Roots)
        {
            writer.WriteStartObject();
            writer.WriteString("root", root.Name);
            writer.WriteNumber("comparisonCount", root.ComparisonCount);
            writer.WriteString("text", root.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteTrades(System.Text.Json.Utf8JsonWriter writer, BacktestResult result, int maxTrades, bool compactRows)
    {
        int returned = Math.Min(maxTrades, result.Trades.Length);
        writer.WriteStartObject("trades");
        writer.WriteNumber("totalTrades", result.Trades.Length);
        writer.WriteNumber("returnedTrades", returned);
        writer.WriteBoolean("truncated", returned < result.Trades.Length);
        writer.WriteString("order", "in the order the engine closed them");
        writer.WriteStartArray("items");
        if (!compactRows)
        {
            for (int i = 0; i < returned; i++)
            {
                BacktestTrade trade = result.Trades[i];
                writer.WriteStartObject();
                writer.WriteNumber("tradeId", trade.TradeId);
                writer.WriteString("side", trade.Side.ToString());
                writer.WriteString("entryDate", ToolJson.FormatDate(trade.EntryTime));
                writer.WriteNumber("entryPrice", trade.EntryPrice);
                writer.WriteString("exitDate", ToolJson.FormatDate(trade.ExitTime));
                writer.WriteNumber("exitPrice", trade.ExitPrice);
                writer.WriteNumber("quantity", trade.Quantity);
                writer.WriteNumber("closedGross", trade.ClosedGross);
                writer.WriteNumber("closedNet", trade.ClosedNet);
                writer.WriteNumber("entryFee", trade.EntryFee);
                writer.WriteNumber("exitFee", trade.ExitFee);
                writer.WriteNumber("holdingBars", trade.HoldingBars);
                writer.WriteBoolean("isForcedLiquidation", trade.IsForcedLiquidation);
                writer.WriteString("entrySignal", trade.EntrySignal);
                writer.WriteString("exitReason", trade.ExitReason);
                ToolJson.WriteNullableNumber(writer, "stopLevelAtExit", trade.StopLevelAtExit);
                writer.WriteNumber("mfePrice", trade.MfePrice);
                writer.WriteNumber("maePrice", trade.MaePrice);
                ToolJson.WriteNullableNumber(writer, "mfePercent", trade.MfePercent);
                ToolJson.WriteNullableNumber(writer, "maePercent", trade.MaePercent);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        if (compactRows)
        {
            ToolJson.WriteStringArray(writer, "columns", TradeColumns);
            writer.WriteStartArray("rows");
            for (int i = 0; i < returned; i++)
            {
                BacktestTrade trade = result.Trades[i];
                writer.WriteStartArray();
                writer.WriteNumberValue(trade.TradeId);
                writer.WriteStringValue(trade.Side.ToString());
                writer.WriteStringValue(ToolJson.FormatDate(trade.EntryTime));
                writer.WriteNumberValue(trade.EntryPrice);
                writer.WriteStringValue(ToolJson.FormatDate(trade.ExitTime));
                writer.WriteNumberValue(trade.ExitPrice);
                writer.WriteNumberValue(trade.Quantity);
                writer.WriteNumberValue(trade.ClosedGross);
                writer.WriteNumberValue(trade.ClosedNet);
                writer.WriteNumberValue(trade.EntryFee);
                writer.WriteNumberValue(trade.ExitFee);
                writer.WriteNumberValue(trade.HoldingBars);
                writer.WriteBooleanValue(trade.IsForcedLiquidation);
                writer.WriteStringValue(trade.EntrySignal);
                writer.WriteStringValue(trade.ExitReason);
                ToolJson.WriteNullableValue(writer, trade.StopLevelAtExit);
                writer.WriteNumberValue(trade.MfePrice);
                writer.WriteNumberValue(trade.MaePrice);
                ToolJson.WriteNullableValue(writer, trade.MfePercent);
                ToolJson.WriteNullableValue(writer, trade.MaePercent);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private void WriteEngineWarnings(System.Text.Json.Utf8JsonWriter writer, BacktestResult result)
    {
        writer.WriteStartObject("engineWarnings");
        writer.WriteNumber("total", result.Warnings.Length);
        writer.WriteStartArray("byCode");
        foreach (IGrouping<WarningCode, BacktestWarning> group in result.Warnings.GroupBy(warning => warning.Code).OrderBy(group => group.Key))
        {
            writer.WriteStartObject();
            writer.WriteString("code", group.Key.ToString());
            writer.WriteNumber("count", group.Count());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("items");
        foreach (BacktestWarning warning in result.Warnings.Take(_limits.MaxReturnedWarnings))
        {
            writer.WriteStartObject();
            writer.WriteString("code", warning.Code.ToString());
            writer.WriteString("date", ToolJson.FormatDate(warning.Timestamp));
            writer.WriteNumber("barIndex", warning.BarIndex);
            writer.WriteString("message", warning.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
