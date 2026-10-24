using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One indicator of a sa_calculate_indicators call after binding: the client's id, its catalogue entry and its settings.</summary>
internal sealed class RequestedIndicator
{
    public RequestedIndicator(int index, string id, IndicatorCatalogEntry entry, CoreIndicatorSettings settings, string? sourceId, string? sourceSeries, string? dynamicId, string? dynamicSeries)
    {
        Index = index;
        Id = id;
        Entry = entry;
        Settings = settings;
        SourceId = sourceId;
        SourceSeries = sourceSeries;
        DynamicId = dynamicId;
        DynamicSeries = dynamicSeries;
    }

    public int Index { get; }

    public string Id { get; }

    public IndicatorCatalogEntry Entry { get; }

    public CoreIndicatorSettings Settings { get; }

    public string? SourceId { get; }

    public string? SourceSeries { get; }

    public string? DynamicId { get; }

    public string? DynamicSeries { get; }

    public string Path => $"indicators[{Index}]";
}

/// <summary>
/// Validates and wires indicator-on-indicator references before the pipeline runs (the pipeline's own circular fallback is not
/// relied on): every reference names an indicator of the same call, source and dynamic-period edges form no cycle, the series asked
/// of an upstream indicator exists, and one upstream indicator is asked for one series only (the pipeline reads an upstream
/// indicator's series through that indicator's own output-series setting). Also computes which output series look ahead.
/// </summary>
internal sealed class IndicatorChainPlan
{
    private const string MainSeries = IndicatorResult.MainSeriesName;

    private readonly IIndicatorFactory _factory;

    public IndicatorChainPlan(IIndicatorFactory factory)
    {
        _factory = factory;
    }

    public void Apply(IReadOnlyList<RequestedIndicator> requests)
    {
        var byId = requests.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var asked = new Dictionary<string, (string Series, string Path)>(StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (RequestedIndicator request in requests)
        {
            edges[request.Id] = new List<string>();
            Reference(request, request.SourceId, request.SourceSeries, "sourceIndicatorId", "sourceOutputSeriesName", byId, asked, edges, isSource: true);
            Reference(request, request.DynamicId, request.DynamicSeries, "dynamicPeriodIndicatorId", "dynamicPeriodOutputSeriesName", byId, asked, edges, isSource: false);
        }

        RejectCycles(requests, byId, edges);

        foreach ((string upstreamId, (string series, _)) in asked)
        {
            byId[upstreamId].Settings.OutputSeriesName = series;
        }
    }

    /// <summary>The output series of <paramref name="request"/> whose value at a bar depends on later bars (its own, or all of them when its input does).</summary>
    public IReadOnlyList<string> NonCausalSeries(RequestedIndicator request, IReadOnlyList<RequestedIndicator> requests)
    {
        var byId = requests.ToDictionary(r => r.Id, StringComparer.Ordinal);
        return NonCausalSeries(request, byId, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
    }

    private IReadOnlyList<string> NonCausalSeries(RequestedIndicator request, Dictionary<string, RequestedIndicator> byId, Dictionary<string, IReadOnlyList<string>> memo)
    {
        if (memo.TryGetValue(request.Id, out IReadOnlyList<string>? known))
        {
            return known;
        }

        IReadOnlyList<string> result = request.Entry.NonCausalSeries;
        if (UsesNonCausalInput(request, request.SourceId, request.SourceSeries, byId, memo)
            || UsesNonCausalInput(request, request.DynamicId, request.DynamicSeries, byId, memo))
        {
            result = request.Entry.OutputSeriesNames;
        }

        memo[request.Id] = result;
        return result;
    }

    private bool UsesNonCausalInput(RequestedIndicator request, string? upstreamId, string? series, Dictionary<string, RequestedIndicator> byId, Dictionary<string, IReadOnlyList<string>> memo)
    {
        if (upstreamId is null)
        {
            return false;
        }

        RequestedIndicator upstream = byId[upstreamId];
        IReadOnlyList<string> flagged = NonCausalSeries(upstream, byId, memo);
        return series is null
            ? flagged.Count > 0 && flagged.Count == upstream.Entry.OutputSeriesNames.Count
            : flagged.Contains(CanonicalSeries(upstream, series), StringComparer.Ordinal);
    }

    private void Reference(
        RequestedIndicator request,
        string? upstreamId,
        string? series,
        string idArgument,
        string seriesArgument,
        Dictionary<string, RequestedIndicator> byId,
        Dictionary<string, (string Series, string Path)> asked,
        Dictionary<string, List<string>> edges,
        bool isSource)
    {
        string idPath = $"{request.Path}.{idArgument}";
        string seriesPath = $"{request.Path}.{seriesArgument}";
        if (upstreamId is null)
        {
            if (series is not null)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{seriesArgument}' needs '{idArgument}'.", seriesPath);
            }

            return;
        }

        if (!byId.TryGetValue(upstreamId, out RequestedIndicator? upstream))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{idArgument}' names '{upstreamId}', which is not an indicator id of this call.",
                idPath,
                "Reference the id of another indicator in the same indicators array.");
        }

