using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_optimize_backtest: a grid search over the numbers of one backtest. The conditions (and the account, risk and report settings) carry placeholders
/// {"$param": name}; every combination of the parameter ranges is bound with the same binders as sa_run_backtest, run by the gateway's batch path (the
/// engine and the report are the application's own, nothing is redefined here) and ranked by one metric of the existing report.
/// </summary>
internal sealed class OptimizeBacktestTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    private const string ConditionsArgument = BacktestSweep.ConditionsArgument;
    private const string ObjectiveArgument = BacktestSweep.ObjectiveArgument;
    private const string DirectionArgument = BacktestSweep.DirectionArgument;
    private const string TopKArgument = "topK";
    private const string OutOfSampleArgument = "outOfSampleStartDate";

    private const string Maximize = BacktestSweep.Maximize;
    private const string Minimize = BacktestSweep.Minimize;

    /// <summary>The status written for an out-of-sample run that produced no report.</summary>
    private const string StatusNoReport = "NoReport";

    /// <summary>The report metrics every ranked combination lists besides its objective (names of <see cref="BacktestReport"/> properties).</summary>
    private static readonly string[] SummaryMetrics =
    {
        nameof(BacktestReport.TotalPnL), nameof(BacktestReport.TotalReturn), nameof(BacktestReport.MaxDrawdown), nameof(BacktestReport.WinRate),
        nameof(BacktestReport.ProfitFactor), nameof(BacktestReport.AnnualizedSharpe),
    };

    private readonly BacktestSweep _sweep;
    private readonly McpLimits _limits;

    public OptimizeBacktestTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, BacktestConditionBinder conditions, McpLimits limits)
    {
        _sweep = new BacktestSweep(gateway, symbols, conditions, limits);
        _limits = limits;
    }

    public string Name => "sa_optimize_backtest";

    public string Description =>
        "Grid search over the numbers of one backtest: runs every combination of parameterRanges with the application's own backtest engine (the same one as sa_run_backtest) and returns the best combinations by one metric of the backtest report. " +
        "Give conditions exactly as for sa_run_backtest, but put {\"$param\": \"<name>\"} where a number goes: an indicator parameter value, the right-hand constant of a comparison, an offset, or a number of riskManagement, account or report. " +
        "parameterRanges lists {name, start, end, step}, one per name used in $param (an unused name or an undefined $param is INVALID_ARGUMENT); a parameter takes start, start + step, ... up to the largest value not above end. " +
        "An integer parameter (Period, offset) that meets a fractional value is INVALID_ARGUMENT. templateId is not accepted because a template cannot carry placeholders. " +
        $"The combinations are numbered from 0 in the order of parameterRanges, the first range changing slowest. At most {_limits.MaxOptimizationParameters} ranges and {_limits.MaxOptimizationCombinations} combinations (the product of the value counts); more is LIMIT_EXCEEDED, the grid is never thinned. " +
        $"objective is the name of a report metric as sa_run_backtest writes it (call sa_get_capabilities for the list: {string.Join(", ", OptimizationObjectives.Names)}); no new formula is defined. " +
        "Only combinations whose objective has status Valid are ranked: the best value first (objectiveDirection Maximize, the default, or Minimize, for example for maxDrawdown), equal values in combination order; the others are counted in invalidCombinationCount. If none is Valid the ranking is empty (not an error). " +
        $"topK (1 to {_limits.MaxOptimizationTopK}) is the number of ranked combinations returned. " +
        "sensitivity of a ranked combination looks at the neighbours that differ in one parameter by one step (up to two per parameter): neighborCount is how many of them have a Valid objective, neighborMean and neighborMin are over those; it is null when none has. " +
        "outOfSampleStartDate (yyyy-MM-dd) splits the period: the combinations are ranked on startDate to the day before it, and each returned combination is also run on that date to endDate (outOfSample: objectiveValue and the metric status). " +
        "symbol, timeFrame (D1, W1 or MN1), startDate, endDate, account, riskManagement, report and allowUnregistered mean what they mean for sa_run_backtest. " +
        "A call that runs out of time or is cancelled returns an error, never a partial ranking. The tool never changes anything and never fetches data.";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"conditions\":{\"entryLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":{\"$param\":\"period\"}}},\"comparison\":\"LessThan\",\"right\":30}," +
        "\"exitLong\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":{\"$param\":\"period\"}}},\"comparison\":\"GreaterThan\",\"right\":70}}," +
        "\"parameterRanges\":[{\"name\":\"period\",\"start\":5,\"end\":21,\"step\":2}],\"objective\":\"annualizedSharpe\",\"topK\":5}";

    public string InputSchemaJson => BacktestSweep.WithRangesAndObjective(ToolSchema.Object()
        .String(SymbolArgument, "The symbol to test (a ticker from sa_list_tickers).", required: true)
        .FreeObject(ConditionsArgument, "The conditions of sa_run_backtest with {\"$param\": name} placeholders where numbers go.", required: true))
        .Integer(TopKArgument, $"How many ranked combinations to return. Default and maximum {_limits.MaxOptimizationTopK}.", defaultValue: _limits.MaxOptimizationTopK, minimum: 1, maximum: _limits.MaxOptimizationTopK)
        .String(StartArgument, "First evaluated day, yyyy-MM-dd. Default: the start of the stored history.")
        .String(EndArgument, "Last evaluated day, yyyy-MM-dd. Default: the latest stored bar.")
        .String(OutOfSampleArgument, "Optional yyyy-MM-dd: rank on the days before it and also run the returned combinations from it to the end.")
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default {ReadOnlyGatewayContract.BacktestTimeFrame}.", allowedValues: ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Select(t => t.ToString()).ToArray())
        .FreeObject(BacktestSettingsBinder.AccountArgument, "Capital, cost, sizing and margin settings, as for sa_run_backtest; numbers may be placeholders.")
        .FreeObject(BacktestSettingsBinder.RiskArgument, "{stopLossRatio, takeProfitRatio}, as for sa_run_backtest; numbers may be placeholders.")
        .FreeObject(BacktestSettingsBinder.ReportArgument, "Report settings, as for sa_run_backtest; numbers may be placeholders.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // Everything the client sent is checked before any data is read.
        TimeFrame timeFrame = RunBacktestTool.ResolveTimeFrame(arguments);
        DateOnly? requestedStart = ToolArgs.Date(arguments, StartArgument);
        DateOnly? requestedEnd = ToolArgs.Date(arguments, EndArgument);
        DateOnly? outOfSampleStart = ToolArgs.Date(arguments, OutOfSampleArgument);
        if (requestedStart is { } start && requestedEnd is { } end && start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        ObjectiveMetric objective = OptimizationObjectives.Resolve(ToolArgs.String(arguments, ObjectiveArgument)!, ObjectiveArgument);
        bool minimize = string.Equals(ToolArgs.String(arguments, DirectionArgument), Minimize, StringComparison.Ordinal);
        int topK = ToolArgs.PositiveWithin(arguments, TopKArgument, _limits.MaxOptimizationTopK, _limits.MaxOptimizationTopK);
        CheckOutOfSampleAgainstRequested(outOfSampleStart, requestedStart, requestedEnd);

        BacktestParameterGrid grid = _sweep.ReadGrid(arguments.GetProperty(BacktestParameterGrid.RangesArgument));
        BoundCombinations bound = _sweep.Bind(arguments, grid, timeFrame);

        SweepSymbol resolved = await _sweep.ResolveSymbolAsync(arguments, SymbolArgument, timeFrame, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = resolved.Warnings;
        IReadOnlyList<CandleData> stored = resolved.StoredBars;

        DateOnly effectiveStart = requestedStart ?? DateOnly.FromDateTime(stored[0].Timestamp);
        DateOnly effectiveEnd = requestedEnd ?? DateOnly.FromDateTime(stored[^1].Timestamp);
        if (effectiveStart > effectiveEnd)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        if (outOfSampleStart is { } split && (split <= effectiveStart || split > effectiveEnd))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{OutOfSampleArgument} must be after the first evaluated day ({effectiveStart:yyyy-MM-dd}) and not after the last ({effectiveEnd:yyyy-MM-dd}).",
                OutOfSampleArgument);
        }

        DateOnly inSampleEnd = outOfSampleStart is { } cut ? cut.AddDays(-1) : effectiveEnd;
        SweepRun[] inSample = await _sweep.RunAsync(
            symbol, timeFrame, bound, Enumerable.Range(0, grid.CombinationCount).Select(index => new SweepItem(index, effectiveStart, inSampleEnd)).ToList(), cancellationToken).ConfigureAwait(false);

        decimal?[] objectiveValues = inSample.Select(run => BacktestSweep.ValidValue(run.Report, objective)).ToArray();
        int[] ranked = OptimizationRanking.Rank(objectiveValues, minimize).Take(topK).ToArray();

        SweepRun[] outOfSample = outOfSampleStart is { } oosStart && ranked.Length > 0
            ? await _sweep.RunAsync(symbol, timeFrame, bound, ranked.Select(index => new SweepItem(index, oosStart, effectiveEnd)).ToList(), cancellationToken).ConfigureAwait(false)
            : Array.Empty<SweepRun>();

        int valid = objectiveValues.Count(value => value is not null);
        var warnings = new List<string>(symbolWarnings);
        warnings.AddRange(bound.Warnings);
        if (valid == 0)
        {
            warnings.Add($"No combination has a Valid {objective.Name}, so the ranking is empty; try another objective or other ranges.");
        }

        return ToolJson.Build(writer => Write(
            writer, symbol, timeFrame, objective, minimize, grid, inSample, objectiveValues, ranked, outOfSample, effectiveStart, effectiveEnd, inSampleEnd,
            outOfSampleStart, requestedStart is null || requestedEnd is null, valid, warnings));
    }

    // ---- reading and checking the request ----------------------------------------------------------------------------------------------

    private void CheckOutOfSampleAgainstRequested(DateOnly? outOfSampleStart, DateOnly? requestedStart, DateOnly? requestedEnd)
    {
        if (outOfSampleStart is not { } split)
        {
            return;
        }

        if (requestedStart is { } start && split <= start)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{OutOfSampleArgument} must be after {StartArgument}.", OutOfSampleArgument);
        }

        if (requestedEnd is { } end && split > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{OutOfSampleArgument} must not be after {EndArgument}.", OutOfSampleArgument);
        }
    }

    // ---- output ------------------------------------------------------------------------------------------------------------------------

    private void Write(
        Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        ObjectiveMetric objective,
        bool minimize,
        BacktestParameterGrid grid,
        SweepRun[] inSample,
        decimal?[] objectiveValues,
        int[] ranked,
        SweepRun[] outOfSample,
        DateOnly start,
        DateOnly end,
        DateOnly inSampleEnd,
        DateOnly? outOfSampleStart,
        bool datesDefaulted,
        int valid,
        IReadOnlyList<string> warnings)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        writer.WriteString("objective", objective.Name);
        writer.WriteString("objectiveDirection", minimize ? Minimize : Maximize);

        writer.WriteStartObject("period");
        writer.WriteString("startDate", FormatDate(start));
        writer.WriteString("endDate", FormatDate(end));
        writer.WriteBoolean("datesFromStoredHistory", datesDefaulted);
        if (outOfSampleStart is { } split)
        {
            writer.WriteString("inSampleEndDate", FormatDate(inSampleEnd));
            writer.WriteString("outOfSampleStartDate", FormatDate(split));
        }

        writer.WriteEndObject();

        writer.WriteNumber("combinationCount", grid.CombinationCount);
        writer.WriteNumber("validCombinationCount", valid);
        writer.WriteNumber("invalidCombinationCount", grid.CombinationCount - valid);

        writer.WriteStartArray("ranking");
        for (int position = 0; position < ranked.Length; position++)
        {
            int index = ranked[position];
            BacktestReport report = inSample[index].Report!;
            writer.WriteStartObject();
            writer.WriteNumber("rank", position + 1);
            writer.WriteNumber("combinationIndex", index);
            writer.WriteStartObject("parameters");
            foreach (KeyValuePair<string, decimal> pair in grid.ValuesAt(index))
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteNumber("objectiveValue", objectiveValues[index]!.Value);
            WriteMetrics(writer, report);
            writer.WriteNumber("totalTrades", report.TotalTrades);
            WriteSensitivity(writer, grid, objectiveValues, index);
            if (outOfSampleStart is not null)
            {
                WriteOutOfSample(writer, objective, outOfSample[position].Report);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        BacktestSweep.WriteWarnings(writer, warnings);
        writer.WriteEndObject();
    }

    private static string FormatDate(DateOnly date) => date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture);

    private static void WriteMetrics(Utf8JsonWriter writer, BacktestReport report)
    {
        writer.WriteStartObject("metrics");
        foreach (string name in SummaryMetrics)
        {
            BacktestSweep.WriteMetric(writer, BacktestReportJson.JsonName(name), BacktestSweep.Metric(report, name));
        }

        writer.WriteEndObject();
    }

    private static void WriteSensitivity(Utf8JsonWriter writer, BacktestParameterGrid grid, decimal?[] objectiveValues, int index)
    {
        decimal[] neighbors = grid.Neighbors(index).Select(neighbor => objectiveValues[neighbor]).Where(value => value is not null).Select(value => value!.Value).ToArray();
        if (neighbors.Length == 0)
        {
            writer.WriteNull("sensitivity");
            return;
        }

        writer.WriteStartObject("sensitivity");
        writer.WriteNumber("neighborMean", OptimizationRanking.Mean(neighbors)!.Value);
        writer.WriteNumber("neighborMin", neighbors.Min());
        writer.WriteNumber("neighborCount", neighbors.Length);
        writer.WriteEndObject();
    }

    private static void WriteOutOfSample(Utf8JsonWriter writer, ObjectiveMetric objective, BacktestReport? report)
    {
        writer.WriteStartObject("outOfSample");
        MetricValue? metric = report is null ? null : objective.Read(report);
        decimal? value = metric is { Status: MetricStatus.Valid } valid ? valid.Value : null;
        ToolJson.WriteNullableNumber(writer, "objectiveValue", value);
        writer.WriteString("status", metric?.Status.ToString() ?? StatusNoReport);
        writer.WriteEndObject();
    }
}
