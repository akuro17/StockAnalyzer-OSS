using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_search_notes and sa_list_upcoming_catalysts (owner decisions U-08, U-09) through the real composition on a notes database written by the application's own classes.
/// </summary>
public sealed class NoteToolsTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT");
    private readonly string _notesRoot = Path.Combine(Path.GetTempPath(), "sa_note_tools_" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();
    private NoteRepository? _repository;

    public NoteToolsTests()
    {
        Directory.CreateDirectory(_notesRoot);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        SqliteConnection.ClearAllPools();
        _fixture.Dispose();
        Directory.Delete(_notesRoot, recursive: true);
    }

    private string DatabasePath => Path.Combine(_notesRoot, NoteDatabaseConnectionManager.DatabaseFileName);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class UnavailableNoteSource : IReadOnlyNoteSource
    {
        public Task<IReadOnlyList<Note>> GetActiveNotesAsync(CancellationToken cancellationToken = default) => throw new DataUnavailableException("locked");

        public Task<IReadOnlyList<Note>> GetCatalystNotesAsync(CancellationToken cancellationToken = default) => throw new DataUnavailableException("locked");

        public Task<IReadOnlyDictionary<Guid, Note>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) => throw new DataUnavailableException("locked");
    }

    private async Task<Note> AddAsync(string body, int day, Func<Note, Note>? shape = null, int hour = 9)
    {
        if (_repository is null)
        {
            var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, _notesRoot);
            await new NoteSchemaInitializer(manager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
            _repository = new NoteRepository(manager, NullLogger<NoteRepository>.Instance);
        }

        var created = new DateTime(2024, 3, day, hour, 0, 0, DateTimeKind.Local);
        Note note = new Note(Guid.NewGuid(), body, created, created);
        note = shape is null ? note : shape(note);
        await _repository.CreateAsync(note);
        return note;
    }

    private async Task<McpMessageDispatcher> StartAsync(IReadOnlyNoteSource? source = null, TimeProvider? time = null, params (string Key, string Value)[] limits)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limits)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new LogSink(new StringWriter()),
            timeProvider: time,
            noteSource: source ?? new SqliteReadOnlyNoteSource(DatabasePath));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string arguments = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, arguments))!;

    private static string[] Bodies(JsonDocument response)
        => McpTestSupport.Structured(response).GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("body").GetString()!).ToArray();

    // ---- sa_search_notes ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_WordsMatchAsPlainTextIgnoringCase_AllNeedsEveryWordAndAnyNeedsOne()
    {
        await AddAsync("Toyota earnings beat guidance", 1);
        await AddAsync("TOYOTA supplier risk", 2);
        await AddAsync("unrelated entry", 3);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument all = await CallAsync(dispatcher, "sa_search_notes", """{"query":"toyota EARNINGS"}""");
        using JsonDocument any = await CallAsync(dispatcher, "sa_search_notes", """{"query":"earnings supplier","matchMode":"Any"}""");
        using JsonDocument none = await CallAsync(dispatcher, "sa_search_notes", """{"query":"toyota missing"}""");
        using JsonDocument everything = await CallAsync(dispatcher, "sa_search_notes");

        Assert.Equal(new[] { "Toyota earnings beat guidance" }, Bodies(all));
        Assert.Equal(new[] { "TOYOTA supplier risk", "Toyota earnings beat guidance" }, Bodies(any)); // newest first
        Assert.Empty(Bodies(none));
        Assert.Equal(3, McpTestSupport.Structured(everything).GetProperty("totalMatched").GetInt32());
        Assert.Equal("All", McpTestSupport.Structured(all).GetProperty("matchMode").GetString());
    }

    [Theory]
    [InlineData("100%", "margin 100% sure")]
    [InlineData("a_b", "ticker a_b note")]
    [InlineData("c:\\\\temp", "see c:\\temp folder")]
    [InlineData("%", "margin 100% sure")]
    public async Task Search_PercentUnderscoreAndBackslashAreOrdinaryCharacters(string queryJson, string expectedBody)
    {
        await AddAsync("margin 100% sure", 1);
        await AddAsync("margin 100x sure", 2);
        await AddAsync("ticker a_b note", 3);
        await AddAsync("ticker axb note", 4);
        await AddAsync("see c:\\temp folder", 5);
        await AddAsync("see c:xtemp folder", 6);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_search_notes", $$"""{"query":"{{queryJson}}"}""");

        Assert.Equal(new[] { expectedBody }, Bodies(response));
    }

    [Fact]
    public async Task Search_TickerHashtagAndCreationDatesNarrowTheResult_AndTheTrashIsNeverReturned()
    {
        await AddAsync("a", 1, n => n with { RelatedTicker = "7203-T", Hashtags = ImmutableArray.Create("earnings") });
        await AddAsync("b", 10, n => n with { RelatedTicker = "7203-T", Hashtags = ImmutableArray.Create("risk") });
        await AddAsync("c", 20, n => n with { RelatedTicker = "MSFT", Hashtags = ImmutableArray.Create("earnings") });
        Note trashed = await AddAsync("d", 25, n => n with { RelatedTicker = "7203-T" });
        await _repository!.SoftDeleteAsync(trashed.Id);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument byTicker = await CallAsync(dispatcher, "sa_search_notes", """{"ticker":"7203.t"}""");
        using JsonDocument byHashtag = await CallAsync(dispatcher, "sa_search_notes", """{"hashtag":"#Earnings"}""");
        using JsonDocument byDates = await CallAsync(dispatcher, "sa_search_notes", """{"createdFrom":"2024-03-10","createdTo":"2024-03-20"}""");
        using JsonDocument combined = await CallAsync(dispatcher, "sa_search_notes", """{"ticker":"7203-T","hashtag":"earnings"}""");

        Assert.Equal(new[] { "b", "a" }, Bodies(byTicker));
        Assert.Equal(new[] { "c", "a" }, Bodies(byHashtag));
        Assert.Equal(new[] { "c", "b" }, Bodies(byDates)); // both end dates inclusive: the 20th at 09:00 is inside
        Assert.Equal(new[] { "a" }, Bodies(combined));
    }

    [Fact]
    public async Task Search_LimitCutsTheListAndTheAnswerSaysSo()
    {
        for (int day = 1; day <= 5; day++)
        {
            await AddAsync($"note {day}", day);
        }

        McpMessageDispatcher dispatcher = await StartAsync(limits: ("MaxReturnedNotes", "3"));

        using JsonDocument defaulted = await CallAsync(dispatcher, "sa_search_notes");
        using JsonDocument two = await CallAsync(dispatcher, "sa_search_notes", """{"limit":2}""");
        using JsonDocument tooMany = await CallAsync(dispatcher, "sa_search_notes", """{"limit":4}""");

        JsonElement result = McpTestSupport.Structured(defaulted);
        Assert.Equal(5, result.GetProperty("totalMatched").GetInt32());
        Assert.Equal(3, result.GetProperty("returnedCount").GetInt32());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { "note 5", "note 4" }, Bodies(two));
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(tooMany));
    }

    [Fact]
    public async Task Search_ParentContext_ShowsAPreviewOfTheRepliedToNoteAndOnlyTheFactForADeletedOrMissingOne()
    {
        Note parent = await AddAsync(new string('p', 250), 1);
        Note quoted = await AddAsync("quoted text", 2);
        Note trashedParent = await AddAsync("secret", 3);
        await AddAsync("reply", 4, n => n with { ParentNoteId = parent.Id, QuotedNoteId = quoted.Id });
        await AddAsync("orphan reply", 5, n => n with { ParentNoteId = trashedParent.Id, QuotedNoteId = Guid.NewGuid() });
        await _repository!.SoftDeleteAsync(trashedParent.Id);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument with = await CallAsync(dispatcher, "sa_search_notes", """{"query":"reply","includeParentContext":true}""");
        using JsonDocument without = await CallAsync(dispatcher, "sa_search_notes", """{"query":"reply"}""");

        JsonElement[] notes = McpTestSupport.Structured(with).GetProperty("notes").EnumerateArray().ToArray();
        JsonElement orphan = notes[0];
        JsonElement reply = notes[1];
        Assert.Equal(200, reply.GetProperty("parentContext").GetProperty("bodyPreview").GetString()!.Length);
        Assert.True(reply.GetProperty("parentContext").GetProperty("bodyCut").GetBoolean());
        Assert.Equal("quoted text", reply.GetProperty("quotedContext").GetProperty("bodyPreview").GetString());
        Assert.True(orphan.GetProperty("parentContext").GetProperty("isDeleted").GetBoolean());
        Assert.False(orphan.GetProperty("parentContext").TryGetProperty("bodyPreview", out _));
        Assert.False(orphan.GetProperty("quotedContext").GetProperty("exists").GetBoolean());
        Assert.DoesNotContain("secret", McpTestSupport.Structured(with).GetRawText());
        Assert.False(McpTestSupport.Structured(without).GetProperty("notes")[0].TryGetProperty("parentContext", out _));
    }

    [Theory]
    [InlineData("""{"query":"a b c d e f g h i j k"}""", "LIMIT_EXCEEDED", "query")]
    [InlineData("""{"createdFrom":"2024-03-10","createdTo":"2024-03-01"}""", "INVALID_ARGUMENT", "createdFrom")]
    [InlineData("""{"ticker":"bad/ticker"}""", "INVALID_ARGUMENT", "ticker")]
    [InlineData("""{"hashtag":"#"}""", "INVALID_ARGUMENT", "hashtag")]
    [InlineData("""{"matchMode":"Some"}""", "INVALID_ARGUMENT", "matchMode")]
    [InlineData("""{"limit":0}""", "INVALID_ARGUMENT", "limit")]
    public async Task Search_RefusesBadArguments(string arguments, string code, string parameter)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_search_notes", arguments);

        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("sa_search_notes")]
    [InlineData("sa_list_upcoming_catalysts")]
    public async Task NoDatabaseIsNoNotesAndNothingIsCreated_AnUnreadableOneIsDataUnavailable(string tool)
    {
        McpMessageDispatcher none = await StartAsync();
        McpMessageDispatcher unavailable = await StartAsync(new UnavailableNoteSource());

        using JsonDocument empty = await CallAsync(none, tool);
        using JsonDocument failed = await CallAsync(unavailable, tool);

        Assert.False(McpTestSupport.IsError(empty));
        Assert.Equal(0, McpTestSupport.Structured(empty).GetProperty("totalMatched").GetInt32());
        Assert.False(File.Exists(DatabasePath));
        Assert.Equal("DATA_UNAVAILABLE", McpTestSupport.ErrorCodeOf(failed));
    }

    // ---- sa_list_upcoming_catalysts ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Catalysts_AreTheNotesInsideTheWindowEarliestFirst_WithDaysUntilAndTheEchoedWindow()
    {
        await AddAsync("before", 1, n => n with { CatalystDate = new DateTime(2024, 4, 9), RelatedTicker = "MSFT" });
        await AddAsync("first day", 2, n => n with { CatalystDate = new DateTime(2024, 4, 10), RelatedTicker = "7203-T" });
        await AddAsync("later", 3, n => n with { CatalystDate = new DateTime(2024, 4, 20), RelatedTicker = "7203-T" });
        await AddAsync("last day", 4, n => n with { CatalystDate = new DateTime(2024, 4, 15), RelatedTicker = "MSFT" });
        await AddAsync("after", 5, n => n with { CatalystDate = new DateTime(2024, 4, 16) });
        await AddAsync("no catalyst", 6);
        Note trashed = await AddAsync("trashed", 7, n => n with { CatalystDate = new DateTime(2024, 4, 12) });
        await _repository!.SoftDeleteAsync(trashed.Id);
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument window = await CallAsync(dispatcher, "sa_list_upcoming_catalysts", """{"fromDate":"2024-04-10","horizonDays":5}""");
        using JsonDocument byTicker = await CallAsync(dispatcher, "sa_list_upcoming_catalysts", """{"fromDate":"2024-04-10","horizonDays":30,"ticker":"7203.T"}""");

        JsonElement result = McpTestSupport.Structured(window);
        Assert.Equal("2024-04-10", result.GetProperty("fromDate").GetString());
        Assert.Equal("2024-04-15", result.GetProperty("toDate").GetString());
        JsonElement[] catalysts = result.GetProperty("catalysts").EnumerateArray().ToArray();
        Assert.Equal(new[] { "first day", "last day" }, catalysts.Select(c => c.GetProperty("note").GetProperty("body").GetString()));
        Assert.Equal(new[] { 0, 5 }, catalysts.Select(c => c.GetProperty("daysUntil").GetInt32()));
        Assert.Equal("2024-04-10", catalysts[0].GetProperty("note").GetProperty("catalystDate").GetString());
        Assert.Equal(new[] { "first day", "later" }, McpTestSupport.Structured(byTicker).GetProperty("catalysts").EnumerateArray().Select(c => c.GetProperty("note").GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Catalysts_FromDateDefaultsToTodaysLocalDateAndIsEchoed()
    {
        await AddAsync("today", 1, n => n with { CatalystDate = new DateTime(2024, 4, 10) });
        await AddAsync("yesterday", 2, n => n with { CatalystDate = new DateTime(2024, 4, 9) });
        McpMessageDispatcher dispatcher = await StartAsync(time: new FixedClock(new DateTimeOffset(2024, 4, 10, 23, 30, 0, TimeSpan.Zero)));

        using JsonDocument response = await CallAsync(dispatcher, "sa_list_upcoming_catalysts");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("2024-04-10", result.GetProperty("fromDate").GetString());
        Assert.Equal("2025-04-10", result.GetProperty("toDate").GetString());
        Assert.Equal(365, result.GetProperty("horizonDays").GetInt32());
        Assert.Equal("today", Assert.Single(result.GetProperty("catalysts").EnumerateArray()).GetProperty("note").GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("""{"horizonDays":366}""", "LIMIT_EXCEEDED", "horizonDays")]
    [InlineData("""{"horizonDays":0}""", "INVALID_ARGUMENT", "horizonDays")]
    [InlineData("""{"fromDate":"10/04/2024"}""", "INVALID_ARGUMENT", "fromDate")]
    [InlineData("""{"limit":101}""", "LIMIT_EXCEEDED", "limit")]
    public async Task Catalysts_RefusesBadArguments(string arguments, string code, string parameter)
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "sa_list_upcoming_catalysts", arguments);

        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }
}
