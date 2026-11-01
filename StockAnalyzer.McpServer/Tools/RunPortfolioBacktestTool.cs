using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_run_portfolio_backtest: runs a multi-symbol basket backtest using the Independent Sleeves model (spec §6.1).
/// Total capital is divided equally across symbols, each sleeve is evaluated independently using the Core backtest engine
/// via RunBacktestBatchAsync, and results are composited via PortfolioEquityComposer.
/// </summary>
internal sealed class RunPortfolioBacktestTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string AllocationArgument = "allocation";
    private const string MaxConcurrentPositionsArgument = "maxConcurrentPositions";
    private const string ConditionsArgument = RunBacktestTool.ConditionsArgument;
    private const string TemplateIdArgument = "templateId";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    private const string TimeFrameArgument = "timeFrame";
    private const string MaxTradesPerSymbolArgument = "maxReturnedTradesPerSymbol";
    private const string EquityCurveArgument = BacktestDiagnostics.EquityCurveArgument;

    private static readonly string[] AllowedEquityCurveResolutions = { "None", "Weekly", "Monthly" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly SymbolResolver _symbols;
    private readonly BacktestConditionBinder _conditions;
    private readonly McpLimits _limits;

    public RunPortfolioBacktestTool(
        IReadOnlyStockAnalyzerGateway gateway,
        ITickerGroupResolver groupResolver,
        SymbolResolver symbols,
        BacktestConditionBinder conditions,
        McpLimits limits)
    {
        _gateway = gateway;
        _groupResolver = groupResolver;
        _symbols = symbols;
        _conditions = conditions;
        _limits = limits;
    }

    public string Name => "sa_run_portfolio_backtest";

    public string Description =>
        "Runs an independent-sleeves portfolio backtest across multiple symbols with equal capital allocation, reporting individual sleeve results and composite portfolio performance. " +
        "Give either symbols (1 to " + _limits.MaxPortfolioBacktestSymbols + " tickers) or groupId (mutually exclusive). " +
        "Give either conditions or templateId (mutually exclusive). " +
        "Capital is equally divided across sleeves: sleeveCapital = initialCapital / N (decimal precision). " +
        "Each sleeve runs independently through the core backtest engine. Symbols with insufficient data remain as cash. " +
        "Outputs individual sleeve metrics and composite portfolio equity, return, drawdown and pooled trade statistics. " +
        "allocation defaults to 'Equal'; maxConcurrentPositions is not accepted in the independent sleeves model.";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"AAPL\",\"MSFT\"],\"timeFrame\":\"D1\",\"conditions\":{\"entryLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":{\"price\":\"Close\",\"offset\":1}},\"exitLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"LessThan\",\"right\":{\"price\":\"Close\",\"offset\":1}}}}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Symbols to test (1 to {_limits.MaxPortfolioBacktestSymbols}). Mutually exclusive with groupId.", minItems: 1)
        .String(GroupIdArgument, "Guid of a saved ticker group (from sa_list_ticker_groups) to test. Mutually exclusive with symbols.")
        .FreeObject(ConditionsArgument, "The entry, exit and reverse conditions. Exactly one of conditions and templateId.")
        .String(TemplateIdArgument, "Id of a saved backtest-condition template (sa_list_templates with templateType backtestCondition). Exactly one of conditions and templateId.")
        .String(AllocationArgument, "Capital allocation model across symbols. Only 'Equal' is supported.", allowedValues: new[] { "Equal" })
        .String(StartArgument, "First evaluated day, yyyy-MM-dd. Default: start of stored history.")
        .String(EndArgument, "Last evaluated day, yyyy-MM-dd. Default: latest stored bar.")
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default {ReadOnlyGatewayContract.BacktestTimeFrame}.", allowedValues: ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Select(t => t.ToString()).ToArray())
        .FreeObject(BacktestSettingsBinder.AccountArgument, "Account capital, cost, sizing and margin settings. initialCapital represents total portfolio funds.")
        .FreeObject(BacktestSettingsBinder.RiskArgument, "Optional stop-loss, take-profit and dynamic exits: {stopLossRatio, takeProfitRatio, dynamicExits}.")
        .FreeObject(BacktestSettingsBinder.ReportArgument, "Report settings: {annualRiskFreeRate, annualMar, annualPeriodsD1, bootstrapSeed, bootstrapIterations}.")
        .Integer(MaxTradesPerSymbolArgument, $"Most trades to list per symbol (default 0; maximum {_limits.MaxReturnedTrades}).", defaultValue: 0, minimum: 0, maximum: _limits.MaxReturnedTrades)
        .String(EquityCurveArgument, "Add composite equity curve: None (default), Weekly or Monthly.", allowedValues: AllowedEquityCurveResolutions)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept symbols that are not in the ticker list but have stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // 1. Argument validation: Allocation & Concurrent Positions
        if (arguments.TryGetProperty(MaxConcurrentPositionsArgument, out _))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{MaxConcurrentPositionsArgument} is not supported in the independent sleeves model.",
                MaxConcurrentPositionsArgument,
                "The independent sleeves model evaluates each symbol with isolated capital.");
        }

        string allocation = ToolArgs.String(arguments, AllocationArgument) ?? "Equal";
        if (!string.Equals(allocation, "Equal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Allocation '{allocation}' is not supported. Only 'Equal' allocation is supported.",
                AllocationArgument,
                "Omit allocation or use 'Equal'.");
        }

        // 2. TimeFrame & Dates
        TimeFrame timeFrame = RunBacktestTool.ResolveTimeFrame(arguments);
        DateOnly? requestedStart = ToolArgs.Date(arguments, StartArgument);
        DateOnly? requestedEnd = ToolArgs.Date(arguments, EndArgument);
        if (requestedStart is { } start && requestedEnd is { } end && start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        // 3. Equity Curve Resolution
        string? curveText = ToolArgs.String(arguments, EquityCurveArgument);
        EquityCurveResolution? equityCurveResolution = null;
        if (curveText is not null && !string.Equals(curveText, "None", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(curveText, "Weekly", StringComparison.OrdinalIgnoreCase))
            {
                equityCurveResolution = EquityCurveResolution.Weekly;
            }
            else if (string.Equals(curveText, "Monthly", StringComparison.OrdinalIgnoreCase))
            {
                equityCurveResolution = EquityCurveResolution.Monthly;
            }
            else
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{EquityCurveArgument} value '{curveText}' is not supported for portfolio backtest. Use 'None', 'Weekly', or 'Monthly'.",
                    EquityCurveArgument);
            }
        }

        if (equityCurveResolution == EquityCurveResolution.Monthly && timeFrame == TimeFrame.W1)
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"{EquityCurveArgument} Monthly cannot be used with weekly bars (timeFrame W1): a weekly bar cannot be cut at a month end.",
                EquityCurveArgument,
                "Use includeEquityCurve Weekly, or omit it.");
        }

        int maxTradesPerSymbol = ToolArgs.NonNegativeWithin(arguments, MaxTradesPerSymbolArgument, 0, _limits.MaxReturnedTrades);
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;

        // 4. Universe Symbols Resolution (symbols vs groupId)
        (List<string> universe, List<string> universeWarnings) = await ResolveUniverseSymbolsAsync(arguments, allowUnregistered, cancellationToken).ConfigureAwait(false);
        int symbolCount = universe.Count;

        // 5. Conditions vs TemplateId Binding
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
        }

        // 6. Capital Allocation per Sleeve (D-11c: exact decimal division)
        decimal totalCapital = settings.Configuration.InitialCapital;
        decimal sleeveCapital = totalCapital / symbolCount;
        decimal allocatedTotal = sleeveCapital * symbolCount;
        var sleeveConfig = WithInitialCapital(settings.Configuration, sleeveCapital);

        // 7. Resolve each symbol and prepare snapshots
        var sleeveHolders = new SleeveHolder[symbolCount];
        var validSnapshots = new List<(int Index, BacktestRunSnapshot Snapshot)>();

        for (int i = 0; i < symbolCount; i++)
        {
            string symbol = universe[i];

            IReadOnlyList<CandleData> stored = await _gateway.GetCandlesAsync(symbol, timeFrame, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
            if (stored.Count == 0)
            {
                sleeveHolders[i] = new SleeveHolder(symbol, sleeveCapital, false, "InsufficientData", $"No stored {timeFrame} bars exist for {symbol}.");
                continue;
            }

            DateOnly effectiveStart = requestedStart ?? DateOnly.FromDateTime(stored[0].Timestamp);
            DateOnly effectiveEnd = requestedEnd ?? DateOnly.FromDateTime(stored[^1].Timestamp);
            if (effectiveStart > effectiveEnd)
            {
                sleeveHolders[i] = new SleeveHolder(symbol, sleeveCapital, false, "InsufficientData", $"{StartArgument} is after {EndArgument} for stored range.");
                continue;
            }

            var snapshot = new BacktestRunSnapshot(
                symbol,
                timeFrame,
                RunBacktestTool.ToUtc(effectiveStart),
                RunBacktestTool.ToUtc(effectiveEnd),
                sleeveConfig,
                ImmutableArray<BacktestConditionEntry>.Empty,
                settings.RiskManagement,
                ImmutableArray<StrategyIndicatorRequest>.Empty,
                settings.ReportDefaults,
                settings.BootstrapSeed,
                settings.BootstrapIterations,
                conditions.Tree);

            validSnapshots.Add((i, snapshot));
        }

        // 8. Execute batch backtests
        if (validSnapshots.Count > 0)
        {
            IReadOnlyList<BacktestRunSnapshot> snapshotsToRun = validSnapshots.Select(v => v.Snapshot).ToList();
            IReadOnlyList<BacktestEvaluationArtifact> artifacts;
            try
            {
                artifacts = await _gateway.RunBacktestBatchAsync(
                    snapshotsToRun,
                    _limits.OptimizationDegreeOfParallelism,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException error)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"The backtest input was rejected: {error.Message.Split(Environment.NewLine)[0]}", hint: "Check the conditions.");
            }

            for (int k = 0; k < validSnapshots.Count; k++)
            {
                int originalIndex = validSnapshots[k].Index;
                string symbol = validSnapshots[k].Snapshot.Symbol;
                BacktestEvaluationArtifact artifact = artifacts[k];

                if (artifact.Result.IsInsufficientData || artifact.Metadata.SampleCount == 0)
                {
                    sleeveHolders[originalIndex] = new SleeveHolder(
                        symbol,
                        sleeveCapital,
                        false,
                        "InsufficientData",
                        "Insufficient data to evaluate backtest.",
                        artifact);
                }
                else
                {
                    sleeveHolders[originalIndex] = new SleeveHolder(
                        symbol,
                        sleeveCapital,
                        true,
                        "Succeeded",
                        null,
                        artifact);
                }
            }
        }

        // 9. Compose Portfolio Equity
        var sleeveInputs = new List<SleeveBacktestInput>(symbolCount);
        for (int i = 0; i < symbolCount; i++)
        {
            SleeveHolder h = sleeveHolders[i];
            sleeveInputs.Add(new SleeveBacktestInput(
                h.Symbol,
                h.SleeveCapital,
                h.IsSuccess,
                h.Reason,
                GetEvaluatedEquityPoints(h.Artifact),
                h.Artifact?.Result.Trades ?? ImmutableArray<BacktestTrade>.Empty));
        }

        DateTime? effectiveInitialTimestamp = requestedStart is { } s ? RunBacktestTool.ToUtc(s) : null;
        CompositePortfolioResult composite = PortfolioEquityComposer.Compose(totalCapital, sleeveInputs, effectiveInitialTimestamp);

        // Pre-sample and validate equity curve (if requested)
        ImmutableArray<EquityCurvePoint>? sampledCurve = null;
        if (equityCurveResolution is { } resolution && !composite.CompositeEquityPoints.IsEmpty)
        {
            try
            {
                var curve = EquityCurveSampler.Sample(
                    composite.AllocatedTotal,
                    composite.CompositeEquityPoints,
                    resolution);
                BacktestDiagnostics.EnsureCurveFits(resolution, curve.Length, _limits);
                sampledCurve = curve;
            }
            catch (OverflowException)
            {
                // Degrades gracefully per Plan 03
            }
            catch (ArithmeticException)
            {
                // Degrades gracefully per Plan 03
            }
        }

        // 10. Format Output JSON
        int succeededCount = sleeveHolders.Count(h => h.IsSuccess);
        int failedCount = symbolCount - succeededCount;

        // Ordinal sort for symbolResults
        var orderedSleeves = sleeveHolders.OrderBy(h => h.Symbol, StringComparer.Ordinal).ToList();

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("method", "IndependentSleeves");
            writer.WriteString("allocation", "Equal");
            writer.WriteNumber("symbolCount", symbolCount);
            writer.WriteNumber("succeededCount", succeededCount);
            writer.WriteNumber("failedCount", failedCount);
            writer.WriteNumber("allocatedTotal", allocatedTotal);

            // symbolResults
            writer.WriteStartArray("symbolResults");
            foreach (SleeveHolder sleeve in orderedSleeves)
            {
                WriteSymbolResult(writer, sleeve, maxTradesPerSymbol);
            }
            writer.WriteEndArray();

            // composite
            WriteComposite(writer, composite, equityCurveResolution, sampledCurve);

            // notes
            writer.WriteStartArray("notes");
            writer.WriteStringValue("Independent sleeves model: Capital is equally divided into isolated sleeves with no shared margin or cross-asset position limits.");
            writer.WriteStringValue("Composite metrics reflect the direct summation of sleeves; risk-adjusted metrics (Sharpe/Sortino) are reported at symbol level only.");
            if (failedCount > 0)
            {
                writer.WriteStringValue($"{failedCount} sleeve(s) failed or had insufficient data; their allocated capital remains as cash without reallocation.");
            }
            foreach (string warning in universeWarnings)
            {
                writer.WriteStringValue(warning);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        });
    }

    private static void WriteSymbolResult(Utf8JsonWriter writer, SleeveHolder sleeve, int maxTrades)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", sleeve.Symbol);
        writer.WriteString("status", sleeve.Status);
        if (sleeve.Reason is { } reason)
        {
            writer.WriteString("reason", reason);
        }

        writer.WriteNumber("sleeveCapital", sleeve.SleeveCapital);

        if (sleeve.IsSuccess && sleeve.Artifact is { } artifact)
        {
            BacktestResult result = artifact.Result;
            BacktestReport? report = artifact.Report;

            decimal finalEq = !result.EquityPoints.IsEmpty ? result.EquityPoints[^1].Equity : sleeve.SleeveCapital;
            writer.WriteNumber("finalEquity", finalEq);

            BacktestReportJson.WriteMetric(writer, "totalReturn", report?.TotalReturn);
            writer.WriteNumber("totalTrades", report?.TotalTrades ?? result.Trades.Length);
            BacktestReportJson.WriteMetric(writer, "winRate", report?.WinRate);
            BacktestReportJson.WriteMetric(writer, "profitFactor", report?.ProfitFactor);
            BacktestReportJson.WriteMetric(writer, "annualizedSharpe", report?.AnnualizedSharpe);
            BacktestReportJson.WriteMetric(writer, "maxDrawdown", report?.MaxDrawdown);
            BacktestReportJson.WriteRunFingerprint(writer, artifact.RunFingerprint);

            if (maxTrades > 0 && !result.Trades.IsEmpty)
            {
                int tradesToTake = Math.Min(maxTrades, result.Trades.Length);
                writer.WriteStartArray("trades");
                for (int t = 0; t < tradesToTake; t++)
                {
                    BacktestTrade trade = result.Trades[t];
                    writer.WriteStartObject();
                    writer.WriteNumber("tradeId", trade.TradeId);
                    writer.WriteString("side", trade.Side.ToString());
                    writer.WriteString("entryTime", ToolJson.FormatDate(trade.EntryTime));
                    writer.WriteNumber("entryPrice", trade.EntryPrice);
                    writer.WriteString("exitTime", ToolJson.FormatDate(trade.ExitTime));
                    writer.WriteNumber("exitPrice", trade.ExitPrice);
                    writer.WriteNumber("quantity", trade.Quantity);
                    writer.WriteNumber("closedNet", trade.ClosedNet);
                    writer.WriteNumber("holdingBars", trade.HoldingBars);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
        }
        else
        {
            writer.WriteNumber("finalEquity", sleeve.SleeveCapital);
            BacktestReportJson.WriteMetric(writer, "totalReturn", null);
            writer.WriteNumber("totalTrades", 0);
            BacktestReportJson.WriteMetric(writer, "winRate", null);
            BacktestReportJson.WriteMetric(writer, "profitFactor", null);
            BacktestReportJson.WriteMetric(writer, "annualizedSharpe", null);
            BacktestReportJson.WriteMetric(writer, "maxDrawdown", null);
            BacktestReportJson.WriteRunFingerprint(writer, null);
        }

        writer.WriteEndObject();
    }

    private static void WriteComposite(
        Utf8JsonWriter writer,
        CompositePortfolioResult composite,
        EquityCurveResolution? curveResolution,
        ImmutableArray<EquityCurvePoint>? sampledCurve)
    {
        writer.WriteStartObject("composite");
        writer.WriteNumber("allocatedTotal", composite.AllocatedTotal);
        writer.WriteNumber("finalEquity", composite.FinalEquity);
        writer.WriteNumber("totalReturn", composite.TotalReturn);
        writer.WriteNumber("maxDrawdownRatio", composite.MaxDrawdownRatio);

        if (composite.WorstEpisode is { } episode)
        {
            writer.WriteStartObject("worstEpisode");
            BacktestDiagnostics.WriteEpisodeFields(writer, episode);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("worstEpisode");
        }

        writer.WriteNumber("pooledTradeCount", composite.PooledTradeCount);
        if (composite.PooledWinRate is { } pwr)
        {
            writer.WriteNumber("pooledWinRate", pwr);
        }
        else
        {
            writer.WriteNull("pooledWinRate");
        }

        if (sampledCurve is { } curve && curveResolution is { } resolution)
        {
            BacktestDiagnostics.WriteEquityCurve(writer, resolution, curve);
        }

        writer.WriteEndObject();
    }

    private static BacktestConfiguration WithInitialCapital(BacktestConfiguration baseConfig, decimal sleeveCapital)
    {
        var config = new BacktestConfiguration
        {
            ExecutionModel = baseConfig.ExecutionModel,
            InitialCapital = sleeveCapital,
            CommissionFlat = baseConfig.CommissionFlat,
            CommissionPerUnit = baseConfig.CommissionPerUnit,
            SlippageRatio = baseConfig.SlippageRatio,
            SizingModel = baseConfig.SizingModel,
            SizingParameter = baseConfig.SizingParameter,
            InitialMarginRatio = baseConfig.InitialMarginRatio,
            MaintenanceMarginRatio = baseConfig.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = baseConfig.LiquidationPenaltyRatio,
        };
        config.Validate();
        return config;
    }

    private static ImmutableArray<EquityPoint> GetEvaluatedEquityPoints(BacktestEvaluationArtifact? artifact)
    {
        if (artifact is null)
        {
            return ImmutableArray<EquityPoint>.Empty;
        }

        ImmutableArray<EquityPoint> all = artifact.Result.EquityPoints;
        int skipped = Math.Min(artifact.Metadata.HistoryStartIndex, all.Length);
        return skipped == 0 ? all : all.RemoveRange(0, skipped);
    }

    private async Task<(List<string> Symbols, List<string> Warnings)> ResolveUniverseSymbolsAsync(
        JsonElement arguments,
        bool allowUnregistered,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();

        if (requested is not null && groupIdRaw is not null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SymbolsArgument}' and '{GroupIdArgument}' cannot be combined.", GroupIdArgument, "Send one of them.");
        }

        if (requested is null && groupIdRaw is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Give exactly one of '{SymbolsArgument}' or '{GroupIdArgument}'.", SymbolsArgument);
        }

        var warnings = new List<string>();

        if (requested is not null)
        {
            if (requested.Count > _limits.MaxPortfolioBacktestSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxPortfolioBacktestSymbols} symbols per call.",
                    SymbolsArgument);
            }

            var resolvedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinctList = new List<string>(requested.Count);
            var offList = new SortedSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < requested.Count; i++)
            {
                string raw = requested[i];
                string parameter = $"{SymbolsArgument}[{i}]";
                ResolvedSymbol resolved = await _symbols.ResolveAsync(raw, parameter, allowUnregistered, cancellationToken).ConfigureAwait(false);
                if (!resolved.IsRegistered)
                {
                    offList.Add(resolved.Symbol);
                }

                if (resolvedSet.Add(resolved.Symbol))
                {
                    distinctList.Add(resolved.Symbol);
                }
            }

            if (distinctList.Count < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "At least 1 symbol is required.", SymbolsArgument);
            }

            if (offList.Count > 0)
            {
                warnings.Add(SymbolResolver.OffListWarning(offList));
            }

            return (distinctList, warnings);
        }

        // groupId
        (Guid _, GroupMembers group) = await GroupMembersLookup.ResolveAsync(
            _groupResolver, groupIdRaw!, GroupIdArgument, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);

        var groupDistinct = group.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (groupDistinct.Count > _limits.MaxPortfolioBacktestSymbols)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The group contains {groupDistinct.Count} symbols, exceeding the limit of {_limits.MaxPortfolioBacktestSymbols}.",
                GroupIdArgument);
        }

        if (groupDistinct.Count < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "The group contains no symbols.", GroupIdArgument);
        }

        if (group.UnregisteredCount > 0)
        {
            warnings.Add($"{group.UnregisteredCount} member(s) of the group are not registered and were left out.");
        }

        return (groupDistinct, warnings);
    }

    private sealed record SleeveHolder(
        string Symbol,
        decimal SleeveCapital,
        bool IsSuccess,
        string Status,
        string? Reason,
        BacktestEvaluationArtifact? Artifact = null);
}
