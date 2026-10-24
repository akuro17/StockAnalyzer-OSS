using System.Text.Json;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_capabilities: the indicator catalogue (types, parameters, output series, flags) and the supported time frames.</summary>
internal sealed class GetCapabilitiesTool : IMcpTool
{
    private const string CategoryArgument = "category";
    private const string FilterArgument = "indicatorTypeFilter";
    private const string DetailsArgument = "includeDetails";
    private const string ScreenerCatalogArgument = "includeScreenerCatalog";

    /// <summary>The unit of every period parameter and result length the tools use, whatever the time frame.</summary>
    public const string PeriodUnit = "bars";

    private readonly IndicatorCatalog _catalog;
    private readonly ScreenerCatalog _screenerCatalog;
    private readonly SandboxedExecutionSettings? _execution;

    /// <param name="execution">The settings of the execution tools when <c>--enable-execution</c> is given; null when they are not offered.</param>
    public GetCapabilitiesTool(IndicatorCatalog catalog, ScreenerCatalog screenerCatalog, SandboxedExecutionSettings? execution = null)
    {
        _catalog = catalog;
        _screenerCatalog = screenerCatalog;
        _execution = execution;
    }

    public string Name => "sa_get_capabilities";

    public string Description =>
        "Describes what this server can calculate: every indicator type with its display name, category and backtest eligibility, and the supported time frames. " +
        "Call it before sa_calculate_indicators to learn exact type names, parameter names with their default and allowed range, and output series names. " +
        "Without arguments the list is compact (type, displayName, category, backtestEligibility, isRegistered); " +
        "with indicatorTypeFilter (or includeDetails true) each indicator also lists parameters, outputSeriesNames, isCrossTicker and nonCausalSeries. " +
        "A parameter has min and max from the indicator's hard range (CoreParameterRange) and, where the chart input is narrower, guiMin and guiMax. " +
        "isCrossTicker marks an indicator that compares two symbols: it needs its ComparisonSymbol parameter and is accepted by sa_calculate_indicators and sa_run_screener, not by sa_run_backtest. " +
        "backtestEligibility is Allowed, NonCausal (the value at a bar depends on later bars) or SynchronousCalculationUnsupported; nonCausalSeries lists the series that look ahead. " +
        "Unregistered types are defined in the product but cannot be calculated (isRegistered false). " +
        "unit is null because no indicator output unit is stored in the application; unitUndefined lists those types. " +
        "includeScreenerCatalog adds the names sa_run_screener accepts besides indicators: columns (isText true for the text columns; unavailableColumns lists table columns no condition can read reliably), criteria (patterns) and price sources, with the comparison and group operator names. " +
        "execution says whether the training tools are offered (enabled) and, when they are, lists what sa_start_training accepts: frameworks, architectures, hyperparameters, the fixed values and the limits, and what sa_start_rl_training does (reinforcementLearning). " +
        $"Period parameters and lengths are always in {PeriodUnit} of the requested time frame.";

    public bool IsHeavy => false;

    public string InputSchemaJson => ToolSchema.Object()
        .String(CategoryArgument, "Only indicators of this category.", allowedValues: Enum.GetNames<CoreIndicatorCategory>())
        .StringArray(FilterArgument, "Only these indicator types (case-insensitive names, for example RSI). Turns the details on unless includeDetails is false.", minItems: 1)
        .Boolean(DetailsArgument, "Add parameters, output series and flags to each indicator. Default: true when indicatorTypeFilter is given, otherwise false.")
        .Boolean(ScreenerCatalogArgument, "Add the screener catalogue (columns, criteria, price sources, operators). Default false.")
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? category = ToolArgs.String(arguments, CategoryArgument);
        IReadOnlyList<string>? filter = ToolArgs.StringArray(arguments, FilterArgument);
        bool details = ToolArgs.Boolean(arguments, DetailsArgument) ?? filter is not null;
        bool screener = ToolArgs.Boolean(arguments, ScreenerCatalogArgument) ?? false;

