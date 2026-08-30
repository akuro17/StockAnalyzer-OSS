using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Minimal IThemeManager test double defaulting to ThemeColors.Dark (matching production's
/// own field-initializer default), so the real NotesSettingsManager can be constructed in tests
/// without a live ThemeManager. CurrentTheme is settable to let a test simulate a theme switch and
/// assert NotesSettingsManager's default-color-follows-theme reactivity via the PropertyChanged event.</summary>
public sealed class FakeThemeManager : IThemeManager
{
    private ThemeColors _currentTheme = ThemeColors.Dark;
    public ThemeColors CurrentTheme
    {
        get => _currentTheme;
        set { _currentTheme = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme))); }
    }

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.Dark;
    public void ChangeTheme(ThemeColors newTheme) => CurrentTheme = newTheme;
    public void SetThemeMode(AppThemeMode mode) => CurrentMode = mode;
    public void UpdateSingleColor(ThemeColorKey key, IndicatorColor color) { }
    public IReadOnlyDictionary<ThemeColorKey, IndicatorColor> GetCurrentColors() => new Dictionary<ThemeColorKey, IndicatorColor>();
    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Shared test doubles for the Notes-tab test suite (sa_constraint_check Phase 2, CODE_REVIEW_GUIDELINES.md
/// §3 Testing Standards "Mocking I/O: Use shared mock foundations... DRY"). Previously each of
/// NoteTimelineViewModelTests.cs and NoteTimelineView_UnifiedSearchTests.cs (and, before it was
/// replaced, NoteTimelineView_ScopeMenuTests.cs) carried its own private copy of these five classes.
/// IDispatcherService is intentionally not duplicated here - use the existing shared
/// StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService instead. FakeDialogService also
/// absorbed the ShowConfirmationAsync-only variant formerly private to
/// EditTickerNotesDialogViewModelTests.cs (sa_constraint_check Phase 3).
/// </summary>
public class FakeDialogService : IDialogService
{
    public int ShowNoteTrashDialogAsyncCallCount { get; private set; }

    /// <summary>The initialTab most recently passed to ShowNoteTrashDialogAsync - lets tests assert
    /// which tab a "Trash"/"Orphaned Files" toolbar icon requested without a real dialog window.</summary>
    public StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab? LastRequestedInitialTab { get; private set; }

    public Task ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab initialTab = StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Deleted)
    {
        ShowNoteTrashDialogAsyncCallCount++;
        LastRequestedInitialTab = initialTab;
        return Task.CompletedTask;
    }

    /// <summary>Confirmation gate result/tracking (sa_constraint_check Phase 3, DialogService fake
    /// consolidation): previously duplicated as a private FakeDialogService in
    /// EditTickerNotesDialogViewModelTests.cs for the History-tab delete confirmation flow.</summary>
    public bool ConfirmationResult { get; set; }
    public int ConfirmationCallCount { get; private set; }
    public string? LastConfirmationTitle { get; private set; }
    public string? LastConfirmationMessage { get; private set; }

    public Task ShowAlertAsync(string title, string message) => throw new NotImplementedException();
    public Task<bool> ShowConfirmationAsync(string title, string message)
    {
        ConfirmationCallCount++;
        LastConfirmationTitle = title;
        LastConfirmationMessage = message;
        return Task.FromResult(ConfirmationResult);
    }
    public Task<string?> ShowInputAsync(string title, string message, string defaultValue = "") => throw new NotImplementedException();
    public Task<AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId) => throw new NotImplementedException();
    public Task<Transaction?> ShowEditTransactionDialogAsync(EditTransactionDialogViewModel viewModel) => throw new NotImplementedException();
    public Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12) => throw new NotImplementedException();
    public Task<DrawingSettingsResult> ShowDrawingSettingsDialogAsync(IChartObject drawing, Action<IChartObject>? onApply = null) => throw new NotImplementedException();
    public Task<global::Avalonia.Media.Color?> ShowColorPickerAsync(global::Avalonia.Media.Color initialColor) => throw new NotImplementedException();
    public Task ShowIndicatorSettingsDialogAsync(IEnumerable<CoreIndicatorSettings> currentIndicators, Action<IEnumerable<CoreIndicatorSettings>>? onApply = null) => throw new NotImplementedException();
    public Task ShowIndicatorPropertiesDialogAsync(CoreIndicatorSettings indicator, Action<CoreIndicatorSettings>? onApply = null, IEnumerable<CoreIndicatorSettings>? allIndicators = null) => throw new NotImplementedException();
    public Task ShowThemeSettingsDialogAsync() => throw new NotImplementedException();
    public Task ShowSettingsDialogAsync(string? initialCategoryKey = null) => throw new NotImplementedException();
    public Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null) => throw new NotImplementedException();
    public Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings, Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null) => throw new NotImplementedException();
    public Task ShowFilterTemplatePickerDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.FilterNode targetNode) => throw new NotImplementedException();
    public Task ShowFilterTemplatePickerForNewFilterDialogAsync(StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner, StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode parentNode) => throw new NotImplementedException();
    public Task ShowScreenerDialogAsync() => throw new NotImplementedException();
    public Task ShowTrainingWizardDialogAsync() => throw new NotImplementedException();
    public Task<BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags) => throw new NotImplementedException();
    public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? notes = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null) => throw new NotImplementedException();
    [Obsolete]
    public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? notes, Action<decimal?, decimal?, decimal?, string?>? onSave) => throw new NotImplementedException();
    public IMultiSyncProgressSession CreateMultiSyncProgressSession() => throw new NotImplementedException();
    public Task<PythonSetupDecision> ShowPythonSetupConfirmationAsync() => throw new NotImplementedException();
    public Task ShowManualSetupInstructionsAsync() => throw new NotImplementedException();
    public Task<PythonSetupDecision> ShowPythonUpdateConfirmationAsync() => throw new NotImplementedException();
    public Task ShowPythonManualUpdateInstructionsAsync() => throw new NotImplementedException();
    public Task RunWithProgressAsync(string title, Func<IProgress<string>, Task> action) => throw new NotImplementedException();
    public object? GetMainWindowOwner() => null;
    public Task ShowLogViewerAsync() => throw new NotImplementedException();
    public Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null) => throw new NotImplementedException();
    public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null, string? initialDirectory = null) => throw new NotImplementedException();
    public Task<string?> ShowOpenFolderDialogAsync(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public Task<bool> ShowExportChartImageDialogAsync(StockAnalyzer.Avalonia.ViewModels.ChartViewModel chartViewModel) => Task.FromResult(false);
    public void Shutdown() { }
    public void ActivateMainWindow() { }
}

