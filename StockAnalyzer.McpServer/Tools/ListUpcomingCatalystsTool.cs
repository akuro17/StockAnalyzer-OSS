using System.Text.Json;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_upcoming_catalysts: the notes that carry a catalyst date (an expected event) inside a window that starts at fromDate, earliest first (owner decision U-09).
/// fromDate defaults to today's local date (the local date <see cref="Note.CatalystDate"/> is stored as) and is always echoed.
/// </summary>
internal sealed class ListUpcomingCatalystsTool : IMcpTool
{
    private const string FromDateArgument = "fromDate";
    private const string HorizonDaysArgument = "horizonDays";
    private const string TickerArgument = "ticker";
    private const string LimitArgument = "limit";

    private readonly IReadOnlyNoteSource _source;
    private readonly McpLimits _limits;
    private readonly TimeProvider _time;

    public ListUpcomingCatalystsTool(IReadOnlyNoteSource source, McpLimits limits, TimeProvider? time = null)
    {
        _source = source;
        _limits = limits;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "sa_list_upcoming_catalysts";

    public string Description =>
        $"Lists Ticker Notes carrying catalyst dates from fromDate up to horizonDays (max {_limits.MaxCatalystHorizonDays}, excluding trash). Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"horizonDays\":30}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(FromDateArgument, $"First date of the window ({ToolJson.DateFormat}). Default: today's local date.")
        .Integer(HorizonDaysArgument, $"Days after fromDate the window reaches. Default and maximum {_limits.MaxCatalystHorizonDays}.", defaultValue: _limits.MaxCatalystHorizonDays, minimum: 1, maximum: _limits.MaxCatalystHorizonDays)
        .String(TickerArgument, "Only notes whose related ticker is this symbol.")
        .Integer(LimitArgument, $"Most notes returned. Default and maximum {_limits.MaxReturnedNotes}.", defaultValue: _limits.MaxReturnedNotes, minimum: 1, maximum: _limits.MaxReturnedNotes)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        DateOnly from = ToolArgs.Date(arguments, FromDateArgument) ?? DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        int horizon = ToolArgs.PositiveWithin(arguments, HorizonDaysArgument, _limits.MaxCatalystHorizonDays, _limits.MaxCatalystHorizonDays);
        string? ticker = ToolArgs.String(arguments, TickerArgument) is { } tickerText ? SymbolResolver.Canonical(tickerText, TickerArgument, allowSynthetic: false) : null;
        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, _limits.MaxReturnedNotes, _limits.MaxReturnedNotes);
        DateOnly to = from.AddDays(horizon);

        IReadOnlyList<Note> notes = await NoteToolSupport.ReadAsync(() => _source.GetCatalystNotesAsync(cancellationToken)).ConfigureAwait(false);
        List<Note> matched = notes
            .Where(note => note.CatalystDate is { } catalyst && DateOnly.FromDateTime(catalyst) >= from && DateOnly.FromDateTime(catalyst) <= to)
            .Where(note => ticker is null || string.Equals(note.RelatedTicker, ticker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<Note> returned = matched.Take(limit).ToList();

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(FromDateArgument, from.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("toDate", to.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteNumber(HorizonDaysArgument, horizon);
            writer.WriteNumber("totalMatched", matched.Count);
            writer.WriteNumber("returnedCount", returned.Count);
            writer.WriteNumber(LimitArgument, limit);
            writer.WriteBoolean("truncated", matched.Count > returned.Count);
            writer.WriteStartArray("catalysts");
            foreach (Note note in returned)
            {
                writer.WriteStartObject();
                writer.WriteNumber("daysUntil", DateOnly.FromDateTime(note.CatalystDate!.Value).DayNumber - from.DayNumber);
                writer.WritePropertyName("note");
                NoteToolSupport.WriteNote(writer, note);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
