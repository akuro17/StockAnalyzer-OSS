using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What a swept run keeps: its report (null when the run produced none) and whether it evaluated no stored bar at all (no bar of the period, which the engine does not treat as an error).</summary>
internal sealed record SweepRun(BacktestReport? Report, bool NoEvaluatedBars);

/// <summary>One run of a sweep: the combination of the grid and the first and last evaluated day.</summary>
internal readonly record struct SweepItem(int Combination, DateOnly Start, DateOnly End);

/// <summary>The bound conditions and settings of every combination, and the advisory notes of the (structurally identical) conditions.</summary>
internal sealed record BoundCombinations(BoundBacktestConditions[] Conditions, BoundBacktestSettings[] Settings, IReadOnlyList<string> Warnings);

/// <summary>The symbol of a sweep in canonical form, the notes about it, and its stored bars (read once, for default dates and window planning).</summary>
internal sealed record SweepSymbol(string Symbol, IReadOnlyList<string> Warnings, IReadOnlyList<CandleData> StoredBars);

/// <summary>
/// The parts sa_optimize_backtest and sa_run_walk_forward_analysis share: reading parameterRanges, checking the placeholders, binding EVERY combination
/// with the binders of sa_run_backtest before anything runs, resolving the symbol, running many snapshots in one gateway batch and ranking the results.
/// </summary>
internal sealed class BacktestSweep
{
    public const string ConditionsArgument = RunBacktestTool.ConditionsArgument;
    public const string ObjectiveArgument = "objective";
    public const string DirectionArgument = "objectiveDirection";

    public const string Maximize = "Maximize";
    public const string Minimize = "Minimize";

    /// <summary>The settings sections a placeholder may stand in (the conditions are the first one).</summary>
    private static readonly string[] SettingsSections = { BacktestSettingsBinder.AccountArgument, BacktestSettingsBinder.RiskArgument, BacktestSettingsBinder.ReportArgument };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly BacktestConditionBinder _conditions;
    private readonly McpLimits _limits;

    public BacktestSweep(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, BacktestConditionBinder conditions, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _conditions = conditions;
        _limits = limits;
    }

    // ---- schema and output parts both tools share --------------------------------------------------------------------------------------

    /// <summary>Adds parameterRanges, objective and objectiveDirection (in this order) to <paramref name="schema"/>.</summary>
    public static ToolSchema WithRangesAndObjective(ToolSchema schema)
        => schema
            .ObjectArray(
                BacktestParameterGrid.RangesArgument,
                "The values of every placeholder: {name, start, end, step}. start, start + step, ... up to end.",
                ToolSchema.Object()
                    .String("name", "The placeholder name used in $param.", required: true)
                    .Number("start", "First value.", required: true)
                    .Number("end", "Largest value allowed (inclusive).", required: true)
                    .Number("step", "Distance between values; greater than 0.", required: true),
                required: true,
                minItems: 1)
            .String(ObjectiveArgument, "The report metric to rank by (a name from sa_get_capabilities backtestOptimization.objectives, for example annualizedSharpe).", required: true)
            .String(DirectionArgument, $"{Maximize} (default: the highest value is best) or {Minimize}.", allowedValues: new[] { Maximize, Minimize });

    /// <summary>Writes the "warnings" array when there is at least one warning.</summary>
    public static void WriteWarnings(System.Text.Json.Utf8JsonWriter writer, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("warnings");
        foreach (string warning in warnings)
        {
            writer.WriteStringValue(warning);
        }

        writer.WriteEndArray();
    }

    // ---- reading and binding the request ----------------------------------------------------------------------------------------------

