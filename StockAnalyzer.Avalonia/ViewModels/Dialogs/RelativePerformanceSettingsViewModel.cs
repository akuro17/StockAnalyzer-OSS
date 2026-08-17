using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using StockAnalyzer.Core.Models.Watchlist;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class RelativePerformanceSettingsViewModel : BaseChartSettingsViewModel
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private IReadOnlyList<string>? _masterTickers;
    private CancellationTokenSource? _searchCts;

    public override string TitleKey => "ChartType_RelativePerformance";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    private ObservableCollection<string> _symbolList = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private StockAnalyzer.Core.Models.ComparisonMode _comparisonMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _comparisonZScorePeriod;

    public static IReadOnlyList<StockAnalyzer.Core.Models.ComparisonMode> ModeOptions { get; } = Enum.GetValues<StockAnalyzer.Core.Models.ComparisonMode>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showFloatingTooltip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showTickerInsteadOfValue;

    [ObservableProperty]
    private string _newSymbol = string.Empty;

    [ObservableProperty]
    private ObservableCollection<RelativePerformancePreset> _savedPresets = new();

    [ObservableProperty]
    private string _newPresetName = string.Empty;

    [ObservableProperty]
    private RelativePerformancePreset? _selectedPreset;

    public ObservableCollection<string> Suggestions { get; } = new();

    [ObservableProperty]
    private string? _selectedSuggestion;



    partial void OnNewSymbolChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(value))
        {
            Suggestions.Clear();
            return;
        }

        var token = _searchCts.Token;

        // Offload search to background thread with zero closures or boxing
        Task.Factory.StartNew(static async state =>
        {
            var vm = (RelativePerformanceSettingsViewModel)state!;
            var token = vm._searchCts?.Token ?? CancellationToken.None;
            try
            {
                // Debounce: Standard Input Throttle
                await Task.Delay(SettingsConstants.UI_StandardInputThrottleMs, token);
                vm.UpdateSearch(vm.NewSymbol, token);
            }
            catch (TaskCanceledException)
            {
                // Ignore cancellation
            }
        }, this, token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    public RelativePerformanceSettingsViewModel(
        IChartSettingsManager settingsManager,
        IMarketDataProvider marketDataProvider,
        IDispatcherService dispatcherService,
        ILogger<RelativePerformanceSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
        _marketDataProvider = marketDataProvider;
        _dispatcherService = dispatcherService;

        _ = InitializeAsync();
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        SymbolList = new ObservableCollection<string>(settings.ComparisonTargets?.ToList() ?? new List<string>());
        SavedPresets = new ObservableCollection<RelativePerformancePreset>(settings.RelativePerformancePresets?.ToList() ?? new List<RelativePerformancePreset>());
        
        ShowFloatingTooltip = settings.ShowFloatingTooltip;
        ComparisonMode = settings.RelativePerformanceMode;
        ComparisonZScorePeriod = settings.ComparisonZScorePeriod;
        ShowTickerInsteadOfValue = settings.ShowTickerInsteadOfValue;
        
        // Ensure we listen to collection changes for IsModified
        SymbolList.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsModified));
        SavedPresets.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsModified));
    }

    protected override void UpdateInitialState()
    {
        _hasExplicitDeletion = false;
        _initialSymbolList = SymbolList.ToList();
        _initialSavedPresets = SavedPresets.ToList();
        _initialShowFloatingTooltip = ShowFloatingTooltip;
        _initialComparisonMode = ComparisonMode;
        _initialComparisonZScorePeriod = ComparisonZScorePeriod;
        _initialShowTickerInsteadOfValue = ShowTickerInsteadOfValue;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ComparisonTargets = SymbolList.ToList(),
            RelativePerformancePresets = SavedPresets.ToList(),
            ShowFloatingTooltip = ShowFloatingTooltip,
            RelativePerformanceMode = ComparisonMode,
            ComparisonZScorePeriod = ComparisonZScorePeriod,
            ShowTickerInsteadOfValue = ShowTickerInsteadOfValue
        };
    }

    [RelayCommand]
    private void AddSymbol()
    {
        if (string.IsNullOrWhiteSpace(NewSymbol) || SymbolList.Count >= WatchlistConstants.MaxRelativePerformanceTargets)
            return;

        var symbol = NewSymbol.Trim().ToUpperInvariant();
        if (!SymbolList.Contains(symbol))
        {
            SymbolList.Add(symbol);
            NewSymbol = string.Empty;
            OnPropertyChanged(nameof(SymbolList)); // Trigger throttled preview update
        }
    }

    [RelayCommand]
    private void RemoveSymbol(string symbol)
    {
        if (symbol != null && SymbolList.Remove(symbol))
        {
            OnPropertyChanged(nameof(SymbolList)); // Trigger throttled preview update
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(NewPresetName) || SymbolList.Count == 0)
            return;

        var name = NewPresetName.Trim();
        var preset = new RelativePerformancePreset
        {
            Name = name,
            Targets = SymbolList.ToList()
        };

        var existing = SavedPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var index = SavedPresets.IndexOf(existing);
            SavedPresets[index] = preset;
        }
        else
        {
            SavedPresets.Add(preset);
        }

        NewPresetName = string.Empty;
        OnPropertyChanged(nameof(IsModified));
    }

    [RelayCommand]
    private void LoadPreset(RelativePerformancePreset? preset)
    {
        if (preset == null || preset.Targets == null)
            return;

        SymbolList.Clear();
        foreach (var target in preset.Targets)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                SymbolList.Add(target.Trim().ToUpperInvariant());
            }
        }

        OnPropertyChanged(nameof(SymbolList)); // Trigger throttled preview update
        OnPropertyChanged(nameof(IsModified));
    }

    [RelayCommand]
    private void DeletePreset(RelativePerformancePreset? preset)
    {
        if (preset != null && SavedPresets.Remove(preset))
        {
            _hasExplicitDeletion = true;
            OnPropertyChanged(nameof(IsModified));
        }
    }

    // Tracks that a preset was explicitly removed this session, since a subsequent
    // re-add could make SavedPresets diff back to an empty match against _initialSavedPresets,
    // masking the deletion from the pure count/content comparison below.
    private bool _hasExplicitDeletion;

    private List<string> _initialSymbolList = new();
    private List<RelativePerformancePreset> _initialSavedPresets = new();
    private bool _initialShowFloatingTooltip;
    private StockAnalyzer.Core.Models.ComparisonMode _initialComparisonMode;
    private int _initialComparisonZScorePeriod;
    private bool _initialShowTickerInsteadOfValue;

    public override bool IsModified
    {
        get
        {
            if (_hasExplicitDeletion) return true;

            if (ShowFloatingTooltip != _initialShowFloatingTooltip ||
                ComparisonMode != _initialComparisonMode ||
                ComparisonZScorePeriod != _initialComparisonZScorePeriod ||
                ShowTickerInsteadOfValue != _initialShowTickerInsteadOfValue ||
                SymbolList.Count != _initialSymbolList.Count ||
                SavedPresets.Count != _initialSavedPresets.Count)
            {
                return true;
            }

            for (int i = 0; i < SymbolList.Count; i++)
            {
                if (SymbolList[i] != _initialSymbolList[i])
                    return true;
            }

            for (int i = 0; i < SavedPresets.Count; i++)
            {
                if (SavedPresets[i] != _initialSavedPresets[i])
                    return true;
            }

            return false;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            _masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize master ticker list in RelativePerformanceSettingsViewModel");
            _masterTickers = Array.Empty<string>();
        }
    }

    private void UpdateSearch(string input, CancellationToken ct)
    {
        if (_masterTickers == null) return;

        string normalized = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            _dispatcherService.Post(static vm =>
            {
                vm.Suggestions.Clear();
            }, this);
            return;
        }

        string[] matches = ArrayPool<string>.Shared.Rent(WatchlistConstants.MaxSearchSuggestions);
        int matchCount = 0;

        try
        {
            // Zero-allocation search loop (avoiding LINQ)
            for (int i = 0; i < _masterTickers.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    ArrayPool<string>.Shared.Return(matches, clearArray: true);
                    return;
                }

                string ticker = _masterTickers[i];

                if (ticker.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (matchCount < WatchlistConstants.MaxSearchSuggestions)
                    {
                        matches[matchCount++] = ticker;
                    }
                }
            }

            _dispatcherService.Post(static state =>
            {
                try
                {
                    if (state.ct.IsCancellationRequested) return;

                    state.vm.Suggestions.Clear();
                    for (int i = 0; i < state.matchCount; i++)
                    {
                        state.vm.Suggestions.Add(state.matches[i]);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(state.matches, clearArray: true);
                }
            }, (vm: this, matches, matchCount, ct));
        }
        catch
        {
            ArrayPool<string>.Shared.Return(matches, clearArray: true);
            throw;
        }
    }

    private bool _isDisposed;

    public override void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        base.Dispose();
    }
}

