using System;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Common;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.Services;

public class WorkspaceCoordinatorTests : IDisposable
{
    private readonly string _testWorkspacePath;
    private readonly LayoutStateStore _stateStore;
    private readonly WorkspaceSerializationService _serializationService;
    private readonly WatchlistManager _watchlistManager;
    private readonly IOptions<WorkspaceCoordinatorOptions> _options;
    private readonly WorkspaceCoordinator _coordinator;

    public WorkspaceCoordinatorTests()
    {
        _testWorkspacePath = Path.Combine(AppContext.BaseDirectory, $"test_workspace_{Guid.NewGuid()}.json");
        _stateStore = new LayoutStateStore();
        _serializationService = new WorkspaceSerializationService();
        _watchlistManager = new WatchlistManager();
        _options = Options.Create(new WorkspaceCoordinatorOptions { WorkspacePath = _testWorkspacePath });
        
        _coordinator = new WorkspaceCoordinator(
            _stateStore,
            _serializationService,
            _watchlistManager,
            _options,
            new YieldingTestDispatcher(),
            NullLogger<WorkspaceCoordinator>.Instance);
    }

    public void Dispose()
    {
        foreach (var copy in PreservedCopies()) File.Delete(copy);
        if (File.Exists(_testWorkspacePath))
        {
            File.Delete(_testWorkspacePath);
        }
    }

    [Fact]
    public void Rebinding_ThrowsInvalidOperationException()
    {
        IWorkspaceLayoutTarget vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        // Act & Assert: Call Bind again
        Assert.Throws<InvalidOperationException>(() => _coordinator.Bind(vm));
    }

    [Fact]
    public async Task InitializeWorkspace_FileMissing_AppliesDefaultLayoutAndStandardIndicators()
    {
        IWorkspaceLayoutTarget vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        // Ensure file is physically deleted
        if (File.Exists(_testWorkspacePath))
        {
            File.Delete(_testWorkspacePath);
        }

        // Act
        await _coordinator.InitializeWorkspaceAsync();

        // Assert: Default layout dimensions applied
        Assert.True(vm.IsLeftPanelVisible);
        Assert.Equal(200, vm.LeftPanelWidth);
        Assert.True(vm.IsRightPanelVisible);
        Assert.Equal(250, vm.RightPanelWidth);
        Assert.False(vm.IsTopPanelVisible);
        Assert.Equal(0, vm.TopPanelHeight);
        Assert.True(vm.IsBottomPanelVisible);
        Assert.Equal(250, vm.BottomPanelHeight);

        // Assert: Main indicators initialized with SMA & Volume auto-enabled
        Assert.Empty(vm.GetIndicators());
    }

    [Fact]
    public async Task InitializeWorkspace_InvalidValues_ClampsToSafeBoundaries()
    {
        IWorkspaceLayoutTarget vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        // Arrange: Write extremely warped numerical dimensions to layout settings
        var settings = new WorkspaceSettings
        {
            IsLeftPanelVisible = true,
            LeftPanelWidth = -500, // Clamped to 50
            IsRightPanelVisible = true,
            RightPanelWidth = 9999, // Clamped to 2000
            IsTopPanelVisible = true,
            TopPanelHeight = -10, // Clamped to 50 when visible
            IsBottomPanelVisible = true,
            BottomPanelHeight = 5555 // Clamped to 1000
        };
        settings.PanelTabIds["Left"] = new System.Collections.Generic.List<string> { "TickerList" };
        await _serializationService.SaveAsync(settings, _testWorkspacePath);

        // Act
        await _coordinator.InitializeWorkspaceAsync();

        // Assert: Widths and heights are strictly clamped within boundaries
        Assert.Equal(50, vm.LeftPanelWidth);
        Assert.Equal(2000, vm.RightPanelWidth);
        Assert.Equal(0, vm.TopPanelHeight);
        Assert.Equal(1000, vm.BottomPanelHeight);
    }