/// <summary>Minimal INotesSettingsManager test double: holds the "Read more" thresholds in memory
/// (defaulting to production's 150/5) without touching the real user_notes_settings.json file.
/// Every setter raises PropertyChanged (sa_minimal_fix, Notes専用Settings ReadMoreThreshold fix
/// request #1) - matching NotesSettingsManager's own contract - since NoteTimelineViewModel now
/// subscribes to this event to auto-refresh an already-displayed timeline when a Settings &gt; Notes
/// value changes; a silent setter here would let that regression slip back in unnoticed.</summary>
public sealed class FakeNotesSettingsManager : INotesSettingsManager
{
    public double ThumbnailSizePixels { get; private set; } = 48.0;
    public NoteImageDisplayMode ImageDisplayMode { get; private set; } = NoteImageDisplayMode.AttachmentList;
    public int MaxRenderedThumbnails { get; private set; } = 3;
    public int ReadMoreMaxCharacters { get; private set; } = 150;
    public int ReadMoreMaxLines { get; private set; } = 5;
    public int ThreadCollapseThreshold { get; private set; } = 3;
    public int TailVisibleCount { get; private set; } = 2;
    public double ConnectorLineLength { get; private set; } = 60.0;
    public double DashLength { get; private set; } = 8.0;

    // Defaults to "effectively unlimited" (unlike NotesSettingsManager's production default of 50)
    // so the large majority of existing timeline tests - written before paging existed and asserting
    // the full fixture note count is displayed - keep passing unmodified. Tests that specifically
    // exercise paging/infinite-scroll behavior set this explicitly via SetTimelinePageSize.
    public int TimelinePageSize { get; private set; } = int.MaxValue;

    public double BodyFontSize { get; private set; } = 16.0;
    public IndicatorColor BodyTextColor { get; private set; } = IndicatorColor.FromRgb(255, 255, 255);
    public IndicatorColor BodyBackgroundColor { get; private set; } = IndicatorColor.FromRgb(0, 0, 0);
    public IndicatorColor UrlColor { get; private set; } = IndicatorColor.FromRgb(255, 255, 255);
    public IndicatorColor HashtagColor { get; private set; } = IndicatorColor.FromRgb(255, 255, 255);

