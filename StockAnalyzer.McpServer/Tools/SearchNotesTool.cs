using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_search_notes: the user's Ticker Notes (the investment journal) filtered by words, ticker, hashtag and creation date, newest first (owner decision U-08).
/// Words are matched as plain text, case-insensitively, as the Notes filter of the application does (<see cref="NoteFilterEngine"/>): <c>%</c>, <c>_</c> and <c>\</c> mean themselves.
/// Read-only: the database is opened read-only and the notes in the trash are never returned.
/// </summary>
internal sealed class SearchNotesTool : IMcpTool
{
    private const string QueryArgument = "query";
    private const string MatchModeArgument = "matchMode";
    private const string TickerArgument = "ticker";
    private const string HashtagArgument = "hashtag";
    private const string CreatedFromArgument = "createdFrom";
    private const string CreatedToArgument = "createdTo";
    private const string LimitArgument = "limit";
    private const string IncludeParentContextArgument = "includeParentContext";
    private const string MatchAll = "All";
    private const string MatchAny = "Any";

    /// <summary>The most words one query may hold; more is LIMIT_EXCEEDED (the words are never cut).</summary>
    internal const int MaxQueryWords = 10;

    private readonly IReadOnlyNoteSource _source;
    private readonly McpLimits _limits;

    public SearchNotesTool(IReadOnlyNoteSource source, McpLimits limits)
    {
        _source = source;
        _limits = limits;
    }

    public string Name => "sa_search_notes";

    public string Description =>
        "Searches the user's Ticker Notes (investment journal entries): each note's text, related ticker, hashtags, catalyst date, creation time and thread links, newest first. " +
        $"query holds up to {MaxQueryWords} words separated by white space, matched as plain text inside the note text, ignoring case ('%', '_' and '\\' are ordinary characters); " +
        $"{MatchModeArgument} {MatchAll} (default) needs every word, {MatchAny} at least one. {TickerArgument} (a symbol, '.' and '-' interchangeable), {HashtagArgument} (with or without '#') and " +
        $"{CreatedFromArgument}/{CreatedToArgument} ({ToolJson.DateFormat}, inclusive, local dates of the creation time) narrow the search; all given conditions must hold. With no condition the newest notes are returned. " +
        $"At most {_limits.MaxReturnedNotes} notes are returned ({LimitArgument}); totalMatched counts all matches and truncated says when more exist. " +
        $"{IncludeParentContextArgument} true adds a short preview of the note a reply answers (parentContext) and of the note a quote cites (quotedContext). " +
        "Notes in the trash are never returned. Attachments are counted (attachmentCount), not read. Read-only; no database means no notes.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"query\":\"earnings guidance\",\"matchMode\":\"All\",\"ticker\":\"7203-T\",\"limit\":20}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(QueryArgument, $"Words to find in the note text, separated by white space (at most {MaxQueryWords}); plain text, case-insensitive.")
        .String(MatchModeArgument, $"{MatchAll}: every word must be in the note. {MatchAny}: at least one. Default {MatchAll}.", allowedValues: new[] { MatchAll, MatchAny })
        .String(TickerArgument, "Only notes whose related ticker is this symbol.")
        .String(HashtagArgument, "Only notes carrying this hashtag (with or without '#', case-insensitive).")
        .String(CreatedFromArgument, $"First creation date to include ({ToolJson.DateFormat}).")
        .String(CreatedToArgument, $"Last creation date to include ({ToolJson.DateFormat}).")
        .Integer(LimitArgument, $"Most notes returned. Default and maximum {_limits.MaxReturnedNotes}.", defaultValue: _limits.MaxReturnedNotes, minimum: 1, maximum: _limits.MaxReturnedNotes)
        .Boolean(IncludeParentContextArgument, "true adds a preview of the replied-to and the quoted note. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string[] words = (ToolArgs.String(arguments, QueryArgument) ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (words.Length > MaxQueryWords)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"At most {MaxQueryWords} words per query.", QueryArgument, $"Use {MaxQueryWords} or fewer words.");
        }

        bool matchAll = !string.Equals(ToolArgs.String(arguments, MatchModeArgument), MatchAny, StringComparison.Ordinal);
        string? ticker = ToolArgs.String(arguments, TickerArgument) is { } tickerText ? SymbolResolver.Canonical(tickerText, TickerArgument, allowSynthetic: false) : null;
        string? hashtag = ToolArgs.String(arguments, HashtagArgument)?.Trim().TrimStart('#');
        if (hashtag is { Length: 0 })
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{HashtagArgument}' is empty.", HashtagArgument);
        }

        DateOnly? from = ToolArgs.Date(arguments, CreatedFromArgument);
        DateOnly? to = ToolArgs.Date(arguments, CreatedToArgument);
        if (from > to)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{CreatedFromArgument}' is after '{CreatedToArgument}'.", CreatedFromArgument);
        }

        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, _limits.MaxReturnedNotes, _limits.MaxReturnedNotes);
        bool includeContext = ToolArgs.Boolean(arguments, IncludeParentContextArgument) ?? false;

        var criteria = new NoteFilterCriteria
        {
            SelectedTickers = ticker is null ? null : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ticker),
            PeriodStartInclusive = from?.ToDateTime(TimeOnly.MinValue),
            PeriodEndInclusive = to?.ToDateTime(TimeOnly.MaxValue),
        };

        IReadOnlyList<Note> all = await NoteToolSupport.ReadAsync(() => _source.GetActiveNotesAsync(cancellationToken)).ConfigureAwait(false);
        List<Note> matched = all
            .Where(note => NoteFilterEngine.Matches(note, criteria))
            .Where(note => hashtag is null || note.Hashtags.Contains(hashtag, StringComparer.OrdinalIgnoreCase))
            .Where(note => MatchesWords(note, words, matchAll))
            .ToList();
        List<Note> returned = matched.Take(limit).ToList();
        IReadOnlyDictionary<Guid, Note>? context = includeContext
            ? await NoteToolSupport.ContextAsync(_source, returned, cancellationToken).ConfigureAwait(false)
            : null;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(MatchModeArgument, matchAll ? MatchAll : MatchAny);
            writer.WriteNumber("totalMatched", matched.Count);
            writer.WriteNumber("returnedCount", returned.Count);
            writer.WriteNumber(LimitArgument, limit);
            writer.WriteBoolean("truncated", matched.Count > returned.Count);
            writer.WriteStartArray("notes");
            foreach (Note note in returned)
            {
                NoteToolSupport.WriteNote(writer, note, context);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>Every word (or any word) is in the note text: the filter of the application, one word at a time.</summary>
    private static bool MatchesWords(Note note, string[] words, bool matchAll)
    {
        if (words.Length == 0)
        {
            return true;
        }

        bool Has(string word) => NoteFilterEngine.Matches(note, new NoteFilterCriteria { SearchText = word });
        return matchAll ? words.All(Has) : words.Any(Has);
    }
}
