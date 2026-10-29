using StockAnalyzer.Core.Services;
using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Engine;
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
    private const string ConditionsArgument = "conditions";
    private const string TemplateIdArgument = "templateId";
    private const string MaxTradesArgument = "maxReturnedTrades";

    /// <summary>The saved template a run used (its name is echoed with its id).</summary>
    private sealed record TemplateReference(Guid Id, string Name);

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly BacktestConditionBinder _conditions;
    private readonly McpLimits _limits;

    public RunBacktestTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, BacktestConditionBinder conditions, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _conditions = conditions;
        _limits = limits;
    }

    public string Name => "sa_run_backtest";

    public string Description =>
        "Runs one backtest on the stored bars of one symbol (daily D1, weekly W1 or monthly MN1) with the application's own backtest engine (the Backtest window's Legacy execution model) and returns the performance report, the trades and the run fingerprint. " +
        "Call sa_get_capabilities first for indicator types, parameters and series; indicators that look ahead are not allowed. " +
        "Give either conditions or templateId: templateId runs a saved backtest-condition template (ids from sa_list_templates with templateType backtestCondition) " +
        "with its own conditions and risk settings; riskManagement must then be omitted, and the template's sides must use the evaluated time frame. " +
        $"conditions has up to six roots: {string.Join(", ", BacktestConditionBinder.RootKeys)}. entryLong/entryShort open a long/short position, exitLong/exitShort close it, " +
        "reverseLong flips a short position to long and reverseShort flips a long position to short (a reverse root acts only while a position is held). Give at least one root, normally an entry. " +
        "A root is a group {operator: And|Or, children: [...]} of groups and comparisons, or one comparison. " +
        $"A comparison is {{left: side, comparison: {string.Join("|", BacktestConditionBinder.ComparisonNames)}, right: number | side}}. " +
        "A side names exactly one of indicator (with optional parameters and outputSeriesName) or price (Close, Open, High, Low, ...), plus an optional offset (bars back from the evaluated bar, default 0). " +
        "Example: {\"entryLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30}," +
        "\"exitLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"GreaterThan\",\"right\":70}}. " +
        "startDate and endDate (yyyy-MM-dd, inclusive) default to the stored history (at most the latest " + _limits.MaxCandlesPerRequest + " bars); bars before startDate warm the indicators up. " +
        "account sets initialCapital, commissionFlat, commissionPerUnit, slippageRatio, sizingModel (FixedQuantity or PercentOfEquity), sizingParameter, initialMarginRatio, maintenanceMarginRatio and liquidationPenaltyRatio; " +
        "riskManagement sets stopLossRatio and takeProfitRatio of the entry price (0.05 = 5%); report sets annualRiskFreeRate, annualMar, annualPeriodsD1 (daily bars per year, for the annualised metrics), bootstrapSeed and bootstrapIterations (the report's bootstrap confidence intervals). " +
        "Omitted values are the Backtest window's factory defaults, not the values saved in the window; the result echoes every effective value. " +
        $"The supported time frames are {string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}; the Legacy execution model is used; there is no equity curve. " +
        $"The result lists the first maxReturnedTrades trades in the order the engine closed them (default and maximum {_limits.MaxReturnedTrades}; totalTrades is the true count) and engine warnings by code. " +
        "allowUnregistered true also accepts a symbol that is not in the ticker list but has stored bars; it is named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. " +
        "Metrics keep status/value/unit/reason: a value is present only when status is Valid. The tool never changes anything and never fetches data.";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"conditions\":{\"entryLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30},\"exitLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"GreaterThan\",\"right\":70}},\"maxReturnedTrades\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "The symbol to test (a ticker from sa_list_tickers).", required: true)
        .FreeObject(ConditionsArgument, "The entry, exit and reverse conditions (see the tool description). Exactly one of conditions and templateId.")
        .String(TemplateIdArgument, "Id of a saved backtest-condition template (sa_list_templates with templateType backtestCondition). Exactly one of conditions and templateId.")
        .String(StartArgument, "First evaluated day, yyyy-MM-dd. Default: the start of the stored history.")
        .String(EndArgument, "Last evaluated day, yyyy-MM-dd. Default: the latest stored bar.")
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default {ReadOnlyGatewayContract.BacktestTimeFrame}.", allowedValues: ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Select(t => t.ToString()).ToArray())
        .FreeObject(BacktestSettingsBinder.AccountArgument, "Capital, cost, sizing and margin settings (see the tool description). Omitted values are the factory defaults.")
        .FreeObject(BacktestSettingsBinder.RiskArgument, "Optional stop-loss and take-profit as ratios of the entry price: {stopLossRatio, takeProfitRatio}.")
        .FreeObject(BacktestSettingsBinder.ReportArgument, "Report settings: {annualRiskFreeRate, annualMar} (simple rates, 0.02 = 2%), {annualPeriodsD1} (daily bars per year), {bootstrapSeed, bootstrapIterations} (whole numbers). Omitted values are the factory defaults.")
        .Integer(MaxTradesArgument, $"Most trades to list. Default and maximum {_limits.MaxReturnedTrades}; 0 lists none.", defaultValue: _limits.MaxReturnedTrades, minimum: 0, maximum: _limits.MaxReturnedTrades)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
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
            conditions = _conditions.Bind(arguments.GetProperty(ConditionsArgument), ConditionsArgument);
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
        catch (ArgumentException error)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"The backtest input was rejected: {error.Message.Split(Environment.NewLine)[0]}", hint: "Check the dates and the conditions.");
        }

        return ToolJson.Build(writer => Write(writer, artifact, symbol, timeFrame, effectiveStart, effectiveEnd, requestedStart is null || requestedEnd is null, settings, conditions, maxTrades, template, symbolWarnings));
    }

    private static DateTime ToUtc(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static TimeFrame ResolveTimeFrame(JsonElement arguments)
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
        BoundBacktestConditions conditions,
        int maxTrades,
        TemplateReference? template,
        IReadOnlyList<string> symbolWarnings)
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

        WriteSettings(writer, settings, metadata);
        WriteConditions(writer, conditions);

        writer.WriteStartObject("equity");
        writer.WriteNumber("initial", settings.Configuration.InitialCapital);
        if (!result.EquityPoints.IsEmpty)
        {
            writer.WriteNumber("final", result.EquityPoints[^1].Equity);
        }

        writer.WriteEndObject();

        writer.WriteString("reproducibilityHash", Convert.ToHexString(result.ReproducibilityHash));
        writer.WriteStartObject("runFingerprint");
        writer.WriteBoolean("isAvailable", artifact.RunFingerprint.IsAvailable);
        if (artifact.RunFingerprint.Sha256 is { } sha)
        {
            writer.WriteString("sha256", sha);
        }

        writer.WriteNumber("schemaVersion", artifact.RunFingerprint.SchemaVersion);
        writer.WriteNumber("executionSemanticsVersion", artifact.RunFingerprint.ExecutionSemanticsVersion);
        if (artifact.RunFingerprint.UnavailableReason is { } reason)
        {
            writer.WriteString("unavailableReason", reason);
        }

        writer.WriteEndObject();

        if (artifact.Report is { } report)
        {
            writer.WritePropertyName("report");
            BacktestReportJson.Write(writer, report);
        }

        WriteTrades(writer, result, maxTrades);
        WriteEngineWarnings(writer, result);
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

    private static void WriteSettings(System.Text.Json.Utf8JsonWriter writer, BoundBacktestSettings settings, BacktestEvaluationMetadata metadata)
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

    private void WriteTrades(System.Text.Json.Utf8JsonWriter writer, BacktestResult result, int maxTrades)
    {
        int returned = Math.Min(maxTrades, result.Trades.Length);
        writer.WriteStartObject("trades");
        writer.WriteNumber("totalTrades", result.Trades.Length);
        writer.WriteNumber("returnedTrades", returned);
        writer.WriteBoolean("truncated", returned < result.Trades.Length);
        writer.WriteString("order", "in the order the engine closed them");
        writer.WriteStartArray("items");
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
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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
