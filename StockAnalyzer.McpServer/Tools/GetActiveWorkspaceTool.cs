using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_active_workspace: the chart set-up the user last saved (symbol, time frame, active indicators with their parameters) and the same indicators as a ready-to-send
/// sa_calculate_indicators request (owner decision U-10). Read-only: it reads the saved default workspace file and the registered indicator files as they are; edits the user has not
/// saved in the running application are not part of it.
/// </summary>
internal sealed class GetActiveWorkspaceTool : IMcpTool
{
    private readonly IReadOnlyWorkspaceSource _workspace;
    private readonly IReadOnlyRegisteredIndicatorSource _registered;
    private readonly IndicatorCatalog _catalog;
    private readonly McpLimits _limits;

    public GetActiveWorkspaceTool(IReadOnlyWorkspaceSource workspace, IReadOnlyRegisteredIndicatorSource registered, IndicatorCatalog catalog, McpLimits limits)
    {
        _workspace = workspace;
        _registered = registered;
        _catalog = catalog;
        _limits = limits;
    }

    /// <summary>The URI of the resource that serves the same document.</summary>
    internal static readonly string ResourceUri = $"{Resources.McpResourceRegistry.UriScheme}workspace/active";

    public string Name => "sa_get_active_workspace";

    public string Description =>
        "Returns the chart set-up the user last SAVED in StockAnalyzer: the symbol and time frame of the chart, the candle count, the chart type, and the active indicators with their parameters, " +
        "plus 'request', the same indicators as arguments of sa_calculate_indicators (symbol, timeFrame, indicators) ready to send. " +
        "An indicator that reads another indicator (sourceIndicatorId, dynamicPeriodIndicatorId) brings the registered source indicator or dynamic period driver it reads into the request. " +
        "Only the saved file is read: changes in the running application that are not saved yet are not included (savedAt says when the file was written). " +
        $"The saved time frames Daily, Weekly and Monthly map to D1, W1 and MN1; another saved time frame has no request (timeFrame null). More than {_limits.MaxIndicatorsPerCall} indicators cannot go into one call: the request is then null and the indicators are listed one by one. " +
        "No saved workspace: workspaceFound false. Read-only. The same document is the resource " + ResourceUri + ".";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{}";