    public BacktestParameterGrid ReadGrid(JsonElement ranges)
    {
        if (ranges.GetArrayLength() > _limits.MaxOptimizationParameters)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"{BacktestParameterGrid.RangesArgument} has {ranges.GetArrayLength()} entries; the limit is {_limits.MaxOptimizationParameters}.",
                BacktestParameterGrid.RangesArgument,
                "Vary fewer parameters in one call.");
        }

        var parsed = new List<ParameterRange>();
        int index = 0;
        foreach (JsonElement item in ranges.EnumerateArray())
        {
            string path = $"{BacktestParameterGrid.RangesArgument}[{index}]";
            parsed.Add(new ParameterRange(
                item.GetProperty("name").GetString()!,
                ReadDecimal(item, "start", path),
                ReadDecimal(item, "end", path),
                ReadDecimal(item, "step", path)));
            index++;
        }

        return BacktestParameterGrid.Create(parsed, _limits.MaxOptimizationCombinations);
    }

    private static decimal ReadDecimal(JsonElement owner, string key, string path)
        => owner.GetProperty(key).TryGetDecimal(out decimal value)
            ? value
            : throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{key}' is outside the supported number range.", $"{path}.{key}");

    /// <summary>
    /// Checks that the placeholders of the conditions and settings and the names of <paramref name="grid"/> match one to one, then binds every combination
    /// with the binders of sa_run_backtest, so a value the engine's rules refuse (a Period of 0, a fractional integer, a ratio above 1) is one error that
    /// names the combination, raised before anything runs.
    /// </summary>
    public BoundCombinations Bind(JsonElement arguments, BacktestParameterGrid grid, TimeFrame timeFrame)
    {
        JsonNode conditions = JsonNode.Parse(arguments.GetProperty(ConditionsArgument).GetRawText())!;
        JsonObject settings = ReadSettingsSections(arguments);
        CheckPlaceholders(grid, conditions, settings);

        bool settingsVary = settings.Any(section => BacktestParameterGrid.FindPlaceholders(section.Value, section.Key).Count > 0);
        BoundBacktestSettings? fixedSettings = settingsVary ? null : BacktestSettingsBinder.Bind(ToElement(settings), timeFrame);
        var boundConditions = new BoundBacktestConditions[grid.CombinationCount];
        var boundSettings = new BoundBacktestSettings[grid.CombinationCount];
        for (int index = 0; index < grid.CombinationCount; index++)
        {
            IReadOnlyList<KeyValuePair<string, decimal>> values = grid.ValuesAt(index);
            var byName = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            try
            {
                boundConditions[index] = _conditions.Bind(ToElement(BacktestParameterGrid.Substitute(conditions, byName)!), ConditionsArgument, timeFrame);
                boundSettings[index] = fixedSettings
                    ?? BacktestSettingsBinder.Bind(ToElement(BacktestParameterGrid.Substitute(settings, byName)!), timeFrame);
            }
            catch (ToolException error)
            {
                throw new ToolException(error.Code, $"With {Describe(values)}: {error.Message}", error.Parameter, error.Hint);
            }
        }

        return new BoundCombinations(boundConditions, boundSettings, boundConditions[0].Warnings);
    }

    private static JsonObject ReadSettingsSections(JsonElement arguments)
    {
        var sections = new JsonObject();
        foreach (string section in SettingsSections)
        {
            if (arguments.TryGetProperty(section, out JsonElement value))
            {
                sections.Add(section, JsonNode.Parse(value.GetRawText()));
            }
        }

        return sections;
    }

    private static void CheckPlaceholders(BacktestParameterGrid grid, JsonNode conditions, JsonObject settings)
    {
        var found = new List<(string Name, string Path)>();
        found.AddRange(BacktestParameterGrid.FindPlaceholders(conditions, ConditionsArgument));
        foreach (KeyValuePair<string, JsonNode?> section in settings)
        {
            found.AddRange(BacktestParameterGrid.FindPlaceholders(section.Value, section.Key));
        }

        var known = new HashSet<string>(grid.Ranges.Select(range => range.Name), StringComparer.Ordinal);
        foreach ((string name, string path) in found)
        {
            if (!known.Contains(name))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"The placeholder '{name}' has no entry in {BacktestParameterGrid.RangesArgument}.",
                    path,
                    $"Names in {BacktestParameterGrid.RangesArgument}: {string.Join(", ", known)}.");
            }
        }

        var used = new HashSet<string>(found.Select(item => item.Name), StringComparer.Ordinal);
        for (int i = 0; i < grid.Ranges.Count; i++)
        {
            if (!used.Contains(grid.Ranges[i].Name))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"The parameter '{grid.Ranges[i].Name}' is not used by any placeholder.",
                    $"{BacktestParameterGrid.RangesArgument}[{i}].name",
                    "Put {\"$param\": \"" + grid.Ranges[i].Name + "\"} where its number goes, or remove the range.");
            }
        }
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static string Describe(IEnumerable<KeyValuePair<string, decimal>> values)
        => string.Join(", ", values.Select(pair => $"{pair.Key} = {pair.Value.ToString(CultureInfo.InvariantCulture)}"));

    // ---- symbol and data ----------------------------------------------------------------------------------------------------------------

    /// <summary>Resolves the symbol argument (registered, or stored with allowUnregistered) and reads its stored bars; a symbol without stored bars is an error.</summary>
    public async Task<SweepSymbol> ResolveSymbolAsync(JsonElement arguments, string symbolArgument, TimeFrame timeFrame, CancellationToken cancellationToken)
    {
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, symbolArgument)!, symbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> warnings = resolved.IsRegistered ? Array.Empty<string>() : new[] { SymbolResolver.OffListWarning(new[] { symbol }) };
        IReadOnlyList<CandleData> stored = await _gateway.GetCandlesAsync(symbol, timeFrame, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", symbolArgument, "Call sa_list_tickers to see the symbols that have data.");
        }

        return new SweepSymbol(symbol, warnings, stored);
    }

    // ---- running and ranking ------------------------------------------------------------------------------------------------------------

    /// <summary>Runs the items in one gateway batch (shared bar loads, <see cref="McpLimits.OptimizationDegreeOfParallelism"/> at a time) and keeps only each run's report; results are in item order.</summary>
    public async Task<SweepRun[]> RunAsync(string symbol, TimeFrame timeFrame, BoundCombinations bound, IReadOnlyList<SweepItem> items, CancellationToken cancellationToken)
    {
        var snapshots = new List<BacktestRunSnapshot>(items.Count);
        foreach (SweepItem item in items)
        {
            BoundBacktestSettings settings = bound.Settings[item.Combination];
            snapshots.Add(new BacktestRunSnapshot(
                symbol,
                timeFrame,
                RunBacktestTool.ToUtc(item.Start),
                RunBacktestTool.ToUtc(item.End),
                settings.Configuration,
                ImmutableArray<BacktestConditionEntry>.Empty,
                settings.RiskManagement,
                ImmutableArray<StrategyIndicatorRequest>.Empty,
                settings.ReportDefaults,
                settings.BootstrapSeed,
                settings.BootstrapIterations,
                bound.Conditions[item.Combination].Tree));
        }

        try
        {
            IReadOnlyList<SweepRun> runs = await _gateway
                .RunBacktestBatchAsync(snapshots, _limits.OptimizationDegreeOfParallelism, artifact => new SweepRun(artifact.Report, artifact.Result.IsInsufficientData || artifact.Metadata.SampleCount == 0), cancellationToken)
                .ConfigureAwait(false);
            return runs.ToArray();
        }
        catch (ArgumentException error)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"The backtest input was rejected: {error.Message.Split(Environment.NewLine)[0]}", hint: "Check the dates and the conditions.");
        }
    }

    /// <summary>The report metric named <paramref name="propertyName"/> (a <see cref="BacktestReport"/> property), or null when there is no report.</summary>
    public static MetricValue? Metric(BacktestReport? report, string propertyName)
        => report is null ? null : OptimizationObjectives.ReadMetric(report, propertyName);

    /// <summary>Writes <paramref name="metric"/> as {status, value} under <paramref name="name"/> (value null unless Valid), or a JSON null when there is no metric.</summary>
    public static void WriteMetric(System.Text.Json.Utf8JsonWriter writer, string name, MetricValue? metric)
    {
        if (metric is not { } present)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("status", present.Status.ToString());
        ToolJson.WriteNullableNumber(writer, "value", present.Value);
        writer.WriteEndObject();
    }

    /// <summary>The value of a metric when it is Valid, otherwise null.</summary>
    public static decimal? ValidValue(MetricValue? metric) => metric is { Status: MetricStatus.Valid } valid ? valid.Value : null;

    /// <summary>The objective of a run when it is Valid, otherwise null.</summary>
    public static decimal? ValidValue(BacktestReport? report, ObjectiveMetric objective)
        => report is not null && objective.Read(report) is { Status: MetricStatus.Valid } metric ? metric.Value : null;
}
