using System.Globalization;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Rendering;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>A saved drawing chosen for the image: what the renderer draws and what the answer echoes (the object, its tool and its control points as saved).</summary>
internal sealed record ImageSavedDrawing(Guid ObjectId, string Tool, AiChartDrawing Drawing, IReadOnlyList<(DateTime Time, decimal Price)> Anchors);

/// <summary>The saved drawings chosen for the image and how many drawable ones were left out because a control point is dated after endDate (see <see cref="DrawingAsOfRule"/>).</summary>
internal sealed record ImageSavedDrawingSelection(IReadOnlyList<ImageSavedDrawing> Drawings, int AfterEndDate)
{
    public static ImageSavedDrawingSelection Empty { get; } = new(Array.Empty<ImageSavedDrawing>(), 0);
}

/// <summary>
/// Chooses the saved drawings of the Standard context that sa_render_chart_image can draw with includeSavedDrawings (owner decision U-14): the horizontal line, the vertical line, the trend line,
/// the ray and the parallel channel on the main panel, with time-based control points, that the user did not hide (neither the object nor its layer), in the order of the application chart:
/// layer order, then the order inside the layer; an object in no layer is not drawn. With an endDate an object that has a control point dated after it is left out too (owner decision M-2 = A),
/// and that test comes BEFORE the cap, so an object that cannot be drawn never takes one of the MaxImageSavedDrawings places. Everything else stays out, and the
/// answer says how many objects that was. The saved type names are the ones <see cref="DrawingValueCatalog"/> knows (the one definition of the class names the application saves).
/// </summary>
internal static class ImageSavedDrawings
{
    public const string NotFoundNote = "No drawing file is stored for this symbol and time frame, so no saved drawing is drawn.";

    public static ImageSavedDrawingSelection Select(
        DrawingDocumentReadResult read,
        IReadOnlyList<DateTime> barTimes,
        int firstDrawnIndex,
        int maxDrawings,
        DateOnly? endDate,
        List<string> notes,
        List<string> warnings)
    {
        var chosen = new List<ImageSavedDrawing>();
        if (read.Status == DrawingDocumentReadStatus.NotFound)
        {
            notes.Add(NotFoundNote);
            return ImageSavedDrawingSelection.Empty;
        }

        if (read.Status == DrawingDocumentReadStatus.LegacyFormat)
        {
            warnings.Add(SavedDrawingNotices.LegacyWarning);
            return ImageSavedDrawingSelection.Empty;
        }

        DrawingContextState? found = null;
        if (read.Document is { } document)
        {
            foreach (DrawingContextState candidate in document.Contexts)
            {
                if (string.Equals(candidate.ContextName, DrawingContextNames.Standard, StringComparison.Ordinal))
                {
                    found = candidate;
                    break;
                }
            }
        }

        if (found is not { } context)
        {
            notes.Add($"The drawing file has no '{DrawingContextNames.Standard}' context, so no saved drawing is drawn.");
            return ImageSavedDrawingSelection.Empty;
        }

        // The application draws what its layers hold: layer order, then the order inside the layer (DRAWING_TOOLS_SPEC invariant 9, LayeredDrawingScene); an object in no layer is not drawn.
        var recordsById = new Dictionary<Guid, DrawingObjectRecord>();
        foreach (DrawingObjectRecord record in context.Objects)
        {
            recordsById.TryAdd(record.ObjectId, record);
        }

        var inAnyLayer = new HashSet<Guid>(context.Layers.SelectMany(layer => layer.ObjectIds));
        var seen = new HashSet<Guid>();
        int otherKinds = 0;
        int afterEndDate = 0;
        int tooMany = 0;
        foreach (DrawingLayerRecord layer in context.Layers)
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            foreach (Guid objectId in layer.ObjectIds)
            {
                if (!recordsById.TryGetValue(objectId, out DrawingObjectRecord record)
                    || !seen.Add(objectId)
                    || !DrawingStoredOptions.IsVisible(record.StyleAndParameters))
                {
                    continue;
                }

                AiChartDrawingKind? kind = KindOf(record);
                if (kind is null)
                {
                    otherKinds++;
                    continue;
                }

                if (endDate is { } asOf && DrawingAsOfRule.IsAfter(record, asOf))
                {
                    afterEndDate++;
                    continue;
                }

                if (chosen.Count >= maxDrawings)
                {
                    tooMany++;
                    continue;
                }

                chosen.Add(Place(record, kind.Value, barTimes, firstDrawnIndex));
            }
        }