    public string InputSchemaJson => ToolSchema.Object().Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => await BuildAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The document of the tool and of the resource.</summary>
    public async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        WorkspaceSettings? workspace;
        IReadOnlyList<CoreIndicatorSettings> sources;
        IReadOnlyList<CoreIndicatorSettings> drivers;
        try
        {
            workspace = await _workspace.LoadAsync(cancellationToken).ConfigureAwait(false);
            sources = await _registered.GetSourceIndicatorsAsync(cancellationToken).ConfigureAwait(false);
            drivers = await _registered.GetDynamicPeriodDriversAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "The saved workspace is temporarily unreadable.", hint: "Retry in a moment.");
        }

        var notes = new List<string> { "The document is the last saved workspace file; changes in the running application that are not saved yet are not included." };
        if (workspace is null)
        {
            return ToolJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("workspaceFound", false);
                WriteStrings(writer, "notes", notes);
                writer.WriteEndObject();
            });
        }

        TimeFrame? timeFrame = ToTimeFrame(workspace.SelectedTimeframe, notes);
        var registeredById = new Dictionary<string, CoreIndicatorSettings>(StringComparer.Ordinal);
        foreach (CoreIndicatorSettings item in sources.Concat(drivers))
        {
            registeredById.TryAdd(item.Id, item);
        }

        var workspaceById = new Dictionary<string, CoreIndicatorSettings>(StringComparer.Ordinal);
        foreach (CoreIndicatorSettings item in workspace.Indicators)
        {
            workspaceById.TryAdd(item.Id, item);
        }

        List<CoreIndicatorSettings> requested = RequestedIndicators(workspace.Indicators, workspaceById, registeredById, notes);
        if (timeFrame is { } chartFrame)
        {
            requested = WithoutRefusedTimeframes(requested, chartFrame, notes);
        }

        bool requestFits = requested.Count <= _limits.MaxIndicatorsPerCall;
        if (!requestFits)
        {
            notes.Add($"{requested.Count} indicators (with what they read) exceed the {_limits.MaxIndicatorsPerCall} of one sa_calculate_indicators call; request is null.");
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("workspaceFound", true);
            writer.WriteString("name", workspace.Name);
            writer.WriteString("savedAt", ToolJson.FormatDateTime(workspace.LastModified));
            WriteNullableString(writer, "symbol", workspace.SelectedTicker);
            WriteNullableString(writer, "savedTimeFrame", workspace.SelectedTimeframe);
            WriteNullableString(writer, "timeFrame", timeFrame?.ToString());
            writer.WriteNumber("maxCandleCount", workspace.MaxCandleCount);
            WriteNullableString(writer, "chartType", workspace.ChartType);

            writer.WriteStartArray("activeIndicators");
            foreach (CoreIndicatorSettings settings in workspace.Indicators)
            {
                WriteIndicator(writer, settings, workspaceById, registeredById, includeState: true, requestFrame: null, chainParticipants: null);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("request");
            if (requestFits && timeFrame is not null && !string.IsNullOrWhiteSpace(workspace.SelectedTicker) && requested.Count > 0)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", workspace.SelectedTicker);
                writer.WriteString("timeFrame", timeFrame.Value.ToString());
                writer.WriteStartArray("indicators");
                HashSet<string> chainParticipants = IndicatorTimeframePlan.CollectChainParticipants(requested);
                foreach (CoreIndicatorSettings settings in requested)
                {
                    WriteIndicator(writer, settings, workspaceById, registeredById, includeState: false, requestFrame: timeFrame.Value, chainParticipants);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            WriteStrings(writer, "notes", notes);
            writer.WriteEndObject();
        });
    }

    /// <summary>The saved timeframe as a chart time frame, or null for one the MCP tools do not serve (a note says so).</summary>
    private static TimeFrame? ToTimeFrame(string? saved, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return null;
        }

        if (Enum.TryParse(saved, ignoreCase: true, out TimeframeType type) && MonitoringTimeframes.IsSupported(type))
        {
            return MonitoringTimeframes.ToCoreTimeFrame(type);
        }

        notes.Add($"The saved time frame '{saved}' is not served by the MCP tools (D1, W1 and MN1); there is no request.");
        return null;
    }

    /// <summary>
    /// The indicators the request carries: the enabled ones the catalogue can calculate, each followed by what it reads (workspace first, then registered), upstream before downstream.
    /// An indicator whose input cannot be found or calculated is left out, with a note.
    /// </summary>
    private List<CoreIndicatorSettings> RequestedIndicators(
        IReadOnlyList<CoreIndicatorSettings> workspaceIndicators,
        Dictionary<string, CoreIndicatorSettings> workspaceById,
        Dictionary<string, CoreIndicatorSettings> registeredById,
        List<string> notes)
    {
        var result = new List<CoreIndicatorSettings>();
        var added = new HashSet<string>(StringComparer.Ordinal);

        bool TryAdd(CoreIndicatorSettings settings, HashSet<string> path)
        {
            if (added.Contains(settings.Id))
            {
                return true;
            }

            if (!IsCalculable(settings) || !path.Add(settings.Id))
            {
                return false; // not calculable, or a cycle
            }

            foreach (string? reference in new[] { settings.SourceIndicatorId, settings.DynamicPeriodIndicatorId })
            {
                if (string.IsNullOrEmpty(reference))
                {
                    continue;
                }

                CoreIndicatorSettings? upstream = workspaceById.GetValueOrDefault(reference) ?? registeredById.GetValueOrDefault(reference);
                if (upstream is null || !TryAdd(upstream, path))
                {
                    return false;
                }
            }

            added.Add(settings.Id);
            result.Add(settings);
            return true;
        }

        foreach (CoreIndicatorSettings settings in workspaceIndicators.Where(s => s.IsEnabled))
        {
            if (!TryAdd(settings, new HashSet<string>(StringComparer.Ordinal)))
            {
                notes.Add($"Indicator '{settings.Id}' ({settings.TypeEnum}) is not in the request: it, or an indicator it reads, cannot be calculated or was not found.");
            }
        }

        return result;
    }

    /// <summary>
    /// Leaves out every indicator the application itself refuses on a chart of <paramref name="chartFrame"/> (<see cref="IndicatorTimeframePlan.IsRefused"/>: a finer frame than the chart's,
    /// Monthly on a Weekly chart, ...), with a note that carries the application's own text. One rule for every such combination, so that the request is always one
    /// sa_calculate_indicators can run (it rejects a finer indicator frame for the whole call) and never shows a value the chart does not draw.
    /// An indicator of a chain is never refused (a chain is calculated on the chart's own frame), so no upstream is removed from under a downstream indicator.
    /// </summary>
    private static List<CoreIndicatorSettings> WithoutRefusedTimeframes(List<CoreIndicatorSettings> requested, TimeFrame chartFrame, List<string> notes)
    {
        HashSet<string> chainParticipants = IndicatorTimeframePlan.CollectChainParticipants(requested);
        var kept = new List<CoreIndicatorSettings>(requested.Count);
        foreach (CoreIndicatorSettings settings in requested)
        {
            IndicatorTimeframePlan plan = IndicatorTimeframePlan.Classify(settings, chartFrame, chainParticipants);
            if (plan.IsRefused)
            {
                notes.Add($"Indicator '{settings.Id}' ({settings.TypeEnum}) is not in the request: the application does not calculate this time frame combination ({plan.RefusalMessage(chartFrame)})");
            }
            else
            {
                kept.Add(settings);
            }
        }

        return kept;
    }

    private bool IsCalculable(CoreIndicatorSettings settings)
        => settings.TypeEnum is { } type && _catalog.Find(type.ToString()) is { IsRegistered: true };

    /// <summary>
    /// One indicator. <paramref name="includeState"/> (the list of the workspace) adds the display name, whether it is enabled and whether it can be calculated; without it the object is
    /// exactly an entry of the <c>indicators</c> argument of sa_calculate_indicators: id, type, parameters (only the names the catalogue lists), priceSource, the chain references and,
    /// for an indicator calculated on a coarser frame than the call's (the Timeframe of the indicator settings), its own <c>timeFrame</c>.
    /// </summary>
    private void WriteIndicator(
        Utf8JsonWriter writer,
        CoreIndicatorSettings settings,
        Dictionary<string, CoreIndicatorSettings> workspaceById,
        Dictionary<string, CoreIndicatorSettings> registeredById,
        bool includeState,
        TimeFrame? requestFrame,
        HashSet<string>? chainParticipants)
    {
        IndicatorCatalogEntry? entry = settings.TypeEnum is { } type ? _catalog.Find(type.ToString()) : null;
        writer.WriteStartObject();
        writer.WriteString("id", settings.Id);
        WriteNullableString(writer, "type", settings.TypeEnum?.ToString());
        if (includeState)
        {
            writer.WriteString("displayName", settings.GetFormattedDisplayName());
            writer.WriteBoolean("isEnabled", settings.IsEnabled);
            writer.WriteBoolean("isCalculable", entry is { IsRegistered: true });
        }

        writer.WriteStartObject("parameters");
        if (entry is not null)
        {
            Dictionary<string, object> values = IndicatorSideResolver.ParameterValues(settings.ParameterObject);
            foreach (IndicatorParameterDescriptor descriptor in entry.Parameters)
            {
                if (values.TryGetValue(descriptor.Name, out object? value) && WriteValue(writer, descriptor.Name, value))
                {
                    continue;
                }
            }
        }

        writer.WriteEndObject();
        writer.WriteString("priceSource", settings.PriceSource.ToString());
        if (OwnTimeFrame(settings, requestFrame, chainParticipants) is { } ownFrame)
        {
            writer.WriteString("timeFrame", ownFrame.ToString());
        }

        if (!string.IsNullOrEmpty(settings.SourceIndicatorId))
        {
            writer.WriteString("sourceIndicatorId", settings.SourceIndicatorId);
            WriteUpstreamSeries(writer, "sourceOutputSeriesName", settings.SourceIndicatorId, workspaceById, registeredById);
        }

        if (!string.IsNullOrEmpty(settings.DynamicPeriodIndicatorId))
        {
            writer.WriteString("dynamicPeriodIndicatorId", settings.DynamicPeriodIndicatorId);
            WriteUpstreamSeries(writer, "dynamicPeriodOutputSeriesName", settings.DynamicPeriodIndicatorId, workspaceById, registeredById);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The frame an indicator of the request is calculated on when it differs from the call's: the effective frame of an indicator the application calculates on a coarser frame than the
    /// chart's (<see cref="IndicatorTimeframeMode.Aligned"/>, from <see cref="IndicatorTimeframePlan.Classify"/>, the one rule of the analysis pipeline: the setting applies, the indicator is
    /// not part of a chain, the frame is coarser and calculable from the chart's). Pivots keep their own source period in <c>parameters</c> and have none. Null otherwise, including for the
    /// workspace list (no call frame).
    /// </summary>
    private static TimeFrame? OwnTimeFrame(CoreIndicatorSettings settings, TimeFrame? requestFrame, HashSet<string>? chainParticipants)
    {
        if (requestFrame is not { } callFrame || chainParticipants is null || !IndicatorTimeframePlan.NeedsPlan(settings))
        {
            return null;
        }

        IndicatorTimeframePlan plan = IndicatorTimeframePlan.Classify(settings, callFrame, chainParticipants);
        return plan.Mode == IndicatorTimeframeMode.Aligned ? plan.EffectiveFrame : null;
    }

    /// <summary>
    /// The series of the upstream indicator that the application reads: the one that indicator has selected (<c>OutputSeriesName</c>), as the analysis pipeline does. The application's
    /// "Main" is its main series, which a request names by leaving the argument out (sa_calculate_indicators names series by their output names, such as RSI or Signal).
    /// </summary>
    private static void WriteUpstreamSeries(
        Utf8JsonWriter writer, string name, string upstreamId, Dictionary<string, CoreIndicatorSettings> workspaceById, Dictionary<string, CoreIndicatorSettings> registeredById)
    {
        CoreIndicatorSettings? upstream = workspaceById.GetValueOrDefault(upstreamId) ?? registeredById.GetValueOrDefault(upstreamId);
        if (upstream is not null && !string.IsNullOrEmpty(upstream.OutputSeriesName) && !string.Equals(upstream.OutputSeriesName, IndicatorResult.MainSeriesName, StringComparison.Ordinal))
        {
            writer.WriteString(name, upstream.OutputSeriesName);
        }
    }

    /// <summary>One parameter value as JSON: numbers and booleans as they are, text and enum values as text; any other kind is left out.</summary>
    private static bool WriteValue(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case bool flag:
                writer.WriteBoolean(name, flag);
                return true;
            case int or long or short or byte:
                writer.WriteNumber(name, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case decimal number:
                writer.WriteNumber(name, number);
                return true;
            case double number when double.IsFinite(number):
                writer.WriteNumber(name, number);
                return true;
            case float number when float.IsFinite(number):
                writer.WriteNumber(name, number);
                return true;
            case string text:
                writer.WriteString(name, text);
                return true;
            case Enum enumValue:
                writer.WriteString(name, enumValue.ToString());
                return true;
            default:
                return false;
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
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
