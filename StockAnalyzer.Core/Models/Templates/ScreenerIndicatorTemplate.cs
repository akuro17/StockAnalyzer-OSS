using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing a collection of screener condition entries.
/// Stored independently under Data/Templates/Screener/.
/// </summary>
/// <remarks>
/// Schema 2 (<see cref="CurrentSchemaVersion"/>) is the first in which the time frame of a side is read by the Screener (each side reads the bars of its own frame).
/// Before it a weekly or monthly frame was stored but never used, so a template of schema 1 that has such a side has no defined meaning: it is discarded as a whole
/// (<see cref="IsDiscardedLegacy"/>), and every other schema-1 template is migrated to schema 2 unchanged. The file on disk is not touched by reading.
/// A file without a schema number is read as the current schema.
/// </remarks>
public class ScreenerIndicatorTemplate : TemplateBase, IJsonOnDeserialized
{
    /// <summary>The schema every newly built template has and every kept older template is migrated to.</summary>
    public const int CurrentSchemaVersion = 2;

    private bool _isDiscardedLegacy;

    public ScreenerIndicatorTemplate()
    {
        SchemaVersion = CurrentSchemaVersion;
    }

    public override TemplateType TemplateType => TemplateType.Screener;

    /// <inheritdoc />
    [JsonIgnore]
    public override bool IsDiscardedLegacy => _isDiscardedLegacy;

    private List<ScreenerIndicatorEntry> _entries = new();

    /// <summary>
    /// The collection of screener indicator entries in this template.
    /// </summary>
    public IReadOnlyList<ScreenerIndicatorEntry> Entries
    {
        get => _entries;
        init => _entries = value != null ? new List<ScreenerIndicatorEntry>(value) : new List<ScreenerIndicatorEntry>();
    }

    /// <summary>
    /// Replaces the entries in this template with a new collection.
    /// </summary>
    public void SetEntries(IEnumerable<ScreenerIndicatorEntry> entries)
    {
        _entries = entries != null ? new List<ScreenerIndicatorEntry>(entries) : new List<ScreenerIndicatorEntry>();
    }

    /// <summary>Called after every property of a read file is set: applies the schema rule above.</summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        // Disabled entries count too: enabling one would make a stored frame live with a meaning the author never gave it.
        if (_entries.Any(entry => ScreenerSideFrames.UsesNonDailyBars(entry)))
        {
            _isDiscardedLegacy = true;
            return;
        }

        SchemaVersion = CurrentSchemaVersion;
    }
}
