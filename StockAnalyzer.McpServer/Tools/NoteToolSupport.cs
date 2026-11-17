using System.Text.Json;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What the two note tools share: reading the notes (an unreadable database is DATA_UNAVAILABLE, never "no notes") and writing one note.</summary>
internal static class NoteToolSupport
{
    /// <summary>The longest text of a parent or quoted note that <c>includeParentContext</c> adds, in characters; the full text is one more call away.</summary>
    internal const int ContextPreviewChars = 200;

    public static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "The notes database is temporarily unreadable.", hint: "Retry in a moment.");
        }
    }

    /// <summary>One note: its identity, text, ticker, tags, catalyst date and thread links. Attachments are counted, never read.</summary>
    public static void WriteNote(Utf8JsonWriter writer, Note note, IReadOnlyDictionary<Guid, Note>? context = null)
    {
        writer.WriteStartObject();
        writer.WriteString("id", note.Id.ToString());
        writer.WriteString("createdAt", ToolJson.FormatDateTime(note.CreatedAt));
        writer.WriteString("updatedAt", ToolJson.FormatDateTime(note.UpdatedAt));
        if (note.ChartAnchorDate is { } anchor)
        {
            writer.WriteString("chartAnchorDate", ToolJson.FormatDate(anchor));
        }

        if (note.ChartTimeframe is { } timeFrame)
        {
            writer.WriteString("chartTimeFrame", timeFrame.ToString());
        }

        if (note.RelatedTicker is { } ticker)
        {
            writer.WriteString("relatedTicker", ticker);
        }

        writer.WriteBoolean("isPinned", note.IsPinned);
        if (note.CatalystDate is { } catalyst)
        {
            writer.WriteString("catalystDate", ToolJson.FormatDate(catalyst));
        }

        writer.WriteStartArray("hashtags");
        foreach (string tag in note.Hashtags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("linkUrls");
        foreach (string url in note.LinkUrls)
        {
            writer.WriteStringValue(url);
        }

        writer.WriteEndArray();
        writer.WriteNumber("attachmentCount", note.AttachmentIds.Length);
        if (note.ParentNoteId is { } parentId)
        {
            writer.WriteString("parentNoteId", parentId.ToString());
        }

        if (note.QuotedNoteId is { } quotedId)
        {
            writer.WriteString("quotedNoteId", quotedId.ToString());
        }

        writer.WriteString("body", note.Body);
        if (context is not null)
        {
            WriteContext(writer, "parentContext", note.ParentNoteId, context);
            WriteContext(writer, "quotedContext", note.QuotedNoteId, context);
        }

        writer.WriteEndObject();
    }

    /// <summary>The note a reply or quote points to: a preview of its text, or only that it was deleted or is gone.</summary>
    private static void WriteContext(Utf8JsonWriter writer, string name, Guid? id, IReadOnlyDictionary<Guid, Note> context)
    {
        if (id is not { } noteId)
        {
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("id", noteId.ToString());
        if (!context.TryGetValue(noteId, out Note? note))
        {
            writer.WriteBoolean("exists", false);
        }
        else if (note.IsDeleted)
        {
            writer.WriteBoolean("exists", true);
            writer.WriteBoolean("isDeleted", true);
        }
        else
        {
            writer.WriteBoolean("exists", true);
            writer.WriteBoolean("isDeleted", false);
            writer.WriteString("createdAt", ToolJson.FormatDateTime(note.CreatedAt));
            if (note.RelatedTicker is { } ticker)
            {
                writer.WriteString("relatedTicker", ticker);
            }

            bool cut = note.Body.Length > ContextPreviewChars;
            writer.WriteString("bodyPreview", cut ? note.Body[..ContextPreviewChars] : note.Body);
            writer.WriteBoolean("bodyCut", cut);
        }

        writer.WriteEndObject();
    }

    /// <summary>The notes that the returned notes' <c>parentNoteId</c> and <c>quotedNoteId</c> point to.</summary>
    public static async Task<IReadOnlyDictionary<Guid, Note>> ContextAsync(IReadOnlyNoteSource source, IEnumerable<Note> notes, CancellationToken cancellationToken)
    {
        Guid[] ids = notes.SelectMany(n => new[] { n.ParentNoteId, n.QuotedNoteId }).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        return await ReadAsync(() => source.GetByIdsAsync(ids, cancellationToken)).ConfigureAwait(false);
    }
}