        if (ReferenceEquals(upstream, request))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Indicator '{request.Id}' cannot use itself as input (circular reference).", idPath);
        }

        string normalized = MainSeries;
        if (series is not null)
        {
            string canonical = CanonicalSeriesOrThrow(upstream, series, seriesPath);
            normalized = IndicatorOutputSeriesResolver.Normalize(Enum.Parse<IndicatorType>(upstream.Entry.Type), canonical, upstream.Settings.PriceSource, _factory);
        }

        if (asked.TryGetValue(upstream.Id, out (string Series, string Path) earlier) && !string.Equals(earlier.Series, normalized, StringComparison.Ordinal))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Indicator '{upstream.Id}' is asked for different output series by several indicators ('{earlier.Series}' and '{normalized}'); one series per upstream indicator is supported.",
                series is null ? idPath : seriesPath,
                "Request the same series everywhere, or add the upstream indicator twice under different ids.");
        }

        asked[upstream.Id] = (normalized, series is null ? idPath : seriesPath);
        edges[request.Id].Add(upstream.Id);
        if (isSource)
        {
            request.Settings.SourceIndicatorId = upstream.Settings.Id;
        }
        else
        {
            request.Settings.DynamicPeriodIndicatorId = upstream.Settings.Id;
        }
    }

    private static string CanonicalSeries(RequestedIndicator indicator, string series)
        => indicator.Entry.OutputSeriesNames.FirstOrDefault(n => string.Equals(n, series, StringComparison.OrdinalIgnoreCase)) ?? series;

    private static string CanonicalSeriesOrThrow(RequestedIndicator upstream, string series, string seriesPath)
    {
        string? match = upstream.Entry.OutputSeriesNames.FirstOrDefault(n => string.Equals(n, series.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ToolException(
            ToolErrorCodes.InvalidOutputSeries,
            $"{upstream.Entry.Type} has no output series '{series}'.",
            seriesPath,
            $"Output series of {upstream.Entry.Type}: {string.Join(", ", upstream.Entry.OutputSeriesNames)}.");
    }

    private static void RejectCycles(IReadOnlyList<RequestedIndicator> requests, Dictionary<string, RequestedIndicator> byId, Dictionary<string, List<string>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the current path, 2 = done
        foreach (RequestedIndicator request in requests)
        {
            Visit(request.Id);
        }

        void Visit(string id)
        {
            if (state.TryGetValue(id, out int s))
            {
                if (s == 1)
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Indicator '{id}' takes part in a circular reference.",
                        $"{byId[id].Path}.sourceIndicatorId");
                }

                return;
            }

            state[id] = 1;
            foreach (string upstream in edges[id])
            {
                Visit(upstream);
            }

            state[id] = 2;
        }
    }
}
