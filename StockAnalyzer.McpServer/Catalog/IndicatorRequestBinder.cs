using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// The one definition of how the JSON items of an indicator list become <see cref="RequestedIndicator"/>s: the id rules, the type lookup, the parameters (through
/// <see cref="IndicatorSettingsBinder"/>), the price source, the optional time frame of an indicator and the wiring names of an indicator that reads another one.
/// Shared by every tool that takes indicators in the shape of <c>sa_calculate_indicators</c>; the messages and paths are those that tool has always given.
/// </summary>
internal sealed class IndicatorRequestBinder
{
    /// <summary>The key of an indicator's own time frame.</summary>
    internal const string TimeFrameKey = "timeFrame";

    private readonly IndicatorCatalog _catalog;
    private readonly IndicatorSettingsBinder _binder;

    public IndicatorRequestBinder(IndicatorCatalog catalog, IndicatorSettingsBinder binder)
    {
        _catalog = catalog;
        _binder = binder;
    }

    /// <summary>
    /// Binds the items in order. Each item comes with the path that names it in errors (for example <c>indicators[0]</c>); ids must be unique over all the items.
    /// The index of a request is its position in <paramref name="items"/>.
    /// </summary>
    public List<RequestedIndicator> Bind(IEnumerable<(JsonElement Item, string Path)> items, TimeFrame callFrame)
    {
        var requests = new List<RequestedIndicator>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach ((JsonElement item, string path) in items)
        {
            string id = item.GetProperty("id").GetString()!;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "An indicator id must not be empty.", $"{path}.id");
            }

            if (!ids.Add(id))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Indicator id '{id}' is used more than once.", $"{path}.id", "Ids must be unique within one call.");
            }

            string typeName = item.GetProperty("type").GetString()!;
            IndicatorCatalogEntry entry = _catalog.Find(typeName)
                ?? throw new ToolException(ToolErrorCodes.UnknownIndicator, $"Unknown indicator type '{typeName}'.", $"{path}.type", "Call sa_get_capabilities to list the type names.");
            if (!entry.IsRegistered)
            {
                throw new ToolException(
                    ToolErrorCodes.UnknownIndicator,
                    $"Indicator type '{entry.Type}' is defined but cannot be calculated.",
                    $"{path}.type",
                    "Types with isRegistered false in sa_get_capabilities are not available.");
            }

            PriceType? priceSource = Optional(item, "priceSource") is { } priceName ? Enum.Parse<PriceType>(priceName) : null;
            item.TryGetProperty("parameters", out JsonElement parameters);
            CoreIndicatorSettings settings = _binder.Bind(entry, parameters, priceSource, path);
            (TimeFrame? written, TimeFrame? own) = ReadTimeFrame(item, callFrame, path);
            if (own is not null && settings.ParameterObject is ICrossTickerParameter)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{entry.Type} compares two symbols, whose bars are matched on the call's time frame, so it cannot have its own {TimeFrameKey}.",
                    $"{path}.{TimeFrameKey}",
                    $"Omit {TimeFrameKey} on this indicator (it then uses the call's {callFrame}).");
            }

            requests.Add(new RequestedIndicator(
                index,
                id,
                entry,
                settings,
                Optional(item, "sourceIndicatorId"),
                Optional(item, "sourceOutputSeriesName"),
                Optional(item, "dynamicPeriodIndicatorId"),
                Optional(item, "dynamicPeriodOutputSeriesName"))
            {
                WrittenTimeFrame = written,
                OwnTimeFrame = own,
                PathName = path,
            });
            index++;
        }

        return requests;
    }

    private static (TimeFrame? Written, TimeFrame? Own) ReadTimeFrame(JsonElement item, TimeFrame callFrame, string path)
    {
        if (!item.TryGetProperty(TimeFrameKey, out JsonElement element))
        {
            return (null, null);
        }

        string keyPath = $"{path}.{TimeFrameKey}";
        TimeFrame written = ToolArgs.SupportedTimeFrame(element.GetString()!, keyPath);
        if (ToolArgs.IsFinerThan(written, callFrame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"An indicator on {written} bars is finer than the call's {callFrame}.",
                keyPath,
                $"Give {callFrame} or a coarser time frame, or omit {TimeFrameKey}.");
        }

        return (written, written == callFrame ? null : written);
    }

    private static string? Optional(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
}