    public void SetThumbnailSizePixels(double value) { ThumbnailSizePixels = value; RaisePropertyChanged(nameof(ThumbnailSizePixels)); }
    public void SetImageDisplayMode(NoteImageDisplayMode value) { ImageDisplayMode = value; RaisePropertyChanged(nameof(ImageDisplayMode)); }
    public void SetMaxRenderedThumbnails(int value) { MaxRenderedThumbnails = value; RaisePropertyChanged(nameof(MaxRenderedThumbnails)); }
    public void SetReadMoreMaxCharacters(int value) { ReadMoreMaxCharacters = value; RaisePropertyChanged(nameof(ReadMoreMaxCharacters)); }
    public void SetReadMoreMaxLines(int value) { ReadMoreMaxLines = value; RaisePropertyChanged(nameof(ReadMoreMaxLines)); }
    public void SetThreadCollapseThreshold(int value) { ThreadCollapseThreshold = value; RaisePropertyChanged(nameof(ThreadCollapseThreshold)); }
    public void SetTailVisibleCount(int value) { TailVisibleCount = value; RaisePropertyChanged(nameof(TailVisibleCount)); }
    public void SetConnectorLineLength(double value) { ConnectorLineLength = value; RaisePropertyChanged(nameof(ConnectorLineLength)); }
    public void SetDashLength(double value) { DashLength = value; RaisePropertyChanged(nameof(DashLength)); }
    public void SetTimelinePageSize(int value) { TimelinePageSize = value; RaisePropertyChanged(nameof(TimelinePageSize)); }
    public void SetBodyFontSize(double value) { BodyFontSize = value; RaisePropertyChanged(nameof(BodyFontSize)); }
    public void SetBodyTextColor(IndicatorColor value) { BodyTextColor = value; RaisePropertyChanged(nameof(BodyTextColor)); }
    public void SetBodyBackgroundColor(IndicatorColor value) { BodyBackgroundColor = value; RaisePropertyChanged(nameof(BodyBackgroundColor)); }
    public void SetUrlColor(IndicatorColor value) { UrlColor = value; RaisePropertyChanged(nameof(UrlColor)); }
    public void SetHashtagColor(IndicatorColor value) { HashtagColor = value; RaisePropertyChanged(nameof(HashtagColor)); }
    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Minimal IWatchlistManager test double (same shape as
/// ScreenerViewModelTests.DummyWatchlistManager): serves a fixed, caller-supplied profile list.</summary>
public sealed class FakeWatchlistManager : IWatchlistManager
{
    private readonly List<WatchlistProfile> _profiles;
    public event EventHandler? WatchlistsChanged { add { } remove { } }

    public FakeWatchlistManager(IEnumerable<WatchlistProfile>? profiles = null) => _profiles = profiles?.ToList() ?? new();

    public IReadOnlyList<WatchlistProfile> GetAllProfiles() => _profiles;
    public WatchlistProfile? GetProfileById(Guid profileId) => _profiles.FirstOrDefault(p => p.Id == profileId);
    public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false) => throw new NotImplementedException();
    public void UpdateProfileName(Guid profileId, string name) { }
    public void DeleteProfile(Guid profileId) { }
    public void AddTickerToProfile(Guid profileId, string ticker) { }
    public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers) { }
    public void RemoveTickerFromProfile(Guid profileId, string ticker) { }
    public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers) { }
    public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers) { }
    public void Initialize(IEnumerable<WatchlistProfile> profiles) { }
}

/// <summary>Minimal IMarketDataProvider test double: serves a fixed available-ticker list and a
/// per-ticker Tag string for LoadAvailableFilterOptionsAsync's registered-tag scan; every other
/// member is unused here (same pattern as EditTickerNotesDialogViewModelTests.FakeMarketDataProvider).</summary>
public sealed class FakeMarketDataProvider : IMarketDataProvider
{
    private readonly IReadOnlyList<string> _availableTickers;
    private readonly IReadOnlyDictionary<string, string> _tickerToTag;

    public FakeMarketDataProvider(IReadOnlyList<string>? availableTickers = null, IReadOnlyDictionary<string, string>? tickerToTag = null)
    {
        _availableTickers = availableTickers ?? Array.Empty<string>();
        _tickerToTag = tickerToTag ?? new Dictionary<string, string>();
    }

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult(_availableTickers);
    public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
    public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());
    public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) =>
        ValueTask.FromResult(_tickerToTag.TryGetValue(ticker, out var tag) ? TickerMetadata.Unknown with { Tag = tag } : TickerMetadata.Unknown);
    public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Task.FromResult(TickerMetadata.Unknown);
    public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
    public Task AddTickerAsync(string symbol) => Task.CompletedTask;
    public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
    public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
    public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
    public void InvalidateMetadataCache(string ticker) { }
    public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);
    public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
}

