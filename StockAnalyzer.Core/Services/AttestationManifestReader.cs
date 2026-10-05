using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>One bar's evidence as written in the manifest (the Parquet date, not yet an instant of the bar's anchor).</summary>
internal sealed record ManifestBar(DateOnly Date, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc,
    DateTimeOffset AvailableAtUtc, BarFinality Finality);

/// <summary>A parsed Attestation Manifest v2 (<c>&lt;instrument&gt;.attest.json</c>). Nothing here has touched the Parquet bytes yet.</summary>
internal sealed record AttestationManifest(string ProviderId, string InstrumentId, TimeframeType Timeframe, long RevisionEpoch,
    ulong ProviderSequence, DateTimeOffset AsOfUtc, string SourcePath, string SourceSha256, int RowCount,
    IReadOnlyList<ManifestBar> Bars, IReadOnlyList<BarTombstone> Tombstones);

/// <summary>
/// Strict reader of the producer attestation sidecar. The format extends the adopted Manifest v1 conventions: explicit UTC
/// spellings, lowercase hex SHA-256, a path that cannot leave the folder, and the producer's own declaration of finality.
/// The application verifies integrity and consistency only; it does not verify who wrote the file (no signature).
/// </summary>
internal static class AttestationManifestReader
{
    public const int SchemaVersion = 2;
    public const string FileSuffix = ".attest.json";

    public static AttestationManifest Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RejectDuplicateKeys(json);
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("The attestation manifest is not valid JSON.", ex); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The attestation manifest must be an object.");
            if (!Number(root, "schema_version", out JsonElement versionElement) || !versionElement.TryGetInt32(out var version) || version != SchemaVersion)
                throw new InvalidDataException("The attestation manifest schema_version is not supported.");
            var source = Member(root, "source");
            if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The attestation manifest source must be an object.");
            if (!Number(source, "row_count", out JsonElement rowElement) || !rowElement.TryGetInt32(out var rowCount) || rowCount < 0)
                throw new InvalidDataException("The attestation manifest row_count is invalid.");
            if (!Number(root, "revision_epoch", out JsonElement epochElement) || !epochElement.TryGetInt64(out var epoch) || epoch < 0)
                throw new InvalidDataException("The attestation manifest revision_epoch is invalid.");
            if (!Number(root, "provider_sequence", out JsonElement sequenceElement) || !sequenceElement.TryGetUInt64(out var sequence))
                throw new InvalidDataException("The attestation manifest provider_sequence is invalid.");
            var sha = Text(source, "sha256");
            if (!PredictionMonitoringReducer.ValidHash(sha)) throw new InvalidDataException("The attestation manifest source hash is invalid.");

            var bars = new List<ManifestBar>(rowCount);
            foreach (var bar in Array(root, "bar_evidence"))
            {
                if (bar.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bar evidence must be an object.");
                if (!DateOnly.TryParseExact(Text(bar, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidDataException("Bar evidence date must be yyyy-MM-dd.");
                bars.Add(new ManifestBar(date, Instant(bar, "period_start_utc"), Instant(bar, "period_end_utc"),
                    Instant(bar, "available_at_utc"), Text(bar, "finality") switch
                    {
                        "final" => BarFinality.Final,
                        "revoked" => BarFinality.ExplicitlyRevoked,
                        "unknown" => BarFinality.Unknown,
                        _ => throw new InvalidDataException("Bar finality must be final, revoked or unknown."),
                    }));
            }
            var tombstones = new List<BarTombstone>();
            foreach (var item in Array(root, "tombstones"))
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A tombstone must be an object.");
                string? reason = null;
                if (item.TryGetProperty("reason", out var reasonElement))
                    reason = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString()
                        : throw new InvalidDataException("A tombstone reason must be text.");
                tombstones.Add(new BarTombstone(Instant(item, "anchor_utc"), Instant(item, "available_at_utc"), reason));
            }
            return new AttestationManifest(Text(root, "provider_id"), Text(root, "instrument_id"), TimeframeOf(Text(root, "timeframe")), epoch,
                sequence, Instant(root, "as_of_utc"), Text(source, "path"), sha, rowCount, bars, tombstones);
        }
    }

    /// <summary>JSON allows a repeated key and parsers keep the last value; a producer file with one is ambiguous, so it is rejected in every object.</summary>
    private static void RejectDuplicateKeys(byte[] json)
    {
        var scopes = new Stack<HashSet<string>?>();
        var reader = new Utf8JsonReader(json);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: scopes.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: scopes.Push(null); break;
                    case JsonTokenType.EndObject: case JsonTokenType.EndArray: scopes.Pop(); break;
                    case JsonTokenType.PropertyName:
                        if (!scopes.Peek()!.Add(reader.GetString()!)) throw new InvalidDataException("The attestation manifest repeats a JSON key.");
                        break;
                }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("The attestation manifest is not valid JSON.", ex); }
    }

    private static TimeframeType TimeframeOf(string text) => text switch
    {
        "daily" => TimeframeType.Daily,
        "weekly" => TimeframeType.Weekly,
        "monthly" => TimeframeType.Monthly,
        _ => throw new InvalidDataException("The attestation manifest timeframe must be daily, weekly or monthly."),
    };

    private static bool Number(JsonElement element, string name, out JsonElement value)
    {
        value = Member(element, name);
        return value.ValueKind == JsonValueKind.Number;
    }

    private static JsonElement Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new InvalidDataException($"The attestation manifest is missing '{name}'.");

    private static string Text(JsonElement element, string name)
    {
        var value = Member(element, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The attestation manifest '{name}' must be non-empty text.");
        return value.GetString()!;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        var value = Member(element, name);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"The attestation manifest '{name}' must be an array.");
        return value.EnumerateArray();
    }

    // ISO 8601 date-time with whole seconds or a fraction; the offset is Z or +00:00 (checked below).
    private static readonly string[] InstantFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    /// <summary>An instant must be spelled exactly as ISO 8601 with an explicit UTC offset (Z or +00:00); anything else is rejected.</summary>
    private static DateTimeOffset Instant(JsonElement element, string name)
    {
        var text = Text(element, name);
        if (!(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new InvalidDataException("Manifest timestamps must explicitly use UTC (Z or +00:00).");
        // A fraction needs at least one digit ("00.Z" is not ISO 8601, although the format string alone would take it).
        if (text.IndexOf('.') is var dot and >= 0 && (dot + 1 >= text.Length || !char.IsAsciiDigit(text[dot + 1])))
            throw new InvalidDataException($"The attestation manifest '{name}' is not a valid UTC instant.");
        if (!DateTimeOffset.TryParseExact(text, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) || value.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"The attestation manifest '{name}' is not a valid UTC instant.");
        return value;
    }
}
