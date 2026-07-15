using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services
{
    public sealed class DetachedTabManager : IDetachedTabManager, IRecipient<RegisterDetachedTabMessage>, IRecipient<ChartSymbolChangedMessage>
    {
        private readonly IMessenger _messenger;
        private readonly IWindowManagementService _windowManagementService;
        private readonly Func<ChartViewModel> _chartViewModelFactory;
        private readonly IDispatcherService _dispatcherService;
        private readonly ILogger<DetachedTabManager> _logger;
        private readonly List<WorkspaceViewItem> _detachedTabs = new();

        public DetachedTabManager(
            IMessenger messenger,
            IWindowManagementService windowManagementService,
            Func<ChartViewModel> chartViewModelFactory,
            IDispatcherService dispatcherService,
            ILogger<DetachedTabManager>? logger = null)
        {
            _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
            _windowManagementService = windowManagementService ?? throw new ArgumentNullException(nameof(windowManagementService));
            _chartViewModelFactory = chartViewModelFactory ?? throw new ArgumentNullException(nameof(chartViewModelFactory));
            _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
            _logger = logger ?? NullLogger<DetachedTabManager>.Instance;

            // Register for messages cleanly
            _messenger.RegisterAll(this);
        }

        public IReadOnlyList<WorkspaceViewItem> DetachedTabs
        {
            get
            {
                _dispatcherService.VerifyAccess();
                return _detachedTabs;
            }
        }

        public bool RegisterActiveDetachedTab(WorkspaceViewItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            _dispatcherService.VerifyAccess();

            // ReferenceEquals and GUID/Id uniqueness guard to ensure idempotency
            foreach (var existing in _detachedTabs)
            {
                if (ReferenceEquals(existing, item) || existing.Id == item.Id)
                {
                    _logger.LogDebug("[TabTearOff] Registration ignored (already registered): Tab={TabId}", item.Id);
                    return false;
                }
            }

            _detachedTabs.Add(item);
            _logger.LogInformation("[TabTearOff] Registered new independent tab: Tab={TabId}", item.Id);
            return true;
        }

        public bool RemoveActiveDetachedTab(WorkspaceViewItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            _dispatcherService.VerifyAccess();

            WorkspaceViewItem? target = null;
            foreach (var existing in _detachedTabs)
            {
                if (ReferenceEquals(existing, item) || existing.Id == item.Id)
                {
                    target = existing;
                    break;
                }
            }

            if (target != null)
            {
                _detachedTabs.Remove(target);
                _logger.LogInformation("[TabTearOff] Removed active detached tab: Tab={TabId}", item.Id);
                return true;
            }

            _logger.LogDebug("[TabTearOff] Removal ignored (tab not found): Tab={TabId}", item.Id);
            return false;
        }

        public void Restore(WorkspaceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _dispatcherService.VerifyAccess();

            if (settings.DetachedTabs == null || settings.DetachedTabs.Count == 0)
            {
                _logger.LogDebug("[TabTearOff] Restore skipped: settings.DetachedTabs is null or empty.");
                return;
            }

            _detachedTabs.Clear();
            var itemsToRestore = new List<(WorkspaceViewItem Item, DetachedTabInfo Info)>(settings.DetachedTabs.Count);

            foreach (var det in settings.DetachedTabs)
            {
                if (det == null) continue;

                try
                {
                    WorkspaceViewItem? item = null;
                    if (det.IsIndependent)
                    {
                        // Restore Independent Window (Factory Spawned)
                        var vm = _chartViewModelFactory();
                        if (vm == null)
                        {
                            _logger.LogWarning("[TabTearOff] Failed to resolve ChartViewModel from factory for tab {TabId}", det.TabId);
                            continue;
                        }

                        vm.IsSyncEnabled = det.IsSyncEnabled;
                        vm.Symbol = det.Symbol ?? StockAnalyzer.Core.ChartConstants.DefaultSymbol;
                        if (!string.IsNullOrEmpty(det.Timeframe) && Enum.TryParse<StockAnalyzer.Core.Models.TimeframeType>(det.Timeframe, out var tf)) 
                            vm.SelectedTimeFrame = tf;
                        if (det.MaxCandleCount > 0) 
                            vm.MaxCandleCount = det.MaxCandleCount;

                        // Restore indicators EARLY to ensure layout calculates correctly
                        vm.Indicators.Clear();
                        if (det.Indicators != null)
                        {
                            foreach (var indicator in det.Indicators)
                            {
                                if (indicator == null) continue;
                                StockAnalyzer.Core.Models.DefaultCoreIndicatorSettings.AutoHeal(indicator);
                                vm.Indicators.Add(indicator);
                            }
                        }

                        if (!string.IsNullOrEmpty(det.ChartType) && Enum.TryParse<ChartType>(det.ChartType, out var ct))
                            vm.ChartType = ct;
                        vm.IsMainWindowVisible = det.IsMainWindowVisible;
                        vm.IsSubWindowVisible = det.IsSubWindowVisible;

                        _ = vm.LoadDataCommand.ExecuteAsync(null);

                        item = new WorkspaceViewItem
                        {
                            Id = det.TabId,
                            Title = vm.Symbol,
                            ViewModel = vm,
                            IsIndependent = true,
                            CanClose = true
                        };
                    }
                    else
                    {
                        // Restore Torn-off Panel Tab
                        item = _windowManagementService.TabFactory.CreateTab(det.TabId);

                        if (item != null && item.ViewModel is ChartViewModel cvm)
                        {
                            cvm.IsSyncEnabled = det.IsSyncEnabled;
                            if (!cvm.IsSyncEnabled)
                            {
                                cvm.Symbol = det.Symbol ?? StockAnalyzer.Core.ChartConstants.DefaultSymbol;
                                if (!string.IsNullOrEmpty(det.Timeframe) && Enum.TryParse<StockAnalyzer.Core.Models.TimeframeType>(det.Timeframe, out var tf)) 
                                    cvm.SelectedTimeFrame = tf;
                                if (det.MaxCandleCount > 0) 
                                    cvm.MaxCandleCount = det.MaxCandleCount;

                                if (!string.IsNullOrEmpty(det.ChartType) && Enum.TryParse<ChartType>(det.ChartType, out var ct))
                                    cvm.ChartType = ct;
                                cvm.IsMainWindowVisible = det.IsMainWindowVisible;
                                cvm.IsSubWindowVisible = det.IsSubWindowVisible;

                                _ = cvm.LoadDataCommand.ExecuteAsync(null);
                            }

                            // Restore indicators regardless of sync state (Sync only affects Symbol/Timeframe)
                            cvm.Indicators.Clear();
                            if (det.Indicators != null)
                            {
                                foreach (var indicator in det.Indicators)
                                {
                                    if (indicator == null) continue;
                                    StockAnalyzer.Core.Models.DefaultCoreIndicatorSettings.AutoHeal(indicator);
                                    cvm.Indicators.Add(indicator);
                                }
                            }
                        }
                    }

                    if (item != null)
                    {
                        item.IsDetached = true;
                        item.OriginalPanelName = det.OriginalPanelName;

                        // Extreme coordinates fallback: double.NaN / Infinity / Width or Height < 100
                        double safeX = det.X;
                        double safeY = det.Y;
                        double safeW = det.Width;
                        double safeH = det.Height;

                        if (double.IsNaN(safeX) || double.IsInfinity(safeX) ||
                            double.IsNaN(safeY) || double.IsInfinity(safeY) ||
                            double.IsNaN(safeW) || double.IsInfinity(safeW) || safeW < LayoutConstants.MinDetachedWindowWidth ||
                            double.IsNaN(safeH) || double.IsInfinity(safeH) || safeH < LayoutConstants.MinDetachedWindowHeight)
                        {
                            _logger.LogWarning("[TabTearOff] Invalid geometry coords caught for tab {TabId} (X={X}, Y={Y}, W={W}, H={H}). Falling back to defaults.", det.TabId, safeX, safeY, safeW, safeH);
                            safeX = LayoutConstants.DefaultDetachedWindowX;
                            safeY = LayoutConstants.DefaultDetachedWindowY;
                            safeW = LayoutConstants.DefaultDetachedWindowWidth;
                            safeH = LayoutConstants.DefaultDetachedWindowHeight;
                        }

                        // BOUNDARY CHECK: Ensure window isn't launched off-screen
                        var (boundedX, boundedY, boundedW, boundedH) = _windowManagementService.BoundaryService.EnsureVisible(safeX, safeY, safeW, safeH);
                        item.DetachedX = boundedX;
                        item.DetachedY = boundedY;
                        item.DetachedWidth = boundedW;
                        item.DetachedHeight = boundedH;

                        _detachedTabs.Add(item);
                        itemsToRestore.Add((item, det));
                    }
                }
                catch (Exception ex)
                {
                    // Error isolation per detached tab
                    _logger.LogError(ex, "[TabTearOff] Failed to restore individual detached tab: {TabId}", det.TabId);
                }
            }

            // Group by ContainerId to restore multi-tab windows (FR-70-12)
            var groups = itemsToRestore.GroupBy(x => x.Info.ContainerId);
            foreach (var group in groups)
            {
                var groupItems = group.Select(x => x.Item).ToList();
                if (string.IsNullOrEmpty(group.Key))
                {
                    // No container ID (old settings or legacy) -> restore individually
                    foreach (var item in groupItems)
                    {
                        try
                        {
                            _windowManagementService.TearOff.RestoreDetached(item);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[TabTearOff] Failed individual restoration for tab {TabId}.", item.Id);
                        }
                    }
                }
                else
                {
                    try
                    {
                        // Atomic group restoration into a single multi-tab window
                        _windowManagementService.TearOff.RestoreDetachedGroup(groupItems);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TabTearOff] Failed atomic group restoration for ContainerId {ContainerId}. Falling back to individual restoration.", group.Key);
                        foreach (var item in groupItems)
                        {
                            try
                            {
                                _windowManagementService.TearOff.RestoreDetached(item);
                            }
                            catch (Exception iex)
                            {
                                _logger.LogError(iex, "[TabTearOff] Failed individual fallback restoration for tab {TabId}.", item.Id);
                            }
                        }
                    }
                }
            }
        }

        public void Capture(List<DetachedTabInfo> destination, IReadOnlyList<CoreIndicatorSettings>? fallbackIndicators = null)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            _dispatcherService.VerifyAccess();

            destination.Clear();

            // Pre-allocate to prevent GC/re-allocation overhead
            if (destination.Capacity < _detachedTabs.Count)
            {
                destination.Capacity = _detachedTabs.Count;
            }

            foreach (var item in _detachedTabs)
            {
                if (item == null) continue;

                try
                {
                    var info = new DetachedTabInfo
                    {
                        TabId = item.Id,
                        ContainerId = item.ContainerId,
                        OriginalPanelName = item.OriginalPanelName,
                        IsIndependent = item.IsIndependent,
                        X = item.DetachedX,
                        Y = item.DetachedY,
                        Width = item.DetachedWidth,
                        Height = item.DetachedHeight
                    };

                    if (item.ViewModel is ChartViewModel cvm)
                    {
                        info.Symbol = cvm.Symbol;
                        info.Timeframe = cvm.SelectedTimeFrame.ToString();
                        info.MaxCandleCount = cvm.MaxCandleCount;
                        info.ChartType = cvm.ChartType.ToString();
                        info.IsSyncEnabled = cvm.IsSyncEnabled;
                        info.IsMainWindowVisible = cvm.IsMainWindowVisible;
                        info.IsSubWindowVisible = cvm.IsSubWindowVisible;

                        // Deep clone indicators with sync fallback
                        var sourceIndicators = cvm.Indicators.Count > 0 
                            ? (IReadOnlyList<CoreIndicatorSettings>)cvm.Indicators 
                            : (cvm.IsSyncEnabled && fallbackIndicators != null ? fallbackIndicators : (IReadOnlyList<CoreIndicatorSettings>)cvm.Indicators);

                        info.Indicators = new List<CoreIndicatorSettings>(sourceIndicators.Count);
                        foreach (var indicator in sourceIndicators)
                        {
                            if (indicator != null)
                            {
                                info.Indicators.Add(indicator.Clone());
                            }
                        }
                    }

                    destination.Add(info);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TabTearOff] Failed to capture state for tab {TabId}.", item.Id);
                }
            }
        }

        public void Receive(RegisterDetachedTabMessage message)
        {
            if (message?.Value == null) return;

            if (_dispatcherService.CheckAccess())
            {
                RegisterActiveDetachedTab(message.Value);
            }
            else
            {
                _dispatcherService.Post(
                    static s => s.Self.RegisterActiveDetachedTab(s.Item),
                    (Self: this, Item: message.Value));
            }
        }

        public void Receive(ChartSymbolChangedMessage message)
        {
            if (message?.Sender == null) return;

            if (_dispatcherService.CheckAccess())
            {
                UpdateDetachedTabTitle(message.Sender, message.Value);
            }
            else
            {
                _dispatcherService.Post(
                    static s => s.Self.UpdateDetachedTabTitle(s.Sender, s.Title),
                    (Self: this, Sender: message.Sender, Title: message.Value));
            }
        }

        private void UpdateDetachedTabTitle(object sender, string title)
        {
            for (int i = 0; i < _detachedTabs.Count; i++)
            {
                var existing = _detachedTabs[i];
                if (existing != null && existing.ViewModel == sender)
                {
                    existing.Title = title;
                    break;
                }
            }
        }

        public void Dispose()
        {
            _messenger.UnregisterAll(this);

            foreach (var item in _detachedTabs)
            {
                if (item == null) continue;

                try
                {
                    if (item.ViewModel is IDisposable disposableVm)
                    {
                        disposableVm.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TabTearOff] Failed to dispose ViewModel for tab {TabId}.", item.Id);
                }
            }

            _detachedTabs.Clear();
            _logger.LogInformation("[TabTearOff] DetachedTabManager cleanly disposed.");
        }
    }
}
