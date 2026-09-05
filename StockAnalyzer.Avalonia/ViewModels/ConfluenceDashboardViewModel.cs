using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.Linq;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// High-performance dashboard logic for real-time confluence score monitoring.
/// Implements Zero-Allocation updates via pre-allocated buffers and in-place property updates.
/// </summary>
public partial class ConfluenceDashboardViewModel : ViewModelBase, IConfluenceDashboardViewModel, IRecipient<ConfluenceSnapshotUpdatedMessage>
{
    private readonly IMessenger _messenger;
    private readonly SignalOrchestrator _orchestrator;
    private readonly IDispatcherService _dispatcherService;
    
    // Zero-Allocation Buffers
    private readonly List<ConfluenceSignal> _workspace = new(64);
    private readonly List<ConfluenceSignal> _dedupedWorkspace = new(64);
    
    // UI Notification Support (Static field avoids repeating nameof() and repeated string allocations)
    private static readonly System.ComponentModel.PropertyChangedEventArgs TotalScoreArgs = new(nameof(TotalScore));

    [ObservableProperty]
    private double _totalScore;

    public ObservableCollection<IndicatorDashboardItem> IndicatorItems { get; } = new();

    public ConfluenceDashboardViewModel(IMessenger messenger, SignalOrchestrator orchestrator, IDispatcherService dispatcherService)
    {
        _messenger = messenger;
        _orchestrator = orchestrator;
        _dispatcherService = dispatcherService;

        _messenger.Register(this);
    }

    /// <summary>
    /// Real-time response to chart snapshot updates.
    /// </summary>
    public void Receive(ConfluenceSnapshotUpdatedMessage message)
    {
        _dispatcherService.Post(() => {
            UpdateLatestData(message.FullIndicatorResults, message.IndicatorSettings, message.FullCount);
        });
    }

    public void UpdateLatestData() { /* Not used by messenger flow */ }

    public void UpdateLatestData(
        IReadOnlyDictionary<string, IIndicatorResult>? results, 
        IReadOnlyList<CoreIndicatorSettings> settings, 
        int fullCount)
    {
        // 1. Boundary Guard
        if (fullCount <= 0 || results == null || results.Count == 0)
        {
            TotalScore = 50;
            // Handle clearing of UI items if needed, but for Zero-Alloc we usually just set IsActive=false
            for (int i = 0; i < IndicatorItems.Count; i++) IndicatorItems[i].IsActive = false;
            return;
        }

        int latestIndex = fullCount - 1;
        _workspace.Clear();
        _dedupedWorkspace.Clear();

        // 2. Sync IndicatorItems collection size (In-place update prep)
        // If the number of indicators changed, we adjust the collection.
        // We reuse existing instances for O(1) property updates.
        int activeIndicatorCount = 0;
        for (int i = 0; i < settings.Count; i++)
        {
            if (settings[i].IsEnabled) activeIndicatorCount++;
        }

        while (IndicatorItems.Count < activeIndicatorCount) IndicatorItems.Add(new IndicatorDashboardItem());
        while (IndicatorItems.Count > activeIndicatorCount) IndicatorItems.RemoveAt(IndicatorItems.Count - 1);

        // 3. Process each active indicator using for-loop over settings (avoids Dictionary enumerator allocation)
        int itemIdx = 0;
        for (int i = 0; i < settings.Count; i++)
        {
            var setting = settings[i];
            if (!setting.IsEnabled) continue;

            if (results.TryGetValue(setting.Id, out var result))
            {
                var dashboardItem = IndicatorItems[itemIdx++];
                
                // Update Dashboard Item properties In-place
                dashboardItem.Name = setting.DisplayName;
                dashboardItem.Group = setting.ConfluenceGroup;
                dashboardItem.Weight = setting.ConfluenceWeight;
                dashboardItem.IsActive = true;
                
                // Get latest value from indicator
                if (result.MainValues.Count > latestIndex)
                {
                    dashboardItem.Value = (double)(result.MainValues[latestIndex] ?? 0m);
                }

                // Collect signals if provider exists
                if (result.SignalProvider != null)
                {
                    var signals = result.SignalProvider.GetSignals(latestIndex, result, setting);
                    
                    // Note: signals (IEnumerable) might still allocate an enumerator. 
                    // To be fixed in Calculation step if required, but usually SignalProviders are lean.
                    foreach (var signal in signals)
                    {
                        _workspace.Add(signal);
                        
                        // Update direction/strength for the dashboard display based on the first signal found
                        // (Usually one indicator gives one signal per bar in this context)
                        dashboardItem.Direction = signal.Direction;
                        dashboardItem.Strength = signal.Strength;
                    }
                }
                else
                {
                    dashboardItem.Direction = SignalDirection.Neutral;
                    dashboardItem.Strength = 0;
                }
            }
        }

        // 4. Orchestrate (Deduplication + Decorrelation + Weighted Aggregation)
        if (_workspace.Count > 0)
        {
            var res = _orchestrator.Orchestrate(latestIndex, _workspace, _dedupedWorkspace);
            
            // Avoid calling setter if value hasn't changed to save on PropertyChanged propagation
            if (Math.Abs(TotalScore - res.Score) > 0.001)
            {
                TotalScore = res.Score;
            }
        }
        else
        {
            TotalScore = 50;
        }
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
