using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Optimization;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_run_walk_forward_analysis: for each window the grid of sa_optimize_backtest is run on the in-sample part, the best combination is run on the
/// out-of-sample part, and the per-window and aggregate results are returned. Windows come from a generator (lengths in bars of the time frame) or from an
/// explicit list of dates. Nothing is defined here that the backtest report does not already define, except the ratio of two means of its BarSharpe.
/// </summary>
internal sealed class RunWalkForwardTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    private const string ConditionsArgument = BacktestSweep.ConditionsArgument;
    private const string ObjectiveArgument = BacktestSweep.ObjectiveArgument;
    private const string DirectionArgument = BacktestSweep.DirectionArgument;
    private const string WindowsArgument = "windows";
    private const string GeneratorKey = "generator";
    private const string ListKey = "list";
    private const string MinTradesArgument = "minInSampleTrades";

    private const string Maximize = BacktestSweep.Maximize;
    private const string Minimize = BacktestSweep.Minimize;

    /// <summary>No upper bound beyond what an int holds for minInSampleTrades (the schema only has a minimum; a count above any trade count simply makes every combination ineligible).</summary>
    private const int NoUpperBound = int.MaxValue;

    private const string ModeList = "List";
    private const string StatusNoBars = nameof(WalkForwardWindowStatus.NoInSampleBars);
    private const string StatusNoValidObjective = nameof(WalkForwardWindowStatus.NoValidInSampleObjective);
    private const string StatusNoTrades = nameof(WalkForwardWindowStatus.NoInSampleTrades);
    private const string StatusNoOutOfSampleBars = nameof(WalkForwardWindowStatus.NoOutOfSampleBars);

    private readonly BacktestSweep _sweep;
    private readonly McpLimits _limits;

    public RunWalkForwardTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, BacktestConditionBinder conditions, McpLimits limits)
    {
        _sweep = new BacktestSweep(gateway, symbols, conditions, limits);
        _limits = limits;
    }

    public string Name => "sa_run_walk_forward_analysis";

    public string Description =>
        "Walk-forward analysis of a backtest: for every window it runs the grid of sa_optimize_backtest (conditions with {\"$param\": name} placeholders, parameterRanges, objective, objectiveDirection, account, riskManagement, report: exactly the same meaning and checks) on the in-sample part, takes the best combination (best objective, ties by combination number) and runs only that one on the out-of-sample part. " +
        "It uses the application's own backtest engine; no formula is defined here. " +
        "windows is exactly one of generator and list. generator {mode, inSampleBars, outOfSampleBars, stepBars} cuts the evaluated period (startDate to endDate, default the stored history) into windows measured in bars of the time frame, never in calendar time: " +
        "Rolling keeps the in-sample length and moves the window by stepBars, Anchored keeps the first bar and lets the in-sample part grow by stepBars; the out-of-sample part is the next outOfSampleBars bars, and a window whose out-of-sample part would run past the last bar is not made. " +
        "list [{inSampleStart, inSampleEnd, outOfSampleStart, outOfSampleEnd}] (yyyy-MM-dd, inclusive) gives the windows yourself; inSampleEnd must be before outOfSampleStart, and startDate and endDate must then be omitted. " +
        $"At most {_limits.MaxWalkForwardWindows} windows (more is LIMIT_EXCEEDED, never cut) and, per window, the limits of sa_optimize_backtest ({_limits.MaxOptimizationCombinations} combinations, {_limits.MaxOptimizationParameters} ranges). A generator that gives no window is INVALID_ARGUMENT. " +
        "Each window lists bestParameters, and for the in-sample (is...) and out-of-sample (oos...) part the objective, barSharpe, totalReturn and maxDrawdown as {status, value} (value null unless Valid) and the total trades. " +
        $"minInSampleTrades (default {_limits.DefaultMinInSampleTrades}) is the fewest trades a combination must make in the in-sample part to be eligible as the best one: a combination without trades is never chosen, because its objective (for example totalReturn 0) says nothing. " +
        $"A window has a status: Evaluated, or isInsufficientData with {StatusNoBars} (the in-sample part holds no stored bar), {StatusNoTrades} (no combination made minInSampleTrades trades there, for example near the start of the stored history, where an indicator has no earlier bars to start from) or {StatusNoValidObjective} (some combination traded enough but none has a Valid objective); such a window has no best combination, is not run out of sample and is left out of the aggregate. " +
        $"{StatusNoOutOfSampleBars} means the best combination exists but the out-of-sample part holds no stored bar: its oos values are null and the window is not used for the out-of-sample means. " +
        "Every run lets its indicators start on the stored bars before its first day, so a window is too short for its indicators only where the stored history itself begins. The tool cannot see how many bars an indicator needs before it is defined, so that case shows up only as few or no trades. " +
        "aggregate (null when no window was evaluated) has meanIsObjective and meanOosObjective (simple means over the evaluated windows whose value is Valid), meanIsBarSharpe, meanOosBarSharpe and barSharpeEfficiency = meanOosBarSharpe / meanIsBarSharpe, " +
        "taken over the windows in which both BarSharpe values are Valid (barSharpeWindowCount); it is null, with barSharpeEfficiencyReason, when no window qualifies or meanIsBarSharpe is not greater than 0. There is no threshold on the denominator. " +
        "No deflated Sharpe ratio, probability of backtest overfitting or shuffled confidence interval is computed. " +
        $"The cost grows with windows x combinations x in-sample bars; a call that runs out of time ({_limits.HeavyToolTimeoutSeconds} s) or is cancelled returns an error, never a partial result. The tool never changes anything and never fetches data.";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"conditions\":{\"entryLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":{\"$param\":\"period\"}}}}," +
        "\"exitLong\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"LessThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":{\"$param\":\"period\"}}}}}," +
        "\"parameterRanges\":[{\"name\":\"period\",\"start\":10,\"end\":60,\"step\":10}],\"objective\":\"annualizedSharpe\"," +
        "\"windows\":{\"generator\":{\"mode\":\"Rolling\",\"inSampleBars\":500,\"outOfSampleBars\":125,\"stepBars\":125}}}";

    public string InputSchemaJson => BacktestSweep.WithRangesAndObjective(ToolSchema.Object()
        .String(SymbolArgument, "The symbol to test (a ticker from sa_list_tickers).", required: true)
        .FreeObject(ConditionsArgument,"The conditions of sa_run_backtest with {\"$param\": name} placeholders where numbers go.", required: true))
        .NestedObject(
            WindowsArgument,
            $"Exactly one of {GeneratorKey} and {ListKey}.",
            ToolSchema.Object()
                .NestedObject(
                    GeneratorKey,
                    "Windows measured in bars of the time frame.",
                    ToolSchema.Object()
                        .String("mode", "Rolling (the in-sample length stays, the window moves) or Anchored (the first bar stays, the in-sample part grows).", required: true, allowedValues: Enum.GetNames<WalkForwardMode>())
                        .Integer("inSampleBars", "Bars of the in-sample part (Anchored: of the first window).", required: true, minimum: 1)
                        .Integer("outOfSampleBars", "Bars of the out-of-sample part.", required: true, minimum: 1)
                        .Integer("stepBars", "Bars the next window is moved (Rolling) or grown (Anchored).", required: true, minimum: 1))
                .ObjectArray(
                    ListKey,
                    "Windows given by date (inclusive).",
                    ToolSchema.Object()
                        .String("inSampleStart", "yyyy-MM-dd", required: true)
                        .String("inSampleEnd", "yyyy-MM-dd; before outOfSampleStart.", required: true)
                        .String("outOfSampleStart", "yyyy-MM-dd", required: true)
                        .String("outOfSampleEnd", "yyyy-MM-dd", required: true),
                    minItems: 1),
            required: true)
        .Integer(MinTradesArgument, $"The fewest trades a combination must make in the in-sample part to be eligible as the best one. Default {_limits.DefaultMinInSampleTrades}; 0 allows a combination that never traded.", defaultValue: _limits.DefaultMinInSampleTrades, minimum: 0)
        .String(StartArgument, "Generator only: first day of the period the windows are cut from, yyyy-MM-dd. Default: the start of the stored history.")
        .String(EndArgument, "Generator only: last day of that period, yyyy-MM-dd. Default: the latest stored bar.")
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default {ReadOnlyGatewayContract.BacktestTimeFrame}.", allowedValues: ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Select(t => t.ToString()).ToArray())
        .FreeObject(BacktestSettingsBinder.AccountArgument, "Capital, cost, sizing and margin settings, as for sa_run_backtest; numbers may be placeholders.")
        .FreeObject(BacktestSettingsBinder.RiskArgument, "{stopLossRatio, takeProfitRatio}, as for sa_run_backtest; numbers may be placeholders.")
        .FreeObject(BacktestSettingsBinder.ReportArgument, "Report settings, as for sa_run_backtest; numbers may be placeholders.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    /// <summary>One window as the run plans it: its mode label and the first and last day of both parts.</summary>
    private readonly record struct PlannedWindow(int Index, DateOnly InSampleStart, DateOnly InSampleEnd, DateOnly OutOfSampleStart, DateOnly OutOfSampleEnd);

    /// <summary>The result of one window; <see cref="Best"/> is the grid index of the best in-sample combination, or -1.</summary>
    private sealed record WindowResult(PlannedWindow Window, string Status, int Best, SweepRun? InSample, SweepRun? OutOfSample, decimal? InSampleObjective, decimal? OutOfSampleObjective);

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // Everything the client sent is checked before any data is read.
        TimeFrame timeFrame = RunBacktestTool.ResolveTimeFrame(arguments);
        DateOnly? requestedStart = ToolArgs.Date(arguments, StartArgument);
        DateOnly? requestedEnd = ToolArgs.Date(arguments, EndArgument);
        if (requestedStart is { } start && requestedEnd is { } end && start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        ObjectiveMetric objective = OptimizationObjectives.Resolve(ToolArgs.String(arguments, ObjectiveArgument)!, ObjectiveArgument);
        bool minimize = string.Equals(ToolArgs.String(arguments, DirectionArgument), Minimize, StringComparison.Ordinal);
        int minInSampleTrades = ToolArgs.NonNegativeWithin(arguments, MinTradesArgument, _limits.DefaultMinInSampleTrades, NoUpperBound);
        JsonElement windowsElement = arguments.GetProperty(WindowsArgument);
        bool hasGenerator = windowsElement.TryGetProperty(GeneratorKey, out JsonElement generator);
        bool hasList = windowsElement.TryGetProperty(ListKey, out JsonElement list);
        if (hasGenerator == hasList)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Give exactly one of {GeneratorKey} and {ListKey}.", WindowsArgument, "A generator cuts the period into windows by bar counts; a list gives the dates yourself.");
        }

        IReadOnlyList<PlannedWindow>? listed = null;
        if (hasList)
        {
            if (requestedStart is not null || requestedEnd is not null)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} and {EndArgument} apply to a generator only; the list gives its own dates.", requestedStart is not null ? StartArgument : EndArgument);
            }

            listed = ReadList(list);
        }

        WalkForwardMode mode = WalkForwardMode.Rolling;
        int inSampleBars = 0, outOfSampleBars = 0, stepBars = 0;
        if (hasGenerator)
        {
            mode = Enum.Parse<WalkForwardMode>(generator.GetProperty("mode").GetString()!);
            inSampleBars = ReadBars(generator, "inSampleBars");
            outOfSampleBars = ReadBars(generator, "outOfSampleBars");
            stepBars = ReadBars(generator, "stepBars");
        }

        BacktestParameterGrid grid = _sweep.ReadGrid(arguments.GetProperty(BacktestParameterGrid.RangesArgument));
        BoundCombinations bound = _sweep.Bind(arguments, grid, timeFrame);
        SweepSymbol resolved = await _sweep.ResolveSymbolAsync(arguments, SymbolArgument, timeFrame, cancellationToken).ConfigureAwait(false);

        DateOnly? firstDay = null, lastDay = null;
        int barCount = 0;
        IReadOnlyList<PlannedWindow> planned;
        if (listed is not null)
        {
            planned = listed;
        }
        else
        {
            firstDay = requestedStart ?? DateOnly.FromDateTime(resolved.StoredBars[0].Timestamp);
            lastDay = requestedEnd ?? DateOnly.FromDateTime(resolved.StoredBars[^1].Timestamp);
            if (firstDay > lastDay)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
            }

            planned = PlanGenerated(resolved.StoredBars, firstDay.Value, lastDay.Value, mode, inSampleBars, outOfSampleBars, stepBars, out barCount);
        }

        var inSampleItems = new List<SweepItem>(planned.Count * grid.CombinationCount);
        foreach (PlannedWindow window in planned)
        {
            for (int combination = 0; combination < grid.CombinationCount; combination++)
            {
                inSampleItems.Add(new SweepItem(combination, window.InSampleStart, window.InSampleEnd));
            }
        }

        SweepRun[] inSampleRuns = await _sweep.RunAsync(resolved.Symbol, timeFrame, bound, inSampleItems, cancellationToken).ConfigureAwait(false);

        var selections = new WalkForwardWindowSelection[planned.Count];
        for (int w = 0; w < planned.Count; w++)
        {
            InSampleOutcome[] outcomes = inSampleRuns
                .Skip(w * grid.CombinationCount)
                .Take(grid.CombinationCount)
                .Select(run => new InSampleOutcome(run.NoEvaluatedBars, run.Report?.TotalTrades, BacktestSweep.ValidValue(run.Report, objective)))
                .ToArray();
            selections[w] = WalkForwardWindowSelector.Select(outcomes, minimize, minInSampleTrades);
        }

        var outOfSampleItems = new List<SweepItem>();
        for (int w = 0; w < planned.Count; w++)
        {
            if (selections[w].BestIndex >= 0)
            {
                outOfSampleItems.Add(new SweepItem(selections[w].BestIndex, planned[w].OutOfSampleStart, planned[w].OutOfSampleEnd));
            }
        }

        SweepRun[] outOfSampleRuns = outOfSampleItems.Count > 0
            ? await _sweep.RunAsync(resolved.Symbol, timeFrame, bound, outOfSampleItems, cancellationToken).ConfigureAwait(false)
            : Array.Empty<SweepRun>();

        var results = new List<WindowResult>(planned.Count);
        int next = 0;
        for (int w = 0; w < planned.Count; w++)
        {
            WalkForwardWindowSelection selection = selections[w];
            if (selection.BestIndex < 0)
            {
                results.Add(new WindowResult(planned[w], selection.Status.ToString(), -1, null, null, null, null));
                continue;
            }

            SweepRun inSampleRun = inSampleRuns[(w * grid.CombinationCount) + selection.BestIndex];
            SweepRun? outOfSampleRun = outOfSampleRuns[next++] is { NoEvaluatedBars: false } run ? run : null;
            string status = (outOfSampleRun is null ? WalkForwardWindowStatus.NoOutOfSampleBars : WalkForwardWindowStatus.Evaluated).ToString();
            results.Add(new WindowResult(planned[w], status, selection.BestIndex, inSampleRun, outOfSampleRun, selection.BestObjective, BacktestSweep.ValidValue(outOfSampleRun?.Report, objective)));
        }

        var warnings = new List<string>(resolved.Warnings);
        warnings.AddRange(bound.Warnings);
        int evaluated = results.Count(result => result.Best >= 0);
        if (evaluated == 0)
        {
            warnings.Add("No window could be evaluated, so there is no aggregate; check the objective, the ranges and the dates.");
        }

        return ToolJson.Build(writer => Write(
            writer, resolved.Symbol, timeFrame, objective, minimize, hasList ? ModeList : mode.ToString(), grid, results, firstDay, lastDay, hasGenerator ? barCount : null, minInSampleTrades, warnings));
    }

    // ---- windows -----------------------------------------------------------------------------------------------------------------------

    private static int ReadBars(JsonElement generator, string key)
    {
        long value = generator.GetProperty(key).GetInt64();
        if (value < 1 || value > int.MaxValue)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{key} must be a whole number from 1 to {int.MaxValue}.", $"{WindowsArgument}.{GeneratorKey}.{key}");
        }

        return (int)value;
    }

    private IReadOnlyList<PlannedWindow> ReadList(JsonElement list)
    {
        CheckWindowCount(list.GetArrayLength(), $"{WindowsArgument}.{ListKey}");
        var windows = new List<PlannedWindow>();
        int index = 0;
        foreach (JsonElement item in list.EnumerateArray())
        {
            string path = $"{WindowsArgument}.{ListKey}[{index}]";
            DateOnly isStart = ReadDate(item, "inSampleStart", path);
            DateOnly isEnd = ReadDate(item, "inSampleEnd", path);
            DateOnly oosStart = ReadDate(item, "outOfSampleStart", path);
            DateOnly oosEnd = ReadDate(item, "outOfSampleEnd", path);
            if (isStart > isEnd)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "inSampleStart must not be after inSampleEnd.", $"{path}.inSampleStart");
            }

            if (isEnd >= oosStart)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "inSampleEnd must be before outOfSampleStart: the out-of-sample part has to come after the in-sample part.", $"{path}.outOfSampleStart");
            }

            if (oosStart > oosEnd)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "outOfSampleStart must not be after outOfSampleEnd.", $"{path}.outOfSampleStart");
            }

            windows.Add(new PlannedWindow(index, isStart, isEnd, oosStart, oosEnd));
            index++;
        }

        return windows;
    }

    private static DateOnly ReadDate(JsonElement owner, string key, string path)
        => DateOnly.TryParseExact(owner.GetProperty(key).GetString(), ToolJson.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{key}' must be a date in the form {ToolJson.DateFormat}.", $"{path}.{key}");

    private void CheckWindowCount(int count, string path)
    {
        if (count > _limits.MaxWalkForwardWindows)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The request gives {count} windows; the limit is {_limits.MaxWalkForwardWindows}.",
                path,
                "Use longer windows or a longer step. The windows are never cut.");
        }
    }

    /// <summary>Cuts the stored bars of the period into windows with the Core planner and names each part by the dates of its first and last bar.</summary>
    private IReadOnlyList<PlannedWindow> PlanGenerated(
        IReadOnlyList<CandleData> stored, DateOnly first, DateOnly last, WalkForwardMode mode, int inSampleBars, int outOfSampleBars, int stepBars, out int barCount)
    {
        DateOnly[] days = stored.Select(bar => DateOnly.FromDateTime(bar.Timestamp)).Where(day => day >= first && day <= last).ToArray();
        barCount = days.Length;
        IReadOnlyList<WalkForwardWindow> windows = WalkForwardWindowPlanner.Plan(days.Length, mode, inSampleBars, outOfSampleBars, stepBars);
        if (windows.Count == 0)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"The period {first:yyyy-MM-dd} to {last:yyyy-MM-dd} has {days.Length} stored bars; one window needs {(long)inSampleBars + outOfSampleBars}.",
                $"{WindowsArgument}.{GeneratorKey}",
                "Use shorter windows or a longer period.");
        }

        CheckWindowCount(windows.Count, $"{WindowsArgument}.{GeneratorKey}");
        return windows
            .Select(window => new PlannedWindow(window.Index, days[window.InSampleStart], days[window.InSampleEnd], days[window.OutOfSampleStart], days[window.OutOfSampleEnd]))
            .ToArray();
    }

    // ---- output ------------------------------------------------------------------------------------------------------------------------

    private static string Date(DateOnly day) => day.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture);

    private void Write(
        Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        ObjectiveMetric objective,
        bool minimize,
        string mode,
        BacktestParameterGrid grid,
        IReadOnlyList<WindowResult> results,
        DateOnly? firstDay,
        DateOnly? lastDay,
        int? barCount,
        int minInSampleTrades,
        IReadOnlyList<string> warnings)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        writer.WriteString("objective", objective.Name);
        writer.WriteString("objectiveDirection", minimize ? Minimize : Maximize);
        writer.WriteString("windowMode", mode);
        writer.WriteNumber("minInSampleTrades", minInSampleTrades);
        if (firstDay is { } first && lastDay is { } last)
        {
            writer.WriteStartObject("period");
            writer.WriteString("startDate", Date(first));
            writer.WriteString("endDate", Date(last));
            writer.WriteNumber("barCount", barCount ?? 0);
            writer.WriteEndObject();
        }

        writer.WriteNumber("combinationCount", grid.CombinationCount);
        writer.WriteNumber("windowCount", results.Count);
        writer.WriteNumber("evaluatedWindowCount", results.Count(result => result.Best >= 0));

        writer.WriteStartArray("windows");
        foreach (WindowResult result in results)
        {
            WriteWindow(writer, objective, grid, result);
        }

        writer.WriteEndArray();
        WriteAggregate(writer, results);

        BacktestSweep.WriteWarnings(writer, warnings);
        writer.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter writer, ObjectiveMetric objective, BacktestParameterGrid grid, WindowResult result)
    {
        PlannedWindow window = result.Window;
        writer.WriteStartObject();
        writer.WriteNumber("index", window.Index);
        writer.WriteStartObject("inSample");
        writer.WriteString("start", Date(window.InSampleStart));
        writer.WriteString("end", Date(window.InSampleEnd));
        writer.WriteEndObject();
        writer.WriteStartObject("outOfSample");
        writer.WriteString("start", Date(window.OutOfSampleStart));
        writer.WriteString("end", Date(window.OutOfSampleEnd));
        writer.WriteEndObject();
        writer.WriteString("status", result.Status);
        writer.WriteBoolean("isInsufficientData", result.Best < 0);

        if (result.Best >= 0)
        {
            writer.WriteStartObject("bestParameters");
            foreach (KeyValuePair<string, decimal> pair in grid.ValuesAt(result.Best))
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteNumber("bestCombinationIndex", result.Best);
        }
        else
        {
            writer.WriteNull("bestParameters");
        }

        WritePart(writer, "is", objective, result.InSample?.Report);
        WritePart(writer, "oos", objective, result.OutOfSample?.Report);
        writer.WriteEndObject();
    }

    /// <summary>The objective, BarSharpe, TotalReturn, MaxDrawdown and trade count of one part of a window, each null when the part has no report.</summary>
    private static void WritePart(Utf8JsonWriter writer, string prefix, ObjectiveMetric objective, BacktestReport? report)
    {
        BacktestSweep.WriteMetric(writer, prefix + "Objective", report is null ? null : objective.Read(report));
        BacktestSweep.WriteMetric(writer, prefix + "BarSharpe", BacktestSweep.Metric(report, nameof(BacktestReport.BarSharpe)));
        BacktestSweep.WriteMetric(writer, prefix + "TotalReturn", BacktestSweep.Metric(report, nameof(BacktestReport.TotalReturn)));
        BacktestSweep.WriteMetric(writer, prefix + "MaxDrawdown", BacktestSweep.Metric(report, nameof(BacktestReport.MaxDrawdown)));
        ToolJson.WriteNullableNumber(writer, prefix + "TotalTrades", (long?)report?.TotalTrades);
    }

    private static void WriteAggregate(Utf8JsonWriter writer, IReadOnlyList<WindowResult> results)
    {
        WindowResult[] evaluated = results.Where(result => result.Best >= 0).ToArray();
        if (evaluated.Length == 0)
        {
            writer.WriteNull("aggregate");
            return;
        }

        decimal[] isObjectives = evaluated.Where(r => r.InSampleObjective is not null).Select(r => r.InSampleObjective!.Value).ToArray();
        decimal[] oosObjectives = evaluated.Where(r => r.OutOfSampleObjective is not null).Select(r => r.OutOfSampleObjective!.Value).ToArray();
        WalkForwardEfficiencyResult efficiency = WalkForwardEfficiency.Compute(evaluated.Select(r => (
            BacktestSweep.ValidValue(BacktestSweep.Metric(r.InSample?.Report, nameof(BacktestReport.BarSharpe))),
            BacktestSweep.ValidValue(BacktestSweep.Metric(r.OutOfSample?.Report, nameof(BacktestReport.BarSharpe))))));

        writer.WriteStartObject("aggregate");
        writer.WriteNumber("evaluatedWindowCount", evaluated.Length);
        ToolJson.WriteNullableNumber(writer, "meanIsObjective", OptimizationRanking.Mean(isObjectives));
        ToolJson.WriteNullableNumber(writer, "meanOosObjective", OptimizationRanking.Mean(oosObjectives));
        ToolJson.WriteNullableNumber(writer, "meanIsBarSharpe", efficiency.MeanInSampleBarSharpe);
        ToolJson.WriteNullableNumber(writer, "meanOosBarSharpe", efficiency.MeanOutOfSampleBarSharpe);
        writer.WriteNumber("barSharpeWindowCount", efficiency.WindowCount);
        ToolJson.WriteNullableNumber(writer, "barSharpeEfficiency", efficiency.Value);
        if (efficiency.Reason is { } reason)
        {
            writer.WriteString("barSharpeEfficiencyReason", reason);
        }
        else
        {
            writer.WriteNull("barSharpeEfficiencyReason");
        }

        writer.WriteEndObject();
    }
}