        int noLayer = context.Objects.Count(record => !inAnyLayer.Contains(record.ObjectId) && DrawingStoredOptions.IsVisible(record.StyleAndParameters) && KindOf(record) is not null);
        if (noLayer > 0)
        {
            notes.Add($"{noLayer.ToString(CultureInfo.InvariantCulture)} saved drawing(s) belong to no layer and are not drawn, as in the application's chart and image export.");
        }

        if (otherKinds > 0)
        {
            notes.Add($"{otherKinds.ToString(CultureInfo.InvariantCulture)} saved object(s) of other kinds (text, Fibonacci tools, shapes ...) are not drawn; sa_get_chart_drawings lists every saved object.");
        }

        if (tooMany > 0)
        {
            warnings.Add($"{tooMany.ToString(CultureInfo.InvariantCulture)} more saved drawing(s) are not drawn: at most {maxDrawings.ToString(CultureInfo.InvariantCulture)} are drawn (the first in layer order, then in the order inside the layer).");
        }

        if (afterEndDate > 0)
        {
            warnings.Add(DrawingAsOfRule.ExcludedWarning(afterEndDate));
        }

        return new ImageSavedDrawingSelection(chosen, afterEndDate);
    }

    /// <summary>The drawing of a saved object: its control points in bar units of the drawn window (the time map is every loaded bar), with the saved extend and channel modes.</summary>
    private static ImageSavedDrawing Place(DrawingObjectRecord record, AiChartDrawingKind kind, IReadOnlyList<DateTime> barTimes, int firstDrawnIndex)
    {
        var anchors = new List<(DateTime Time, decimal Price)>(record.Points.Count);
        var points = new List<AiChartDrawingPoint>(record.Points.Count);
        foreach (DrawingStoredPoint point in record.Points)
        {
            DateTime time = DateTime.SpecifyKind(point.UtcTime!.Value, DateTimeKind.Unspecified);
            anchors.Add((time, point.YValue));
            points.Add(new AiChartDrawingPoint(AiChartTimeAxis.BarPosition(barTimes, time) - firstDrawnIndex, point.YValue));
        }

        return new ImageSavedDrawing(
            record.ObjectId,
            kind.ToString(),
            new AiChartDrawing(
                kind,
                points,
                kind == AiChartDrawingKind.TrendLine ? DrawingStoredOptions.ReadLineExtendMode(record.StyleAndParameters) : LineExtendMode.None,
                kind == AiChartDrawingKind.ParallelChannel ? DrawingStoredOptions.ReadChannelMode(record.StyleAndParameters) : ChannelMode.Parallel),
            anchors);
    }

    /// <summary>The kind of drawing a saved object is, or null when the image does not draw it (another type, the panel of an indicator, a non-time axis, a wrong number of points).</summary>
    private static AiChartDrawingKind? KindOf(DrawingObjectRecord record)
    {
        if (record.Panel.Kind != PanelKind.Main
            || record.CoordinateKind != DrawingCoordinateKind.UtcTime
            || !DrawingValueCatalog.TryGetStored(record.TypeName, null, out DrawingValueDescriptor descriptor)
            || record.Points.Count != descriptor.PointCount
            || record.Points.Any(point => point.Kind != DrawingCoordinateKind.UtcTime || point.UtcTime is null))
        {
            return null;
        }

        return descriptor.Name switch
        {
            DrawingValueCatalog.HorizontalLineName => AiChartDrawingKind.HorizontalLine,
            DrawingValueCatalog.VerticalLineName => AiChartDrawingKind.VerticalLine,
            DrawingValueCatalog.TrendLineName => AiChartDrawingKind.TrendLine,
            DrawingValueCatalog.RayName => AiChartDrawingKind.Ray,
            DrawingValueCatalog.ParallelChannelName => AiChartDrawingKind.ParallelChannel,
            _ => null,
        };
    }
}
