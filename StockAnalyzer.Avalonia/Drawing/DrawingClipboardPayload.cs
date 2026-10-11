using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Data payload for clipboard copy and paste operations of chart drawings.
/// Encapsulates serialized object definitions, link groups, and source context metadata.
/// </summary>
public sealed record DrawingClipboardPayload
{
    public const string MimeType = "application/x-stockanalyzer-drawings+json";
    public const string SchemaVersion = "2.0";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = SchemaVersion;

    [JsonPropertyName("sourceTicker")]
    public string SourceTicker { get; init; } = string.Empty;

    [JsonPropertyName("sourceTimeframe")]
    public TimeframeType SourceTimeframe { get; init; }

    [JsonPropertyName("sourceContext")]
    public ChartDrawingContextType SourceContext { get; init; }

    [JsonPropertyName("referenceAnchor")]
    public ChartPoint ReferenceAnchor { get; init; }

    [JsonPropertyName("objects")]
    public IReadOnlyList<DrawingObjectRecord> Objects { get; init; } = Array.Empty<DrawingObjectRecord>();

    [JsonPropertyName("linkGroups")]
    public IReadOnlyList<DrawingLinkGroupRecord> LinkGroups { get; init; } = Array.Empty<DrawingLinkGroupRecord>();

    public DrawingClipboardPayload() { }

    [JsonConstructor]
    public DrawingClipboardPayload(
        string sourceTicker,
        TimeframeType sourceTimeframe,
        ChartDrawingContextType sourceContext,
        ChartPoint referenceAnchor,
        IReadOnlyList<DrawingObjectRecord> objects,
        IReadOnlyList<DrawingLinkGroupRecord> linkGroups,
        string schema = SchemaVersion)
    {
        SourceTicker = sourceTicker ?? string.Empty;
        SourceTimeframe = sourceTimeframe;
        SourceContext = sourceContext;
        ReferenceAnchor = referenceAnchor;
        Objects = objects ?? Array.Empty<DrawingObjectRecord>();
        LinkGroups = linkGroups ?? Array.Empty<DrawingLinkGroupRecord>();
        Schema = schema ?? SchemaVersion;
    }
}
