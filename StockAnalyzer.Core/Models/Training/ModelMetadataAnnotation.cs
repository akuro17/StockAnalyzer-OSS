using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Training;

public sealed record ModelUpdateHistoryEntry(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("fieldName")] string FieldName,
    [property: JsonPropertyName("oldValue")] string? OldValue,
    [property: JsonPropertyName("newValue")] string? NewValue,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record ModelMetadataAnnotation
{
    [JsonPropertyName("key")]
    public required ModelMetadataKey Key { get; init; }

    [JsonPropertyName("customDisplayName")]
    public string? CustomDisplayName { get; init; }

    [JsonPropertyName("annotationCreatedUtc")]
    public DateTimeOffset AnnotationCreatedUtc { get; init; }

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; init; }

    [JsonPropertyName("history")]
    public ImmutableList<ModelUpdateHistoryEntry> History { get; init; } = ImmutableList<ModelUpdateHistoryEntry>.Empty;
}

public sealed record ModelMetadataStore(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("entries")] ImmutableList<ModelMetadataAnnotation> Entries);