        IEnumerable<IndicatorCatalogEntry> selected = _catalog.Entries;
        if (filter is not null)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < filter.Count; index++)
            {
                wanted.Add(ResolveTypeName(filter[index], $"{FilterArgument}[{index}]"));
            }

            selected = selected.Where(entry => wanted.Contains(entry.Type));
        }

        if (category is not null)
        {
            selected = selected.Where(entry => string.Equals(entry.Category, category, StringComparison.Ordinal));
        }

        return Task.FromResult(BuildJson(selected.ToList(), details, screener, includeExecution: true));
    }

    /// <summary>The whole indicator catalogue with details (the content of the indicator catalogue resource).</summary>
    public string IndicatorCatalogJson() => BuildJson(_catalog.Entries.ToList(), details: true, screener: false);

    /// <summary>The screener catalogue alone (the content of the screener catalogue resource).</summary>
    public string ScreenerCatalogJson() => ToolJson.Build(writer =>
    {
        writer.WriteStartObject();
        WriteScreenerCatalogProperties(writer);
        writer.WriteEndObject();
    });

    private string BuildJson(List<IndicatorCatalogEntry> entries, bool details, bool screener, bool includeExecution = false)
    {
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("indicatorCount", entries.Count);
            writer.WriteString("periodUnit", PeriodUnit);
            writer.WriteStartArray("supportedTimeFrames");
            foreach (TimeFrame timeFrame in ReadOnlyGatewayContract.SupportedTimeFrames)
            {
                writer.WriteStringValue(timeFrame.ToString());
            }

            writer.WriteEndArray();
            writer.WriteStartArray("indicators");
            foreach (IndicatorCatalogEntry entry in entries)
            {
                WriteEntry(writer, entry, details);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("unitUndefined");
            foreach (IndicatorCatalogEntry entry in entries.Where(e => e.IsRegistered && e.Unit is null))
            {
                writer.WriteStringValue(entry.Type);
            }

            writer.WriteEndArray();
            if (screener)
            {
                writer.WriteStartObject("screener");
                WriteScreenerCatalogProperties(writer);
                writer.WriteEndObject();
            }

            if (includeExecution)
            {
                WriteExecution(writer);
            }

            writer.WriteEndObject();
        });
    }

    // The block lists what the execution tools accept, drawn from the application's own tables, so a new hyperparameter or architecture appears here by itself.
    private void WriteExecution(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("execution");
        writer.WriteBoolean("enabled", _execution is not null);
        if (_execution is { } settings)
        {
            TrainingFramework[] frameworks = Enum.GetValues<TrainingFramework>();
            WriteStrings(writer, "frameworks", frameworks.Select(f => f.ToString()));
            writer.WriteStartObject("architectures");
            foreach (TrainingFramework framework in frameworks)
            {
                WriteStrings(writer, framework.ToString(), TrainingArchitectureCatalog.For(framework));
            }

            writer.WriteEndObject();
            writer.WriteStartObject("defaultArchitectures");
            foreach (TrainingFramework framework in frameworks)
            {
                writer.WriteString(framework.ToString(), TrainingArchitectureCatalog.DefaultFor(framework));
            }

            writer.WriteEndObject();
            writer.WriteStartArray("hyperparameters");
            foreach (TrainingHyperparameterDefinition definition in TrainingHyperparameterCatalog.All)
            {
                WriteHyperparameter(writer, definition);
            }

            writer.WriteEndArray();
            WriteFixedValues(writer, EphemeralFixedValues.Current);
            WriteReinforcementLearning(writer);
            WriteExecutionLimits(writer, settings);
        }

        writer.WriteEndObject();
    }

    private static void WriteHyperparameter(Utf8JsonWriter writer, TrainingHyperparameterDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("key", definition.Key);
        writer.WriteString("default", definition.DefaultValue);
        WriteStrings(writer, "frameworks", definition.Frameworks.Select(f => f.ToString()));
        if (definition.Architecture is not null)
        {
            writer.WriteString("architecture", definition.Architecture);
        }

        if (definition.Domain is { } domain)
        {
            writer.WriteString("kind", domain.Kind.ToString());
            if (domain.Minimum is { } minimum)
            {
                writer.WriteNumber("minimum", minimum);
                writer.WriteBoolean("minimumInclusive", domain.MinimumInclusive);
            }

            if (domain.Maximum is { } maximum)
            {
                writer.WriteNumber("maximum", maximum);
                writer.WriteBoolean("maximumInclusive", domain.MaximumInclusive);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteFixedValues(Utf8JsonWriter writer, EphemeralFixedValues fixedValues)
    {
        writer.WriteStartObject("fixed");
        writer.WriteString("timeframe", fixedValues.Timeframe.ToString());
        writer.WriteString("featureMode", fixedValues.FeatureMode.ToString());
        writer.WriteString("initializationMode", fixedValues.InitializationMode.ToString());
        writer.WriteString("ensembleMethod", fixedValues.EnsembleMethod.ToString());
        writer.WriteBoolean("fixedZScore", fixedValues.FixedZScore);
        writer.WriteBoolean("calibration", fixedValues.Calibration);
        writer.WriteBoolean("cpcv", fixedValues.Cpcv);
        writer.WriteEndObject();
    }

    // What sa_start_rl_training does, drawn from the RL contract: the numbers of the smoke run (a full run is not offered).
    private static void WriteReinforcementLearning(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("reinforcementLearning");
        writer.WriteString("tool", "sa_start_rl_training");
        writer.WriteBoolean("smokeOnly", true);
        writer.WriteNumber("trainingTransitions", RlContract.SmokeTrainingTransitions);
        writer.WriteNumber("candidateSeeds", 1);
        writer.WriteNumber("deadlineMinutes", RlContract.JobDeadlineMinutes);
        writer.WriteString("costs", "factory defaults of the Backtest window");
        writer.WriteString("environment", "the application's managed training Python environment (not the embedded Python)");
        writer.WriteEndObject();
    }

    private static void WriteExecutionLimits(Utf8JsonWriter writer, SandboxedExecutionSettings settings)
    {
        writer.WriteStartObject("limits");
        writer.WriteNumber("keepLatestRuns", settings.KeepLatestRuns);
        writer.WriteNumber("orphanSessionMaxAgeHours", settings.OrphanSessionMaxAgeHours);
        writer.WriteNumber("maxConcurrentJobs", settings.MaxConcurrentJobs);
        writer.WriteNumber("statusTailMessages", settings.StatusTailMessages);
        writer.WriteNumber("maxPredictionBars", settings.MaxPredictionBars);
        writer.WriteNumber("cancelGraceSeconds", ExecutionLimits.CancelGraceSeconds);
        writer.WriteNumber("maxTrackedJobs", ExecutionLimits.MaxTrackedJobs);
        writer.WriteNumber("maxStatusMessageChars", ExecutionLimits.MaxStatusMessageChars);
        writer.WriteNumber("maxStageChars", ExecutionLimits.MaxStageChars);
        writer.WriteEndObject();
    }

    private void WriteScreenerCatalogProperties(Utf8JsonWriter writer)
    {
        writer.WriteString("timeFrame", RunScreenerTool.ScreenerTimeFrame.ToString());
        WriteStrings(writer, "comparisons", ScreenerConditionBinder.ComparisonNames);
        WriteStrings(writer, "groupOperators", ScreenerConditionBinder.LogicalOperatorNames);
        WriteNames(writer, "columns", _screenerCatalog.Columns);
        WriteStrings(writer, "unavailableColumns", _screenerCatalog.UnavailableColumns);
        WriteNames(writer, "criteria", _screenerCatalog.Criteria);
        WriteNames(writer, "prices", _screenerCatalog.Prices);
    }

    private static void WriteNames(Utf8JsonWriter writer, string name, IReadOnlyList<ScreenerCatalogName> items)
    {
        writer.WriteStartArray(name);
        foreach (ScreenerCatalogName item in items)
        {
            writer.WriteStartObject();
            writer.WriteString("name", item.Name);
            writer.WriteString("group", item.Group);
            writer.WriteString("description", item.Description);
            if (item.IsText)
            {
                writer.WriteBoolean("isText", true);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private string ResolveTypeName(string name, string parameter)
    {
        return _catalog.Find(name)?.Type ?? throw new ToolException(
            ToolErrorCodes.UnknownIndicator,
            $"Unknown indicator type '{name}'.",
            parameter,
            "Call sa_get_capabilities without indicatorTypeFilter to list the type names.");
    }

    private static void WriteEntry(Utf8JsonWriter writer, IndicatorCatalogEntry entry, bool details)
    {
        writer.WriteStartObject();
        writer.WriteString("type", entry.Type);
        writer.WriteBoolean("isRegistered", entry.IsRegistered);
        writer.WriteString("displayName", entry.DisplayName);
        if (entry.Category is not null)
        {
            writer.WriteString("category", entry.Category);
        }

        if (entry.BacktestEligibility is not null)
        {
            writer.WriteString("backtestEligibility", entry.BacktestEligibility);
        }

        if (details && entry.IsRegistered)
        {
            writer.WriteBoolean("isCrossTicker", entry.IsCrossTicker);
            WriteStrings(writer, "outputSeriesNames", entry.OutputSeriesNames);
            WriteStrings(writer, "nonCausalSeries", entry.NonCausalSeries);
            writer.WriteNull("unit");
            writer.WriteStartArray("parameters");
            foreach (IndicatorParameterDescriptor parameter in entry.Parameters)
            {
                WriteParameter(writer, parameter);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteParameter(Utf8JsonWriter writer, IndicatorParameterDescriptor parameter)
    {
        writer.WriteStartObject();
        writer.WriteString("name", parameter.Name);
        writer.WriteString("displayName", parameter.DisplayName);
        if (parameter.Description is not null)
        {
            writer.WriteString("description", parameter.Description);
        }

        writer.WriteString("type", parameter.Type);
        WriteValue(writer, "default", parameter.Default);
        WriteBound(writer, "min", parameter.Min);
        WriteBound(writer, "max", parameter.Max);
        WriteBound(writer, "guiMin", parameter.GuiMin);
        WriteBound(writer, "guiMax", parameter.GuiMax);
        if (parameter.AllowedValues is not null)
        {
            WriteStrings(writer, "allowedValues", parameter.AllowedValues);
        }

        writer.WriteEndObject();
    }

    private static void WriteBound(Utf8JsonWriter writer, string name, decimal? bound)
    {
        if (bound is { } value)
        {
            writer.WriteNumber(name, value);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;
            case double number:
                writer.WriteNumber(name, number);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case long number:
                writer.WriteNumber(name, number);
                break;
            case short number:
                writer.WriteNumber(name, number);
                break;
            case byte number:
                writer.WriteNumber(name, number);
                break;
            default:
                throw new NotSupportedException($"Parameter default of type {value.GetType().Name} cannot be written.");
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
