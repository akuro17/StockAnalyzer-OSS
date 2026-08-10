using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Services;

public sealed class WorkspaceCoordinator : IWorkspaceCoordinator
{
    private readonly LayoutStateStore _stateStore;
    private readonly IWorkspaceSerializationService _serializationService;
    private readonly IWatchlistManager _watchlistManager;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WorkspaceCoordinator> _logger;
    private readonly string _defaultWorkspacePath;
    
    private IWorkspaceLayoutTarget? _viewModel;
    private bool _isBound;
 
    public WorkspaceCoordinator(
        LayoutStateStore stateStore,
        IWorkspaceSerializationService serializationService,
        IWatchlistManager watchlistManager,
        IOptions<WorkspaceCoordinatorOptions> options,
        IDispatcherService dispatcherService,
        ILogger<WorkspaceCoordinator> logger)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
        _watchlistManager = watchlistManager ?? throw new ArgumentNullException(nameof(watchlistManager));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        var opt = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(opt.WorkspacePath))
            throw new ArgumentException("WorkspacePath must not be null or whitespace.", nameof(options));
            
        _defaultWorkspacePath = opt.WorkspacePath;
    }

    public void Bind(IWorkspaceLayoutTarget viewModel)
    {
        if (_isBound)
            throw new InvalidOperationException("WorkspaceCoordinator has already been bound to a target. Re-binding is prohibited.");
            
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _isBound = true;
    }

    public async Task InitializeWorkspaceAsync(string? customPath = null)
    {
        if (!_isBound || _viewModel == null)
            throw new InvalidOperationException("WorkspaceCoordinator must be bound to IWorkspaceLayoutTarget before initialization.");

        string targetPath = customPath ?? _defaultWorkspacePath;
        _logger.LogInformation("Starting workspace restoration pipeline. Target Path: {Path}", targetPath);
        _stateStore.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!File.Exists(targetPath))
            {
                _logger.LogWarning("Workspace settings file not found. Applying default layout.");
                await _dispatcherService.PostAsync(static coord => { coord.RestoreLayoutSettings(null); return Task.CompletedTask; }, this);
                return;
            }

            var settings = await _serializationService.LoadAsync(targetPath).ConfigureAwait(false);
            if (settings == null)
            {
                _logger.LogWarning("Workspace settings was empty or returned null. Applying default layout.");
                await _dispatcherService.PostAsync(static coord => { coord.RestoreLayoutSettings(null); return Task.CompletedTask; }, this);
                return;
            }

            // Dispatch to UI thread to apply settings deterministically
            await _dispatcherService.PostAsync(static s =>
            {
                s.Coord.RestoreAll(s.Settings);
                return Task.CompletedTask;
            }, (Coord: this, Settings: settings));

            stopwatch.Stop();
            _logger.LogInformation("Workspace successfully restored and applied in {ElapsedMs} ms. Target Path: {Path}", stopwatch.ElapsedMilliseconds, targetPath);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Workspace restoration pipeline failed in {ElapsedMs} ms. Falling back to default layout.", stopwatch.ElapsedMilliseconds);
            
            // Safe fallback guarantee
            await _dispatcherService.PostAsync(static coord => { coord.RestoreLayoutSettings(null); return Task.CompletedTask; }, this);
        }
        finally
        {
            _stateStore.LifecycleState = WorkspaceLifecycleState.Ready;
        }
    }

    public async Task SaveActiveWorkspaceAsync(string? customPath = null)
    {
        if (!_isBound || _viewModel == null)
            throw new InvalidOperationException("WorkspaceCoordinator must be bound to IWorkspaceLayoutTarget before saving.");

        if (_stateStore.LifecycleState != WorkspaceLifecycleState.Ready)
        {
            _logger.LogWarning("Workspace save requested but LifecycleState is not Ready. Saving is suspended.");
            return;
        }

        string targetPath = customPath ?? _defaultWorkspacePath;
        _logger.LogInformation("Capturing active workspace settings. Target Path: {Path}", targetPath);
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WorkspaceSettings settings = new WorkspaceSettings();
        List<CoreIndicatorSettings> indicatorSnapshot = new();

        // Copy snapshot safely on the UI thread to avoid collection concurrent modification
        await _dispatcherService.PostAsync(static s =>
        {
            s.Target.CaptureWorkspaceSettings(s.Settings);

            // Null-safe indicators copy
            var indicators = s.Target.GetIndicators();
            if (indicators != null)
            {
                foreach (var indicator in indicators)
                {
                    var cloned = indicator.Clone();
                    if (cloned != null)
                    {
                        s.Snapshot.Add(cloned);
                    }
                }
            }
            s.Settings.Indicators = s.Snapshot;
            return Task.CompletedTask;
        }, (Target: _viewModel!, Settings: settings, Snapshot: indicatorSnapshot));

        try
        {
            // Reuse existing serialization service for atomic file replacement
            await _serializationService.SaveAsync(settings, targetPath).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation("Workspace atomically saved to disk in {ElapsedMs} ms. Target Path: {Path}", stopwatch.ElapsedMilliseconds, targetPath);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to execute atomic workspace save operation in {ElapsedMs} ms.", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public void ForceSaveSync(WorkspaceSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        try
        {
            _serializationService.Save(settings, _defaultWorkspacePath);
        }
        catch (Exception ex) when (ex is IOException || 
                                   ex is UnauthorizedAccessException || 
                                   ex is System.Text.Json.JsonException || 
                                   ex is System.Security.SecurityException)
        {
            _logger.LogError(ex, "Failed to save workspace settings atomically on shutdown. DefaultPath={DefaultPath}", _defaultWorkspacePath);
        }
    }

    private void RestoreLayoutSettings(WorkspaceSettings? settings)
    {
        if (_viewModel == null) return;

        if (settings == null)
        {
            // Centralized defaults safely clamped
            _viewModel.IsLeftPanelVisible = true;
            _viewModel.LeftPanelWidth = LayoutConstants.DefaultLeftWidth;
            _viewModel.IsLeftPanelPinned = true;
            _viewModel.IsRightPanelVisible = true;
            _viewModel.RightPanelWidth = LayoutConstants.DefaultRightWidth;
            _viewModel.IsRightPanelPinned = true;
            _viewModel.IsTopPanelVisible = false;
            _viewModel.TopPanelHeight = LayoutConstants.DefaultTopHeight;
            _viewModel.IsTopPanelPinned = true;
            _viewModel.IsBottomPanelVisible = true;
            _viewModel.BottomPanelHeight = LayoutConstants.DefaultBottomHeight;
            _viewModel.IsBottomPanelPinned = true;

            _viewModel.LeftSelectedTabIndex = 0;
            _viewModel.RightSelectedTabIndex = 0;
            _viewModel.TopSelectedTabIndex = 0;
            _viewModel.BottomSelectedTabIndex = 0;


            return;
        }

        _viewModel.RestoreChartSettings(settings);

        // Centralized boundaries clamping to prevent layout engine freezes
        _viewModel.IsLeftPanelVisible = settings.IsLeftPanelVisible;
        _viewModel.LeftPanelWidth = Math.Clamp(settings.LeftPanelWidth, LayoutConstants.MinPanelWidthClamp, LayoutConstants.MaxPanelWidthClamp);
        _viewModel.IsLeftPanelPinned = settings.IsLeftPanelPinned;

        _viewModel.IsRightPanelVisible = settings.IsRightPanelVisible;
        _viewModel.RightPanelWidth = Math.Clamp(settings.RightPanelWidth, LayoutConstants.MinPanelWidthClamp, LayoutConstants.MaxPanelWidthClamp);
        _viewModel.IsRightPanelPinned = settings.IsRightPanelPinned;

        _viewModel.IsTopPanelVisible = settings.IsTopPanelVisible;
        _viewModel.TopPanelHeight = Math.Clamp(settings.TopPanelHeight, LayoutConstants.MinPanelHeightClamp, LayoutConstants.MaxPanelHeightClamp);
        _viewModel.IsTopPanelPinned = settings.IsTopPanelPinned;

        _viewModel.IsBottomPanelVisible = settings.IsBottomPanelVisible;
        _viewModel.BottomPanelHeight = Math.Clamp(settings.BottomPanelHeight, LayoutConstants.MinPanelHeightClamp, LayoutConstants.MaxPanelHeightClamp);
        _viewModel.IsBottomPanelPinned = settings.IsBottomPanelPinned;

        // Clamping selected tab indices
        var indices = settings.PanelSelectedTabIndices;
        _viewModel.LeftSelectedTabIndex = ClampTabIndex(indices, "Left", _viewModel.LeftPanelTabs.Count);
        _viewModel.RightSelectedTabIndex = ClampTabIndex(indices, "Right", _viewModel.RightPanelTabs.Count);
        _viewModel.TopSelectedTabIndex = ClampTabIndex(indices, "Top", _viewModel.TopPanelTabs.Count);
        _viewModel.BottomSelectedTabIndex = ClampTabIndex(indices, "Bottom", _viewModel.BottomPanelTabs.Count);

        // Restore indicators
        if (settings.Indicators != null)
        {
            foreach (var indicator in settings.Indicators)
            {
                DefaultCoreIndicatorSettings.AutoHeal(indicator);
            }
            _viewModel.ApplyIndicators(settings.Indicators);
        }
        else
        {
            _viewModel.ApplyIndicators(Array.Empty<CoreIndicatorSettings>());
        }
    }

    private static int ClampTabIndex(Dictionary<string, int>? indices, string key, int tabCount)
    {
        if (tabCount <= 0) return 0;
        if (indices == null || !indices.TryGetValue(key, out int index)) return 0;
        return (index < 0 || index >= tabCount) ? 0 : index;
    }

    /// <summary>
    /// Runs the full restoration sequence in the order required by <see cref="RestoreSelectedTicker"/>
    /// (must precede tab collection restoration). Shared by both restoration entry points
    /// (<see cref="InitializeWorkspaceAsync"/>, <see cref="ApplyLoadedWorkspaceAsync"/>) so the
    /// sequence cannot drift between them.
    /// </summary>
    private void RestoreAll(WorkspaceSettings settings)
    {
        RestoreLayoutSettings(settings);
        RestoreSelectedTicker(settings);
        RestoreTabCollections(settings);
        RestoreDetachedWindows(settings);
        RestoreWatchlistAndColumns(settings);
    }

    private void RestoreSelectedTicker(WorkspaceSettings settings)
    {
        if (_viewModel == null) return;

        // Must run before RestoreTabCollections: Sync-enabled Chart tabs query
        // MainWindowViewModel.SelectedTicker (via CurrentTickerRequestMessage) synchronously
        // during their own restoration. If SelectedTicker has not been restored yet, they
        // fall back to the hardcoded default symbol instead of the persisted ticker.
        if (!string.IsNullOrEmpty(settings.SelectedTicker))
        {
            _viewModel.SelectedTicker = settings.SelectedTicker;
        }
    }

    private void RestoreTabCollections(WorkspaceSettings settings)
    {
        if (_viewModel == null) return;
        _logger.LogDebug("Restoring tab collections from workspace settings...");
        _viewModel.ApplyPanelTabs(settings);
    }

    private void RestoreDetachedWindows(WorkspaceSettings settings)
    {
        if (_viewModel == null) return;
        _logger.LogDebug("Restoring detached windows from workspace settings...");
        _viewModel.RestoreDetachedTabs(settings);
    }

    private void RestoreWatchlistAndColumns(WorkspaceSettings settings)
    {
        if (_viewModel == null) return;
        _logger.LogDebug("Restoring active watchlist selection and column sorts...");

        // SelectedTicker is restored earlier by RestoreSelectedTicker (must run before
        // RestoreTabCollections); see that method for the reason.

        // Initialize Watchlist Manager using injected interface dependency
        if (settings.WatchlistProfiles != null)
        {
            _watchlistManager.Initialize(settings.WatchlistProfiles);
            _viewModel.RefreshTickerListNodes();

            if (settings.TagFilters != null)
            {
                _viewModel.ImportFilterSettings(settings.TagFilters);
            }
        }

        if (settings.SelectedWatchlistId.HasValue)
        {
            var profile = _watchlistManager.GetAllProfiles().FirstOrDefault(p => p.Id == settings.SelectedWatchlistId.Value);
            if (profile != null)
            {
                _viewModel.SelectTickerListNodeById(profile.Id);
            }
        }

        if (!string.IsNullOrEmpty(settings.SelectedTimeframe) && 
            Enum.TryParse<TimeframeType>(settings.SelectedTimeframe, true, out var timeframe))
        {
            _viewModel.SetTimeframe(timeframe); // Directly call timeframe-typed overload to keep type safety
        }

        // Restore TickerList column visibility
        if (settings.TickerListVisibleColumns != null && settings.TickerListVisibleColumns.Count > 0)
        {
            _logger.LogDebug("Restoring {Count} visible columns from workspace settings.", settings.TickerListVisibleColumns.Count);
            _viewModel.SetActiveColumns(settings.TickerListVisibleColumns);
        }

        // Restore TickerList column widths
        if (settings.TickerListColumnWidths != null && settings.TickerListColumnWidths.Count > 0)
        {
            _logger.LogDebug("Restoring {Count} custom column widths from workspace settings.", settings.TickerListColumnWidths.Count);
            _viewModel.ApplyColumnWidths(settings.TickerListColumnWidths);
        }

        // Restore TickerList sort state
        if (!string.IsNullOrEmpty(settings.TickerListSortColumn))
        {
            _logger.LogDebug("Restoring sort state: {Column} direction={Direction}.", settings.TickerListSortColumn, settings.TickerListSortDirection);
            _viewModel.ApplySortState(settings.TickerListSortColumn, settings.TickerListSortDirection);
        }
    }

    public async Task ApplyLoadedWorkspaceAsync(WorkspaceSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (!_isBound || _viewModel == null)
            throw new InvalidOperationException("WorkspaceCoordinator must be bound to IWorkspaceLayoutTarget before applying layout.");

        _logger.LogInformation("Applying pre-loaded workspace settings (SchemaVersion={Version})", settings.SchemaVersion);
        _stateStore.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;

        try
        {
            await _dispatcherService.PostAsync(static s =>
            {
                s.Coord.RestoreAll(s.Settings);
                return Task.CompletedTask;
            }, (Coord: this, Settings: settings));
        }
        finally
        {
            _stateStore.LifecycleState = WorkspaceLifecycleState.Ready;
        }
    }

    public async Task<WorkspaceSettings> CaptureCurrentWorkspaceAsync()
    {
        if (!_isBound || _viewModel == null)
            throw new InvalidOperationException("WorkspaceCoordinator must be bound to IWorkspaceLayoutTarget before capturing layout.");

        WorkspaceSettings settings = new WorkspaceSettings();
        List<CoreIndicatorSettings> indicatorSnapshot = new();

        await _dispatcherService.PostAsync(static s =>
        {
            s.Target.CaptureWorkspaceSettings(s.Settings);

            var indicators = s.Target.GetIndicators();
            if (indicators != null)
            {
                foreach (var indicator in indicators)
                {
                    var cloned = indicator.Clone();
                    if (cloned != null)
                    {
                        s.Snapshot.Add(cloned);
                    }
                }
            }
            s.Settings.Indicators = s.Snapshot;
            return Task.CompletedTask;
        }, (Target: _viewModel!, Settings: settings, Snapshot: indicatorSnapshot));

        return settings;
    }
}