/// <summary>Minimal ITickerStateStore test double (sa_implement Task 3): mirrors just enough of
/// TickerListViewModel's real node tree - an AllTickersNode plus one WatchlistNode/PortfolioNode per
/// profile - for tests to pick a SelectedScopeNode and verify GetTickersForNode resolves it, without
/// depending on the real (heavy) TickerListViewModel. FilterNode entries are not built by
/// FromProfiles (WatchlistManager profiles have no concept of a Filter) - add them to Groups
/// directly when a test needs one.</summary>
public sealed class FakeTickerStateStore : ITickerStateStore
{
    public List<TickerGroupNode> Groups { get; } = new();
    IEnumerable<TickerGroupNode> ITickerStateStore.Groups => Groups;
    public IEnumerable<IFilterableSymbol> DisplayItems => Array.Empty<IFilterableSymbol>();

    public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node) => node switch
    {
        WatchlistNode wl => wl.Profile.Items.Select(i => i.Ticker).ToList(),
        PortfolioNode pf => pf.Profile.Items.Select(i => i.Ticker).ToList(),
        _ => Array.Empty<string>()
    };

    public static FakeTickerStateStore FromProfiles(IEnumerable<WatchlistProfile> profiles)
    {
        var store = new FakeTickerStateStore();
        store.Groups.Add(new AllTickersNode("All Tickers"));
        foreach (var profile in profiles)
        {
            store.Groups.Add(profile.IsPortfolio ? new PortfolioNode(profile) : new WatchlistNode(profile));
        }
        return store;
    }
}

/// <summary>
/// Shared construction helper for the full <see cref="NoteTimelineViewModel"/> dependency graph
/// (sa_constraint_check Phase 2, StockAnalyzer Constraint Check report 2026-08-14): NoteDatabaseConnectionManager
/// -&gt; NoteSchemaInitializer -&gt; NoteRepository -&gt; AttachmentRepository -&gt;
/// TickerMetadataNotesCacheSynchronizer -&gt; NoteTimelineViewModel. Consolidates what were five
/// separate, near-identical private copies of this exact wiring (NoteTimelineViewModelTests,
/// NoteTimelineView_UnifiedSearchTests, NoteDetailViewTests, NoteTimelineView_ComposePanelTests,
/// NoteTimelineView_ReplyConnectorLineTests) - the comment above <see cref="FakeDialogService"/>
/// already claimed this file solved Notes test-double duplication, but that only ever covered the
/// leaf Fakes; the code that wires them together into a runnable ViewModel was still copy-pasted
/// separately in each file. Every parameter is optional so a caller only supplies what it actually
/// customizes (e.g. NoteTimelineView_UnifiedSearchTests passes its own FakeMarketDataProvider/
/// FakeWatchlistManager/FakeTickerStateStore pre-seeded with specific tickers/profiles/nodes, then
/// does its own additional setup - LoadAvailableFilterOptionsAsync, seeding Notes, mounting a View -
/// on top of the returned Timeline). Mounting a View (NoteTimelineView vs. NoteDetailView, different
/// Width/Height per test) is deliberately left to each caller rather than folded in here, since that
/// part genuinely differs per test file.
/// </summary>
public static class NoteTimelineTestFixture
{
    public static async Task<(NoteTimelineViewModel Timeline, NoteRepository NoteRepository, FakeDialogService DialogService)> CreateTimelineAsync(
        string tempDir,
        IWatchlistManager? watchlistManager = null,
        IMarketDataProvider? marketDataProvider = null,
        IDispatcherService? dispatcherService = null,
        FakeTickerStateStore? tickerStateStore = null,
        INotesSettingsManager? notesSettingsManager = null)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();
        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
        var resolvedNotesSettingsManager = notesSettingsManager ?? new FakeNotesSettingsManager();
        var cacheSynchronizer = new TickerMetadataNotesCacheSynchronizer(
            noteRepository, UserStrategyMetadataRepository.Instance, resolvedNotesSettingsManager, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);

        var resolvedMarketDataProvider = marketDataProvider ?? new FakeMarketDataProvider();
        var resolvedWatchlistManager = watchlistManager ?? new FakeWatchlistManager();
        var resolvedDispatcherService = dispatcherService ?? new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        NoteEditorViewModel EditorFactory() => new(noteRepository, attachmentRepository, cacheSynchronizer, resolvedMarketDataProvider, resolvedDispatcherService, resolvedNotesSettingsManager, NullLogger<NoteEditorViewModel>.Instance);

        var dialogService = new FakeDialogService();
        var resolvedTickerStateStore = tickerStateStore ?? FakeTickerStateStore.FromProfiles(resolvedWatchlistManager.GetAllProfiles());
        var timeline = new NoteTimelineViewModel(
            noteRepository, schemaInitializer, cacheSynchronizer, EditorFactory, dialogService,
            resolvedWatchlistManager, resolvedMarketDataProvider, attachmentRepository, resolvedDispatcherService, resolvedTickerStateStore,
            new OrphanedAttachmentScanResultHolder(), resolvedNotesSettingsManager);
        return (timeline, noteRepository, dialogService);
    }
}