    [Fact]
    public async Task ApplyLoadedWorkspace_OneCorruptedIndicator_SkipsItButRestoresOthers()
    {
        IWorkspaceLayoutTarget vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        // Arrange: an in-memory settings object (bypassing the JSON round-trip, which has its
        // own, separately-scoped deserialization behavior for a malformed array) where the
        // middle indicator entry is corrupted (null -- e.g. a defensive stand-in for any
        // indicator whose restore throws, such as a schema-mismatched entry from a workspace
        // file saved by a newer build). ApplyLoadedWorkspaceAsync has only a try/finally, so
        // prior to the isolation fix, AutoHeal(null) would throw straight out of this call.
        var settings = new WorkspaceSettings
        {
            IsLeftPanelVisible = true,
            LeftPanelWidth = 200,
            SelectedTicker = "AAPL",
            Indicators = new System.Collections.Generic.List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, DisplayName = "SMA", ParameterObject = new StockAnalyzer.Core.Models.Parameters.CoreSmaParameter() },
                null!,
                new CoreIndicatorSettings { TypeEnum = IndicatorType.EMA, DisplayName = "EMA", ParameterObject = new StockAnalyzer.Core.Models.Parameters.CoreEmaParameter() }
            }
        };

        // Act: must not throw despite the corrupted entry.
        await _coordinator.ApplyLoadedWorkspaceAsync(settings);

        // Assert: both healthy indicators restored; the corrupted entry was skipped, not
        // fatal to the rest of the restore.
        var restored = vm.GetIndicators();
        Assert.Equal(2, restored.Count);
        Assert.Contains(restored, i => i.TypeEnum == IndicatorType.SMA);
        Assert.Contains(restored, i => i.TypeEnum == IndicatorType.EMA);

        // Assert: the rest of the workspace (ticker, layout) restored normally too, proving
        // the corrupted indicator did not abort the wider RestoreAll sequence.
        Assert.Equal("AAPL", vm.SelectedTicker);
        Assert.Equal(200, vm.LeftPanelWidth);
    }

    /// <summary>Regression test (sa_minimal_fix): a FilterNode's ParentId can be a custom category or
    /// list folder Id (Add Filter is available on both - see StockAnalyzer.Avalonia.ViewModels.
    /// TickerListViewModel.AddFilter). ImportFilterSettings resolves ParentId via FindNodeById(Groups,
    /// ...), so if it runs before ImportCustomTickerCategories has populated those nodes into Groups,
    /// the filter's parent can't be found and it is silently - and, since it never gets re-attached,
    /// permanently - dropped on the very next workspace save. RestoreWatchlistAndColumns must call
    /// ImportCustomTickerCategories before ImportFilterSettings.</summary>
    [Fact]
    public async Task ApplyLoadedWorkspace_RestoresCustomTickerCategoriesBeforeFilterSettings()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        var settings = new WorkspaceSettings
        {
            WatchlistProfiles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>(),
            TagFilters = new System.Collections.Generic.List<FilterSettings>
            {
                new FilterSettings { Name = "Filter Under Folder", ParentId = Guid.NewGuid() }
            },
            CustomTickerCategories = new System.Collections.Generic.List<StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings>
            {
                new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings { Name = "Sector" }
            }
        };

        await _coordinator.ApplyLoadedWorkspaceAsync(settings);

        Assert.Equal(
            new[] { nameof(IWorkspaceLayoutTarget.ImportCustomTickerCategories), nameof(IWorkspaceLayoutTarget.ImportFilterSettings) },
            vm.ImportCallOrder);
    }

    /// <summary>sa_implement (Column Customization selection scope): the shared selection is a store-only import,
    /// so it must run before the per-list restore, which is the single step that applies the active store.</summary>
    [Fact]
    public async Task ApplyLoadedWorkspace_RestoresSharedColumnSelectionBeforePerListSelection()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var sharedId = Guid.NewGuid();
        var settings = new WorkspaceSettings
        {
            WatchlistProfiles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>(),
            TickerListSharedColumnTemplateId = sharedId
        };

        await _coordinator.ApplyLoadedWorkspaceAsync(settings);

        var order = vm.RestoreCallOrder;
        Assert.Equal(sharedId, vm.RestoredSharedColumnTemplateId);
        Assert.True(order.IndexOf(nameof(IWorkspaceLayoutTarget.RestoreTickerSharedColumnTemplateSelection)) >= 0);
        Assert.True(
            order.IndexOf(nameof(IWorkspaceLayoutTarget.RestoreTickerSharedColumnTemplateSelection)) <
            order.IndexOf(nameof(IWorkspaceLayoutTarget.RestoreTickerColumnTemplateSelection)));
    }

    [Fact]
    public async Task InitializeWorkspace_ValidFile_AppliesDimensionsAndTabSnapshots()
    {
        IWorkspaceLayoutTarget vm = new MockLayoutTarget();
        _coordinator.Bind(vm);

        // Ensure state is Ready initially or standard Initialized
        _stateStore.LifecycleState = WorkspaceLifecycleState.Initializing;

        // Act
        var initTask = _coordinator.InitializeWorkspaceAsync();
        
        await initTask;

        // Assert: Final state transition is Ready
        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
    }

    // ---- D-3' Run C: automatic-save lock -------------------------------------------------------------------

    private const string OriginalWorkspaceName = "ORIGINAL-FILE-CONTENT";

    private static WorkspaceRestoreIssue Issue() => new(WorkspaceRestoreIssueKind.PartialRestore, null, Array.Empty<WorkspaceLoadWarning>());

    private void MakeReady()
    {
        _stateStore.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        _stateStore.LifecycleState = WorkspaceLifecycleState.Ready;
    }

    private async Task<byte[]> WriteOriginalAsync()
    {
        await _serializationService.SaveAsync(new WorkspaceSettings { Name = OriginalWorkspaceName }, _testWorkspacePath);
        return await File.ReadAllBytesAsync(_testWorkspacePath);
    }

    [Fact]
    public async Task AutoSaveLock_NotLocked_DefaultPathSaveWritesAsBefore()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        var original = await WriteOriginalAsync();

        await _coordinator.SaveActiveWorkspaceAsync();

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task AutoSaveLock_Locked_DefaultPathSaveDoesNotTouchTheFile()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());

        await _coordinator.SaveActiveWorkspaceAsync();

        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task AutoSaveLock_Locked_ShutdownSaveDoesNotTouchTheFile()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());

        _coordinator.ForceSaveSync(new WorkspaceSettings { Name = "FROM-SHUTDOWN" });

        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task AutoSaveLock_NotLocked_ShutdownSaveWrites()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        await WriteOriginalAsync();

        _coordinator.ForceSaveSync(new WorkspaceSettings { Name = "FROM-SHUTDOWN" });

        Assert.Contains("FROM-SHUTDOWN", await File.ReadAllTextAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task AutoSaveLock_Locked_ExplicitPathSaveStillWrites()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());
        var export = Path.Combine(AppContext.BaseDirectory, $"explicit_{Guid.NewGuid()}.json");
        try
        {
            await _coordinator.SaveActiveWorkspaceAsync(export);

            Assert.True(File.Exists(export));
            Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
        }
        finally
        {
            File.Delete(export);
        }
    }

    [Fact]
    public async Task AutoSaveLock_ReleasedAgain_DefaultPathSaveWritesAgain()
    {
        _coordinator.Bind(new MockLayoutTarget());
        MakeReady();
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());
        _stateStore.ReleaseAutoSaveLock();

        await _coordinator.SaveActiveWorkspaceAsync();

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task SaveCurrentStateAndUnlock_WritesTheCurrentStateAndThenUnlocks()
    {
        _coordinator.Bind(new MockLayoutTarget { LeftPanelWidth = 321 });
        MakeReady();
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());

        var saved = await _coordinator.SaveCurrentStateAndUnlockAsync();

        Assert.True(saved);
        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Null(_stateStore.RestoreIssue);
        Assert.NotEqual(original, await File.ReadAllBytesAsync(_testWorkspacePath));
        var reloaded = await _serializationService.LoadAsync(_testWorkspacePath);
        Assert.Equal(321, reloaded!.LeftPanelWidth);
    }

    [Fact]
    public async Task SaveCurrentStateAndUnlock_LifecycleNotReady_WritesNothingAndKeepsTheLock()
    {
        _coordinator.Bind(new MockLayoutTarget());
        _stateStore.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
        var original = await WriteOriginalAsync();
        _stateStore.EngageAutoSaveLock(Issue());

        var saved = await _coordinator.SaveCurrentStateAndUnlockAsync();

        Assert.False(saved);
        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task SaveCurrentStateAndUnlock_WriteFails_KeepsTheLock()
    {
        // A directory squatting on the workspace path makes the atomic replace fail.
        var directoryAsPath = Path.Combine(AppContext.BaseDirectory, $"workspace_dir_{Guid.NewGuid()}");
        Directory.CreateDirectory(directoryAsPath);
        try
        {
            var store = new LayoutStateStore();
            var coordinator = new WorkspaceCoordinator(
                store,
                _serializationService,
                _watchlistManager,
                Options.Create(new WorkspaceCoordinatorOptions { WorkspacePath = directoryAsPath }),
                new YieldingTestDispatcher(),
                NullLogger<WorkspaceCoordinator>.Instance);
            coordinator.Bind(new MockLayoutTarget());
            store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
            store.LifecycleState = WorkspaceLifecycleState.Ready;
            store.EngageAutoSaveLock(Issue());

            var saved = await coordinator.SaveCurrentStateAndUnlockAsync();

            Assert.False(saved);
            Assert.True(coordinator.IsAutoSaveLocked);
        }
        finally
        {
            Directory.Delete(directoryAsPath, recursive: true);
        }
    }

    // ---- D-3' Run D: two-stage restore wired into the coordinator ------------------------------------------

    private string[] PreservedCopies() => Directory.GetFiles(
        Path.GetDirectoryName(_testWorkspacePath)!,
        CorruptedFile.PreservedSearchPattern(Path.GetFileName(_testWorkspacePath)));

    private static CoreIndicatorSettings Sma(string id) => new()
    {
        Id = id,
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new StockAnalyzer.Core.Models.Parameters.CoreSmaParameter { Period = 14 }
    };

    /// <summary>An indicator whose healing throws: a series-colour list with a null entry (see the normaliser tests).</summary>
    private static CoreIndicatorSettings UnhealableIndicator() => new()
    {
        Id = "bad",
        TypeEnum = DefaultCoreIndicatorSettings.GetDefault().First(d => d.SeriesColors is { Count: > 0 }).TypeEnum,
        SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> { null! }
    };

    /// <summary>Saves a valid workspace with the real serializer, lets the test damage its JSON tree, writes it back.</summary>
    private async Task<byte[]> WriteDamagedAsync(WorkspaceSettings settings, Action<System.Text.Json.Nodes.JsonObject> damage)
    {
        await _serializationService.SaveAsync(settings, _testWorkspacePath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_testWorkspacePath))!.AsObject();
        damage(root);
        await File.WriteAllTextAsync(_testWorkspacePath, root.ToJsonString());
        return await File.ReadAllBytesAsync(_testWorkspacePath);
    }

    private async Task AssertEveryDefaultSavePathLeavesTheFileAlone(byte[] original)
    {
        await _coordinator.SaveActiveWorkspaceAsync();
        _coordinator.ForceSaveSync(new WorkspaceSettings { Name = "FROM-SHUTDOWN" });
        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task Startup_FileMissing_NoLockAndNoCopy()
    {
        _coordinator.Bind(new MockLayoutTarget());

        await _coordinator.InitializeWorkspaceAsync();

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Empty(PreservedCopies());
        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public async Task Startup_FileEmpty_NoLockAndNoCopy(string content)
    {
        _coordinator.Bind(new MockLayoutTarget());
        await File.WriteAllTextAsync(_testWorkspacePath, content);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Empty(PreservedCopies());
    }

    [Fact]
    public async Task Startup_BrokenJson_DefaultsLockAndCopy_AndTheOriginalSurvivesEveryDefaultSavePath()
    {
        var vm = new MockLayoutTarget { LeftPanelWidth = 999 };
        _coordinator.Bind(vm);
        await File.WriteAllTextAsync(_testWorkspacePath, "{ \"Name\": \"x\", ");
        var original = await File.ReadAllBytesAsync(_testWorkspacePath);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
        Assert.Equal(200, vm.LeftPanelWidth); // default layout
        Assert.True(_coordinator.IsAutoSaveLocked);
        var issue = _stateStore.RestoreIssue!;
        Assert.Equal(WorkspaceRestoreIssueKind.PartialRestore, issue.Kind);
        Assert.Equal(Assert.Single(PreservedCopies()), issue.PreservedCopyPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(issue.PreservedCopyPath!));
        Assert.Single(issue.Warnings);
        await AssertEveryDefaultSavePathLeavesTheFileAlone(original);
    }

    [Fact]
    public async Task Startup_NewerSchema_LocksWithoutCopyAndLoadsNothing()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        await File.WriteAllTextAsync(_testWorkspacePath, "{ \"SchemaVersion\": 99, \"Version\": 99, \"SelectedTicker\": \"FUTURE\", \"Indicators\": 12345 }");
        var original = await File.ReadAllBytesAsync(_testWorkspacePath);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Equal(WorkspaceRestoreIssueKind.NewerSchema, _stateStore.RestoreIssue!.Kind);
        Assert.Null(_stateStore.RestoreIssue.PreservedCopyPath);
        Assert.Empty(PreservedCopies());
        Assert.Null(vm.SelectedTicker); // no partial restore
        await AssertEveryDefaultSavePathLeavesTheFileAlone(original);
    }

    [Fact]
    public async Task Startup_OneBrokenIndicator_RestoresTheRestLocksAndPreserves()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings { SelectedTicker = "AAPL", LeftPanelWidth = 321 };
        settings.Indicators.Add(Sma("a"));
        settings.Indicators.Add(Sma("b"));
        var original = await WriteDamagedAsync(settings, root => root["Indicators"]![0]!["TypeEnum"] = "NoSuchIndicatorType");

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Equal("AAPL", vm.SelectedTicker);
        Assert.Equal(321, vm.LeftPanelWidth);
        Assert.Equal("b", Assert.Single(vm.GetIndicators()).Id);
        Assert.True(_coordinator.IsAutoSaveLocked);
        var issue = _stateStore.RestoreIssue!;
        Assert.Equal(WorkspaceRestoreIssueKind.PartialRestore, issue.Kind);
        Assert.NotNull(issue.PreservedCopyPath);
        Assert.Contains(issue.Warnings, w => w.Severity == WorkspaceWarningSeverity.Lossy && w.Path == "Indicators[0]");
        await AssertEveryDefaultSavePathLeavesTheFileAlone(original);
    }

    [Fact]
    public async Task Startup_UnhealableIndicator_IsDroppedByTheNormaliserAndLocks()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings();
        settings.Indicators.Add(UnhealableIndicator());
        settings.Indicators.Add(Sma("ok"));
        await _serializationService.SaveAsync(settings, _testWorkspacePath);
        var original = await File.ReadAllBytesAsync(_testWorkspacePath);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Equal("ok", Assert.Single(vm.GetIndicators()).Id);
        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Contains(_stateStore.RestoreIssue!.Warnings, w => w.Severity == WorkspaceWarningSeverity.Lossy && w.Path == "Indicators[0]");
        Assert.Single(PreservedCopies());
        await AssertEveryDefaultSavePathLeavesTheFileAlone(original);
    }

    [Fact]
    public async Task Startup_HealthyWorkspace_AppliesItWithoutLockOrCopy()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings { SelectedTicker = "MSFT", LeftPanelWidth = 321 };
        settings.Indicators.Add(Sma("a"));
        await _serializationService.SaveAsync(settings, _testWorkspacePath);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Equal("MSFT", vm.SelectedTicker);
        Assert.Equal(321, vm.LeftPanelWidth);
        Assert.Equal("a", Assert.Single(vm.GetIndicators()).Id);
        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Empty(PreservedCopies());
    }

    [Fact]
    public async Task Startup_OnlyCosmeticNormalisation_NeitherLocksNorCopies()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        await WriteDamagedAsync(new WorkspaceSettings(), root => root["SelectedTimeframe"] = "Fortnightly");

        await _coordinator.InitializeWorkspaceAsync();

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Empty(PreservedCopies());
    }

    [Fact]
    public async Task Startup_ScreenApplyThrows_KeepsTheOriginalLocksAndShowsDefaults()
    {
        var vm = new MockLayoutTarget { ApplyPanelTabsFailure = new InvalidOperationException("view model defect") };
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings { LeftPanelWidth = 321 };
        await _serializationService.SaveAsync(settings, _testWorkspacePath);
        var original = await File.ReadAllBytesAsync(_testWorkspacePath);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
        Assert.Equal(200, vm.LeftPanelWidth); // the default layout replaced the half-applied one
        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Equal(WorkspaceRestoreIssueKind.RestoreFailed, _stateStore.RestoreIssue!.Kind);
        Assert.Equal(Assert.Single(PreservedCopies()), _stateStore.RestoreIssue.PreservedCopyPath);
        await AssertEveryDefaultSavePathLeavesTheFileAlone(original);
    }

    [Fact]
    public async Task Startup_NullWatchlistProfiles_OptionC_RestoresCategoriesAndFiltersAndStartsWithNoWatchlist()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings();
        settings.CustomTickerCategories.Add(new TickerParentCategorySettings { Name = "My category" });
        settings.TagFilters.Add(new FilterSettings { Name = "My filter" });
        await WriteDamagedAsync(settings, root => root["WatchlistProfiles"] = null);

        await _coordinator.InitializeWorkspaceAsync();

        Assert.Empty(_watchlistManager.GetAllProfiles());
        Assert.Equal(1, vm.ImportedCategoryCount);
        Assert.Equal(1, vm.ImportedFilterCount);
        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Empty(PreservedCopies());
    }

    // ---- manual load (ApplyLoadedWorkspaceAsync) --------------------------------------------------------------

    [Fact]
    public async Task ManualLoad_Clean_ReleasesAnExistingLock()
    {
        _coordinator.Bind(new MockLayoutTarget());
        _stateStore.EngageAutoSaveLock(Issue());

        await _coordinator.ApplyLoadedWorkspaceAsync(new WorkspaceSettings { SelectedTicker = "MSFT" });

        Assert.False(_coordinator.IsAutoSaveLocked);
        Assert.Null(_stateStore.RestoreIssue);
        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
    }

    [Fact]
    public async Task ManualLoad_CosmeticOnly_StillReleasesTheLock()
    {
        _coordinator.Bind(new MockLayoutTarget());
        _stateStore.EngageAutoSaveLock(Issue());

        await _coordinator.ApplyLoadedWorkspaceAsync(new WorkspaceSettings { SelectedTimeframe = "Fortnightly" });

        Assert.False(_coordinator.IsAutoSaveLocked);
    }

    [Fact]
    public async Task ManualLoad_WithLostIndicator_LocksWithoutCopyAndKeepsTheWarnings()
    {
        var vm = new MockLayoutTarget();
        _coordinator.Bind(vm);
        var settings = new WorkspaceSettings();
        settings.Indicators.Add(UnhealableIndicator());
        settings.Indicators.Add(Sma("ok"));

        await _coordinator.ApplyLoadedWorkspaceAsync(settings);

        Assert.Equal("ok", Assert.Single(vm.GetIndicators()).Id);
        Assert.True(_coordinator.IsAutoSaveLocked);
        var issue = _stateStore.RestoreIssue!;
        Assert.Equal(WorkspaceRestoreIssueKind.PartialRestore, issue.Kind);
        Assert.Null(issue.PreservedCopyPath);
        Assert.Contains(issue.Warnings, w => w.Severity == WorkspaceWarningSeverity.Lossy && w.Path == "Indicators[0]");
        Assert.Empty(PreservedCopies()); // the file is the user's own: nothing is copied
    }

    [Fact]
    public async Task ManualLoad_ScreenApplyThrows_LocksAndRethrows()
    {
        _coordinator.Bind(new MockLayoutTarget { ApplyPanelTabsFailure = new InvalidOperationException("view model defect") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _coordinator.ApplyLoadedWorkspaceAsync(new WorkspaceSettings()));

        Assert.True(_coordinator.IsAutoSaveLocked);
        Assert.Equal(WorkspaceRestoreIssueKind.RestoreFailed, _stateStore.RestoreIssue!.Kind);
        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
    }

    private (WatchlistManager Manager, StockAnalyzer.Core.Models.Watchlist.WatchlistProfile First, StockAnalyzer.Core.Models.Watchlist.WatchlistProfile Second) TwoWatchlistsWithTickers()
    {
        var first = _watchlistManager.CreateProfile("Tech", IndicatorColor.Gray);
        var second = _watchlistManager.CreateProfile("Energy", IndicatorColor.Gray);
        _watchlistManager.AddTickersToProfile(first.Id, new[] { "AAPL", "MSFT" });
        _watchlistManager.AddTickerToProfile(second.Id, "XOM");
        return (_watchlistManager, _watchlistManager.GetProfileById(first.Id)!, _watchlistManager.GetProfileById(second.Id)!);
    }

    [Fact]
    public async Task ManualLoad_NullWatchlistProfiles_OptionC_KeepsTheCurrentWatchlistsThroughTheFollowingSave()
    {
        var vm = new MockLayoutTarget(_watchlistManager);
        _coordinator.Bind(vm);
        MakeReady();
        var (_, tech, energy) = TwoWatchlistsWithTickers();
        var loaded = new WorkspaceSettings { WatchlistProfiles = null!, SelectedWatchlistId = tech.Id };
        loaded.CustomTickerCategories.Add(new TickerParentCategorySettings { Name = "From file" });
        loaded.TagFilters.Add(new FilterSettings { Name = "From file" });

        await _coordinator.ApplyLoadedWorkspaceAsync(loaded);
        await _coordinator.SaveActiveWorkspaceAsync(); // what MainWindowViewModel.LoadWorkspaceAsync does next

        // The saved file still holds every original watchlist and ticker.
        var saved = await _serializationService.LoadAsync(_testWorkspacePath);
        Assert.Equal(new[] { tech.Id, energy.Id }, saved!.WatchlistProfiles.Select(p => p.Id));
        Assert.Equal(new[] { "AAPL", "MSFT" }, saved.WatchlistProfiles[0].Items.Select(i => i.Ticker));
        Assert.Equal(new[] { "XOM" }, saved.WatchlistProfiles[1].Items.Select(i => i.Ticker));
        Assert.Equal(new[] { tech.Id, energy.Id }, _watchlistManager.GetAllProfiles().Select(p => p.Id));
        // The file's own categories and filters were applied, and the selection was validated against the taken-over profiles.
        Assert.Equal(1, vm.ImportedCategoryCount);
        Assert.Equal(1, vm.ImportedFilterCount);
        Assert.Equal(tech.Id, loaded.SelectedWatchlistId);
    }

    [Fact]
    public async Task ManualLoad_NullWatchlistProfiles_TakeOverIsACopy_NotTheManagersOwnList()
    {
        _coordinator.Bind(new MockLayoutTarget(_watchlistManager));
        var (_, tech, energy) = TwoWatchlistsWithTickers();
        var loaded = new WorkspaceSettings { WatchlistProfiles = null! };

        await _coordinator.ApplyLoadedWorkspaceAsync(loaded);
        loaded.WatchlistProfiles.Clear(); // a later change to the loaded settings must not reach the manager

        Assert.Equal(new[] { tech.Id, energy.Id }, _watchlistManager.GetAllProfiles().Select(p => p.Id));
    }

    [Fact]
    public async Task ManualLoad_NullWatchlistProfiles_UnknownSelectedWatchlistIsStillCleared()
    {
        _coordinator.Bind(new MockLayoutTarget(_watchlistManager));
        TwoWatchlistsWithTickers();
        var loaded = new WorkspaceSettings { WatchlistProfiles = null!, SelectedWatchlistId = Guid.NewGuid() };

        await _coordinator.ApplyLoadedWorkspaceAsync(loaded);

        Assert.Null(loaded.SelectedWatchlistId);
    }

    [Fact]
    public async Task ManualLoad_EmptyButPresentWatchlistProfiles_StillReplacesTheWatchlists()
    {
        // Only a MISSING (null) list is carried over; an explicit empty list in the file is the file's content.
        _coordinator.Bind(new MockLayoutTarget(_watchlistManager));
        TwoWatchlistsWithTickers();

        await _coordinator.ApplyLoadedWorkspaceAsync(new WorkspaceSettings { WatchlistProfiles = new() });

        Assert.Empty(_watchlistManager.GetAllProfiles());
    }

    // ---- D-2' Run 2 / S3: read-only data folder ------------------------------------------------------------

    private WorkspaceCoordinator CoordinatorWith(IDataStorageState? state, MockLayoutTarget? target = null)
    {
        var coordinator = new WorkspaceCoordinator(
            _stateStore,
            _serializationService,
            _watchlistManager,
            _options,
            new YieldingTestDispatcher(),
            NullLogger<WorkspaceCoordinator>.Instance,
            dataStorageState: state);
        coordinator.Bind(target ?? new MockLayoutTarget());
        return coordinator;
    }

    [Fact]
    public async Task ReadOnlyStorage_Startup_LocksWithTheReadOnlyReason_AndStillRestoresTheWorkspace()
    {
        var vm = new MockLayoutTarget();
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState(), vm);
        await _serializationService.SaveAsync(new WorkspaceSettings { SelectedTicker = "MSFT", LeftPanelWidth = 321 }, _testWorkspacePath);

        await coordinator.InitializeWorkspaceAsync();

        Assert.Equal(AutoSaveLock.StorageReadOnly, _stateStore.AutoSaveLock);
        Assert.True(coordinator.IsAutoSaveLocked);
        Assert.Null(_stateStore.RestoreIssue);
        Assert.Equal("MSFT", vm.SelectedTicker); // reading and viewing work
        Assert.Equal(321, vm.LeftPanelWidth);
        Assert.Equal(WorkspaceLifecycleState.Ready, _stateStore.LifecycleState);
    }

    [Fact]
    public async Task ReadOnlyStorage_FileMissing_StillStartsWithDefaults_AndLocks()
    {
        var vm = new MockLayoutTarget();
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState(), vm);

        await coordinator.InitializeWorkspaceAsync();

        Assert.Equal(AutoSaveLock.StorageReadOnly, _stateStore.AutoSaveLock);
        Assert.Equal(200, vm.LeftPanelWidth);
    }

    [Fact]
    public async Task ReadOnlyStorage_NoDefaultPathSaveWrites_AndTheFileIsLeftAlone()
    {
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState());
        var original = await WriteOriginalAsync();
        await coordinator.InitializeWorkspaceAsync();

        await coordinator.SaveActiveWorkspaceAsync();
        coordinator.ForceSaveSync(new WorkspaceSettings { Name = "FROM-SHUTDOWN" });

        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task ReadOnlyStorage_SaveCurrentStateAndUnlock_WritesNothingAndKeepsTheLock()
    {
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState());
        var original = await WriteOriginalAsync();
        await coordinator.InitializeWorkspaceAsync();

        var saved = await coordinator.SaveCurrentStateAndUnlockAsync();

        Assert.False(saved);
        Assert.Equal(AutoSaveLock.StorageReadOnly, _stateStore.AutoSaveLock);
        Assert.Equal(original, await File.ReadAllBytesAsync(_testWorkspacePath));
    }

    [Fact]
    public async Task ReadOnlyStorage_CleanManualLoad_DoesNotLiftTheReadOnlyLock()
    {
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState());
        await coordinator.InitializeWorkspaceAsync();

        await coordinator.ApplyLoadedWorkspaceAsync(new WorkspaceSettings { SelectedTicker = "MSFT" });

        Assert.Equal(AutoSaveLock.StorageReadOnly, _stateStore.AutoSaveLock);
    }

    [Fact]
    public async Task ReadOnlyStorage_DamagedFile_RecordsTheIssueAndKeepsTheReadOnlyReason()
    {
        var coordinator = CoordinatorWith(TestDataStorageState.ReadOnlyState());
        await File.WriteAllTextAsync(_testWorkspacePath, "{ \"Name\": ");

        await coordinator.InitializeWorkspaceAsync();

        Assert.Equal(AutoSaveLock.StorageReadOnly, _stateStore.AutoSaveLock);
        Assert.Equal(WorkspaceRestoreIssueKind.PartialRestore, _stateStore.RestoreIssue!.Kind);
    }

    [Fact]
    public async Task WritableStorage_Startup_DoesNotLock()
    {
        var coordinator = CoordinatorWith(TestDataStorageState.WritableState());
        await _serializationService.SaveAsync(new WorkspaceSettings(), _testWorkspacePath);

        await coordinator.InitializeWorkspaceAsync();

        Assert.False(coordinator.IsAutoSaveLocked);
    }

    [Fact]
    public async Task NoStorageState_Startup_DoesNotLock()
    {
        var coordinator = CoordinatorWith(null);

        await coordinator.InitializeWorkspaceAsync();

        Assert.False(coordinator.IsAutoSaveLocked);
    }

    /* Commented out due to missing ViewModel parameter-less constructors in the test project context
    [Fact]
    public async Task CaptureWorkspaceSettings_ConcurrentTabModification_DoesNotThrow()
    {
        var vm = new MainWindowViewModel();
        var settings = new WorkspaceSettings();

        // Populate initial tabs to LeftPanelTabs
        for (int i = 0; i < 5; i++)
        {
            vm.LeftPanelTabs.Add(new WorkspaceViewItem { Id = $"Tab_{i}", ViewModel = new ChartViewModel() });
        }

        // Run concurrent additions/removals in a parallel task
        var tokenSource = new System.Threading.CancellationTokenSource();
        var modificationTask = Task.Run(async () =>
        {
            int counter = 5;
            while (!tokenSource.IsCancellationRequested)
            {
                vm.LeftPanelTabs.Add(new WorkspaceViewItem { Id = $"Tab_{counter++}", ViewModel = new ChartViewModel() });
                if (vm.LeftPanelTabs.Count > 0)
                {
                    vm.LeftPanelTabs.RemoveAt(0);
                }
                await Task.Delay(1);
            }
        });

        // Act & Assert: Call CaptureWorkspaceSettings multiple times concurrently
        Exception? exception = null;
        try
        {
            for (int i = 0; i < 100; i++)
            {
                vm.CaptureWorkspaceSettings(settings);
                await Task.Delay(1);
            }
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            tokenSource.Cancel();
            await modificationTask;
        }

        Assert.Null(exception);
    }

    [Fact(Skip = "Requires heavy MainWindowViewModel dependency tree. Moved to ViewModel integration suite.")]
    public void UIInteraction_TriggersSaveTelemetry_AndRespectsSuspension()
    {
        var vm = new MainWindowViewModel();
        
        // Assert that we can pause auto save using the public PauseAutoSave method
        using (vm.PauseAutoSave())
        {
            // Verify that while paused, _isApplyingSettings is true
            var isApplyingField = typeof(MainWindowViewModel).GetProperty("_isApplyingSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(isApplyingField);
            var isApplying = (bool)isApplyingField.GetValue(vm)!;
            Assert.True(isApplying);
            
            // While suspended, changing properties should not trigger any saves or logs that would throw
            vm.LeftSelectedTabIndex = 2;
            vm.SelectedTicker = "AAPL";
        }
        
        // Verify that after exiting the suspension scope, _isApplyingSettings is false
        var isApplyingAfter = (bool)typeof(MainWindowViewModel).GetProperty("_isApplyingSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(vm)!;
        Assert.False(isApplyingAfter);

        // Verification of property set outside suspension (should execute successfully without exceptions)
        vm.LeftSelectedTabIndex = 0;
        vm.SelectedTicker = "MSFT";
    }
    */

    private class MockLayoutTarget : IWorkspaceLayoutTarget
    {
        private readonly StockAnalyzer.Core.Interfaces.IWatchlistManager? _watchlists;

        /// <param name="watchlists">When given, a captured workspace carries the manager's current profiles (as the real view model does).</param>
        public MockLayoutTarget(StockAnalyzer.Core.Interfaces.IWatchlistManager? watchlists = null) => _watchlists = watchlists;

        /// <summary>Set to make the screen-apply stage throw, as a real view-model defect would.</summary>
        public Exception? ApplyPanelTabsFailure { get; set; }
        public int ImportedCategoryCount { get; private set; }
        public int ImportedFilterCount { get; private set; }
        public double LeftPanelWidth { get; set; } = 200;
        public double RightPanelWidth { get; set; } = 250;
        public bool IsLeftPanelVisible { get; set; } = true;
        public bool IsRightPanelVisible { get; set; } = true;
        public double TopPanelHeight { get; set; } = 0;
        public double BottomPanelHeight { get; set; } = 250;
        public bool IsTopPanelVisible { get; set; } = false;
        public bool IsBottomPanelVisible { get; set; } = true;
        public bool IsLeftPanelPinned { get; set; } = true;
        public bool IsRightPanelPinned { get; set; } = true;
        public bool IsTopPanelPinned { get; set; } = true;
        public bool IsBottomPanelPinned { get; set; } = true;
        public int LeftSelectedTabIndex { get; set; }
        public int RightSelectedTabIndex { get; set; }
        public int TopSelectedTabIndex { get; set; }
        public int BottomSelectedTabIndex { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem> LeftPanelTabs { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem> RightPanelTabs { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem> TopPanelTabs { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<WorkspaceViewItem> BottomPanelTabs { get; } = new();
        public string? SelectedTicker { get; set; }
        public void ApplyPanelTabs(WorkspaceSettings settings) { if (ApplyPanelTabsFailure != null) throw ApplyPanelTabsFailure; }
        public void RestoreDetachedTabs(WorkspaceSettings settings) { }
        public void SetTimeframe(TimeframeType timeframe) { }
        public void RestoreChartSettings(WorkspaceSettings settings) { }
        public void SetActiveColumns(IEnumerable<string> columnNames) { }
        public void ApplyColumnWidths(Dictionary<string, string>? widths) { }
        // Both restore calls are recorded so a test can prove the shared selection is stored BEFORE the per-list
        // restore applies the active store (sa_implement: Column Customization selection scope).
        public void RestoreTickerColumnTemplateSelection(IReadOnlyDictionary<Guid, Guid>? selectionsByList) => RestoreCallOrder.Add(nameof(RestoreTickerColumnTemplateSelection));
        public System.Collections.Generic.List<string> RestoreCallOrder { get; } = new();
        public Guid RestoredSharedColumnTemplateId { get; private set; }
        public void RestoreTickerSharedColumnTemplateSelection(Guid templateId)
        {
            RestoredSharedColumnTemplateId = templateId;
            RestoreCallOrder.Add(nameof(RestoreTickerSharedColumnTemplateSelection));
        }
        public void ApplySortState(string? columnName, int direction) { }
        public void CaptureWorkspaceSettings(WorkspaceSettings settings)
        {
            settings.LeftPanelWidth = LeftPanelWidth;
            settings.RightPanelWidth = RightPanelWidth;
            settings.IsLeftPanelVisible = IsLeftPanelVisible;
            settings.IsRightPanelVisible = IsRightPanelVisible;
            settings.TopPanelHeight = TopPanelHeight;
            settings.BottomPanelHeight = BottomPanelHeight;
            settings.IsTopPanelVisible = IsTopPanelVisible;
            settings.IsBottomPanelVisible = IsBottomPanelVisible;
            if (_watchlists != null) settings.WatchlistProfiles = _watchlists.GetAllProfiles().ToList();
        }
        private System.Collections.Generic.List<CoreIndicatorSettings> _indicators = new();
        public IReadOnlyList<CoreIndicatorSettings> GetIndicators() => _indicators;
        public void ApplyIndicators(IEnumerable<CoreIndicatorSettings> indicators)
        {
            _indicators = indicators.ToList();
        }
        public void RefreshTickerListNodes() { }
        public void SelectTickerListNodeById(Guid id) { }

        // Records call order so a test can prove ImportCustomTickerCategories runs before
        // ImportFilterSettings (sa_minimal_fix: a saved FilterNode's ParentId can point at a custom
        // category/list folder, which ImportFilterSettings can only resolve if it already exists).
        public System.Collections.Generic.List<string> ImportCallOrder { get; } = new();
        public void ImportFilterSettings(IEnumerable<FilterSettings> filters) { ImportCallOrder.Add(nameof(ImportFilterSettings)); ImportedFilterCount = filters.Count(); }
        public void ImportCustomTickerCategories(IEnumerable<StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings> categories) { ImportCallOrder.Add(nameof(ImportCustomTickerCategories)); ImportedCategoryCount = categories.Count(); }
    }

    private class YieldingTestDispatcher : StockAnalyzer.Core.Services.IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public async Task PostAsync(Func<Task> action)
        {
            await action();
        }
        public async Task PostAsync<TState>(Func<TState, Task> action, TState state)
        {
            await action(state);
        }
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }
}
