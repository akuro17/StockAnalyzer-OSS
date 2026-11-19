using System.Text.Json;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What the two note tools share: reading the notes (an unreadable database is DATA_UNAVAILABLE, never "no notes") and writing one note.</summary>
internal static class NoteToolSupport
{
    /// <summary>The longest text of a parent or quoted note that <c>includeParentContext</c> adds, in characters; the full text is one more call away.</summary>
    internal const int ContextPreviewChars = 200;

    /// <summary>The longest body text emitted by <c>sa_search_notes</c> when <c>snippetOnly</c> is true, in characters.</summary>
    internal const int BodySnippetMaxChars = 200;

    private const int SnippetMatchLeadingContextChars = 40;

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

    /// <summary>
    /// Extracts a bounded UTF-16 safe snippet around the earliest occurrence of any word in <paramref name="queryWords"/>,
    /// returning whether truncation occurred.
    /// </summary>
    internal static (string Text, bool Truncated) ExtractSnippet(string body, IReadOnlyList<string>? queryWords, int maxChars = BodySnippetMaxChars)
    {
        if (body.Length <= maxChars)
        {
            return (body, false);
        }

        int earliestMatch = int.MaxValue;
        if (queryWords is not null)
        {
            foreach (string word in queryWords)
            {
                if (string.IsNullOrEmpty(word))
                {
                    continue;
                }

                int index = body.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < earliestMatch)
                {
                    earliestMatch = index;
                }
            }
        }

        int anchor = earliestMatch == int.MaxValue ? 0 : earliestMatch;
        int start = Math.Max(0, Math.Min(anchor - SnippetMatchLeadingContextChars, body.Length - maxChars));
        if (start > 0 && char.IsLowSurrogate(body[start]))
        {
            start++;
        }

        int length = Math.Min(maxChars, body.Length - start);
        if (length > 0 && char.IsHighSurrogate(body[start + length - 1]))
        {
            length--;
        }

        return (body.Substring(start, length), true);
    }

    /// <summary>One note: its identity, text, ticker, tags, catalyst date and thread links. Attachments are counted, never read.</summary>
    public static void WriteNote(Utf8JsonWriter writer, Note note, IReadOnlyDictionary<Guid, Note>? context = null, bool snippetOnly = false, IReadOnlyList<string>? queryWords = null)
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

        if (snippetOnly)
        {
            (string snippet, bool bodyTruncated) = ExtractSnippet(note.Body, queryWords);
            writer.WriteString("body", snippet);
            writer.WriteBoolean("bodyTruncated", bodyTruncated);
        }
        else
        {
            writer.WriteString("body", note.Body);
        }

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
