using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public class SignalTargetOption
{
    public SignalTargetType Type { get; }
    public string DisplayName { get; }

    public SignalTargetOption(SignalTargetType type, string displayName)
    {
        Type = type;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;
}

public partial class EditTickerNotesDialogViewModel : ViewModelBase, IDisposable
{
    public string Ticker { get; }

    /// <summary>
    /// The currently displayed ticker symbol. Initialized from Ticker, can be changed
    /// via Symbol input (when IsSyncEnabled=false) or TickerSelectedMessage (when IsSyncEnabled=true).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DashboardTitle))]
    private string _symbol = string.Empty;

    /// <summary>
    /// When true, the Dashboard follows the main chart's ticker selection via TickerSelectedMessage.
    /// When false, the user can independently set Symbol via the input field.
    /// </summary>
    [ObservableProperty]
    private bool _isSyncEnabled = false;

    /// <summary>
    /// Guards against re-broadcasting TickerSelectedMessage when Symbol is updated as a
    /// result of receiving one (prevents an infinite Sync feedback loop).
    /// </summary>
    private bool _isSyncingSelection;

    /// <summary>
    /// Set by Dispose(). Guards long-lived async commands (Delete, Sync) so their continuations
    /// stop mutating state or sending messages after the dialog window has been closed.
    /// </summary>
    private bool _isDisposed;

    /// <summary>All available tickers for the Symbol AutoCompleteBox.</summary>
    public ObservableCollection<string> AvailableTickers { get; } = new();

    public string DashboardTitle => $"{Symbol} Dashboard";

    [ObservableProperty] private string _longText = string.Empty;
    [ObservableProperty] private string _exitLongText = string.Empty;
    [ObservableProperty] private string _stopLossLongText = string.Empty;
    [ObservableProperty] private string _shortText = string.Empty;
    [ObservableProperty] private string _exitShortText = string.Empty;
    [ObservableProperty] private string _stopLossShortText = string.Empty;
    [ObservableProperty] private string? _reminder = string.Empty;

    // Signal Flags
    [ObservableProperty] private bool? _isLong;
    [ObservableProperty] private bool? _isTPLong;
    [ObservableProperty] private bool? _isSLLong;
    [ObservableProperty] private bool? _isShort;
    [ObservableProperty] private bool? _isTPShort;
    [ObservableProperty] private bool? _isSLShort;

    public decimal? Long => decimal.TryParse(LongText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    public decimal? ExitLong => decimal.TryParse(ExitLongText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    public decimal? StopLossLong => decimal.TryParse(StopLossLongText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    public decimal? Short => decimal.TryParse(ShortText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    public decimal? ExitShort => decimal.TryParse(ExitShortText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    public decimal? StopLossShort => decimal.TryParse(StopLossShortText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;

    // Legacy backward-compatibility properties
    public decimal? EntryPrice => Long;
    public decimal? TargetPrice => ExitLong;
    public decimal? StopLoss => StopLossLong;
    public string EntryPriceText { get => LongText; set => LongText = value; }
    public string TargetPriceText { get => ExitLongText; set => ExitLongText = value; }
    public string StopLossText { get => StopLossLongText; set => StopLossLongText = value; }

    public Action<bool>? CloseAction { get; set; }
    public Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? OnApplyCallback { get; set; }

    // Embedded Indicator Registration VM (reusing exact Screener catalog & controls)
    public IndicatorRegistrationViewModel IndicatorRegistrationViewModel { get; }

    // Draft list of individual conditions selected before bundling
    public ObservableCollection<ScreenerIndicatorEntry> DraftConditions { get; } = new();

    // Registered Bundled Conditions
    public ObservableCollection<BundledSignalCondition> BundledConditions { get; } = new();

    // Horizontal Matrix Columns (Long, TP Long, SL Long, Short, TP Short, SL Short)
    public ObservableCollection<SignalMatrixColumnViewModel> MatrixColumns { get; } = new();

    private const string DisplayNameTakeProfitLong = "Take Profit Long";
    private const string DisplayNameStopLossLong = "Stop Loss Long";
    private const string DisplayNameTakeProfitShort = "Take Profit Short";
    private const string DisplayNameStopLossShort = "Stop Loss Short";

    // English-only Signal Menu Options (with None for active target reset)
    public IReadOnlyList<SignalTargetOption> SignalOptions { get; } = new[]
    {
        new SignalTargetOption(SignalTargetType.None, "None"),
        new SignalTargetOption(SignalTargetType.Long, "Long"),
        new SignalTargetOption(SignalTargetType.ExitLong, DisplayNameTakeProfitLong),
        new SignalTargetOption(SignalTargetType.StopLossLong, DisplayNameStopLossLong),
        new SignalTargetOption(SignalTargetType.Short, "Short"),
        new SignalTargetOption(SignalTargetType.ExitShort, DisplayNameTakeProfitShort),
        new SignalTargetOption(SignalTargetType.StopLossShort, DisplayNameStopLossShort)
    };

    // Signal Target Options for Bundle Creation (Strictly excluding "None")
    public IReadOnlyList<SignalTargetOption> BundleTargetOptions { get; } = new[]
    {
        new SignalTargetOption(SignalTargetType.Long, "Long"),
        new SignalTargetOption(SignalTargetType.ExitLong, DisplayNameTakeProfitLong),
        new SignalTargetOption(SignalTargetType.StopLossLong, DisplayNameStopLossLong),
        new SignalTargetOption(SignalTargetType.Short, "Short"),
        new SignalTargetOption(SignalTargetType.ExitShort, DisplayNameTakeProfitShort),
        new SignalTargetOption(SignalTargetType.StopLossShort, DisplayNameStopLossShort)
    };

    [ObservableProperty]
    private SignalTargetOption _selectedSignalOption;

    partial void OnSelectedSignalOptionChanged(SignalTargetOption value)
    {
        if (value == null) return;

        IsLong = value.Type == SignalTargetType.Long;
        IsTPLong = value.Type == SignalTargetType.ExitLong;
        IsSLLong = value.Type == SignalTargetType.StopLossLong;
        IsShort = value.Type == SignalTargetType.Short;
        IsTPShort = value.Type == SignalTargetType.ExitShort;
        IsSLShort = value.Type == SignalTargetType.StopLossShort;
        UpdateMatrixColumns();
    }

    [ObservableProperty]
    private SignalTargetOption _selectedTargetForBundleOption;

    [ObservableProperty]
    private string _newBundleName = "Condition Set 1";

    private IReadOnlyList<CandleData> _candles = Array.Empty<CandleData>();
    private TickerMetadata _metadata = TickerMetadata.Unknown;

    // Constructor-injected (resolved via ActivatorUtilities from DialogService, which is the
    // sole production caller). Nullable because this VM is also constructed directly (`new(...)`)
    // by unit tests that don't exercise data-loading/dialog/sync behavior.
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly IDialogService? _dialogService;
    private readonly ITickerSyncService? _tickerSyncService;

    /// <summary>OHLCV rows for the History tab, kept sorted newest-first, mirroring _candles.</summary>
    public ObservableCollection<CandleData> HistoryRows { get; } = new();

    /// <summary>Drives the History tab's Zero-State overlay: true once at least one row is loaded.</summary>
    public bool HasHistoryData => HistoryRows.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteHistoryFromSelectedCommand))]
    private CandleData? _selectedHistoryRow;

    public EditTickerNotesDialogViewModel(
        string ticker,
        decimal? longVal = null,
        decimal? exitLong = null,
        decimal? stopLossLong = null,
        decimal? shortVal = null,
        decimal? exitShort = null,
        decimal? stopLossShort = null,
        string? reminder = null,
        Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onApplyCallback = null,
        IMarketDataProvider? marketDataProvider = null,
        IDialogService? dialogService = null,
        ITickerSyncService? tickerSyncService = null)
    {
        _marketDataProvider = marketDataProvider;
        _dialogService = dialogService;
        _tickerSyncService = tickerSyncService;

        Ticker = ticker ?? string.Empty;
        // Direct field assignment to avoid triggering OnSymbolChanged during construction
        _symbol = Ticker;
        _longText = longVal?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _exitLongText = exitLong?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _stopLossLongText = stopLossLong?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _shortText = shortVal?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _exitShortText = exitShort?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _stopLossShortText = stopLossShort?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _reminder = reminder ?? string.Empty;
        OnApplyCallback = onApplyCallback;

        IndicatorRegistrationViewModel = new IndicatorRegistrationViewModel();
        // Hide Screener logical join AND/OR buttons and Operator/Mode headers specifically in Dashboard
        IndicatorRegistrationViewModel.IsLogicalJoinVisible = false;
        IndicatorRegistrationViewModel.IsHeaderRowVisible = false;

        // Auto-transfer newly added indicators from catalog Add button directly into DraftConditions
        IndicatorRegistrationViewModel.RegisteredEntries.CollectionChanged += OnCatalogEntriesCollectionChanged;

        // Default MUST be Long per user specification
        var longOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SignalOptions[1];
        _selectedSignalOption = longOption;
        _selectedTargetForBundleOption = BundleTargetOptions[0]; // Default Long (excluding None)

        InitializeMatrixColumns();
        LoadExistingStrategyMetadata();
        _ = LoadTickerCandlesAsync();

        // Subscribe to ticker selection to support Sync mode
        WeakReferenceMessenger.Default.Register<TickerSelectedMessage>(this, static (recipient, message) =>
        {
            var vm = (EditTickerNotesDialogViewModel)recipient;
            if (vm.IsSyncEnabled && !string.IsNullOrEmpty(message.Value))
            {
                vm._isSyncingSelection = true;
                try
                {
                    vm.Symbol = message.Value;
                }
                finally
                {
                    vm._isSyncingSelection = false;
                }
            }
        });

        // Load available tickers for the AutoCompleteBox
        _ = LoadAvailableTickersAsync();
    }

    private void OnCatalogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (ScreenerIndicatorEntry entry in e.NewItems)
            {
                if (!DraftConditions.Contains(entry))
                {
                    DraftConditions.Add(entry);
                }
            }
            EvaluateSignals();
        }
    }

    public EditTickerNotesDialogViewModel(
        string ticker,
        decimal? entryPrice,
        decimal? targetPrice,
        decimal? stopLoss,
        string? reminder,
        Action<decimal?, decimal?, decimal?, string?>? legacyCallback)
        : this(ticker, entryPrice, targetPrice, stopLoss, null, null, null, reminder,
            (l, el, sll, s, es, sls, n) => legacyCallback?.Invoke(l, el, sll, n))
    {
    }

    private void InitializeMatrixColumns()
    {
        MatrixColumns.Clear();
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.Long, "Long", IsLong));
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.ExitLong, DisplayNameTakeProfitLong, IsTPLong));
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.StopLossLong, DisplayNameStopLossLong, IsSLLong));
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.Short, "Short", IsShort));
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.ExitShort, DisplayNameTakeProfitShort, IsTPShort));
        MatrixColumns.Add(new SignalMatrixColumnViewModel(SignalTargetType.StopLossShort, DisplayNameStopLossShort, IsSLShort));
    }

    /// <summary>
    /// Handles Symbol property changes: reloads all dashboard data for the new symbol.
    /// Not called during construction (Symbol is set via direct field assignment).
    /// When Sync is ON and the change originates from the user (not from receiving a
    /// TickerSelectedMessage), broadcasts the new symbol so the main chart follows it too.
    /// </summary>
    partial void OnSymbolChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        ReloadForSymbol(value);

        if (IsSyncEnabled && !_isSyncingSelection)
        {
            WeakReferenceMessenger.Default.Send(new TickerSelectedMessage(value));
        }
    }

    /// <summary>
    /// Handles IsSyncEnabled changes. When Sync is turned ON, immediately fetches the
    /// current main chart ticker via CurrentTickerRequestMessage.
    /// </summary>
    partial void OnIsSyncEnabledChanged(bool value)
    {
        if (value)
        {
            var request = new CurrentTickerRequestMessage();
            WeakReferenceMessenger.Default.Send(request);
            if (request.HasReceivedResponse && !string.IsNullOrEmpty(request.Response))
            {
                _isSyncingSelection = true;
                try
                {
                    Symbol = request.Response;
                }
                finally
                {
                    _isSyncingSelection = false;
                }
            }
        }
    }

    /// <summary>
    /// Reloads all dashboard data for the specified symbol.
    /// Called when Symbol changes after initial construction.
    /// </summary>
    private void ReloadForSymbol(string symbol)
    {
        // Clear all current data before loading new symbol's data
        _longText = string.Empty;
        OnPropertyChanged(nameof(LongText));
        _exitLongText = string.Empty;
        OnPropertyChanged(nameof(ExitLongText));
        _stopLossLongText = string.Empty;
        OnPropertyChanged(nameof(StopLossLongText));
        _shortText = string.Empty;
        OnPropertyChanged(nameof(ShortText));
        _exitShortText = string.Empty;
        OnPropertyChanged(nameof(ExitShortText));
        _stopLossShortText = string.Empty;
        OnPropertyChanged(nameof(StopLossShortText));
        _reminder = string.Empty;
        OnPropertyChanged(nameof(Reminder));
        IsLong = null;
        IsTPLong = null;
        IsSLLong = null;
        IsShort = null;
        IsTPShort = null;
        IsSLShort = null;
        BundledConditions.Clear();
        DraftConditions.Clear();

        // Load price fields and signal flags from repository for the new symbol
        var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(symbol);
        if (strategy != null)
        {
            _longText = strategy.EffectiveLong?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(LongText));
            _exitLongText = strategy.EffectiveExitLong?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(ExitLongText));
            _stopLossLongText = strategy.EffectiveStopLossLong?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(StopLossLongText));
            _shortText = strategy.Short?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(ShortText));
            _exitShortText = strategy.ExitShort?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(ExitShortText));
            _stopLossShortText = strategy.StopLossShort?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(StopLossShortText));
            _reminder = strategy.Reminder ?? string.Empty;
            OnPropertyChanged(nameof(Reminder));
            IsLong = strategy.IsLong;
            IsTPLong = strategy.IsTPLong;
            IsSLLong = strategy.IsSLLong;
            IsShort = strategy.IsShort;
            IsTPShort = strategy.IsTPShort;
            IsSLShort = strategy.IsSLShort;

            if (IsLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SignalOptions[1];
            else if (IsTPLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitLong) ?? SignalOptions[1];
            else if (IsSLLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossLong) ?? SignalOptions[1];
            else if (IsShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Short) ?? SignalOptions[1];
            else if (IsTPShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitShort) ?? SignalOptions[1];
            else if (IsSLShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossShort) ?? SignalOptions[1];
            else SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SignalOptions[1];
        }

        var savedBundles = UserStrategyMetadataRepository.Instance.GetSignalBundles(symbol);
        if (savedBundles != null && savedBundles.Count > 0)
        {
            foreach (var b in savedBundles)
                BundledConditions.Add(b);
        }

        InitializeMatrixColumns();
        _ = LoadTickerCandlesAsync();
    }

    private void LoadExistingStrategyMetadata()
    {
        if (string.IsNullOrWhiteSpace(Symbol)) return;

        var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(Symbol);
        if (strategy != null)
        {
            if (string.IsNullOrEmpty(_longText) && strategy.EffectiveLong.HasValue)
            {
                _longText = strategy.EffectiveLong.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(LongText));
            }
            if (string.IsNullOrEmpty(_exitLongText) && strategy.EffectiveExitLong.HasValue)
            {
                _exitLongText = strategy.EffectiveExitLong.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(ExitLongText));
            }
            if (string.IsNullOrEmpty(_stopLossLongText) && strategy.EffectiveStopLossLong.HasValue)
            {
                _stopLossLongText = strategy.EffectiveStopLossLong.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(StopLossLongText));
            }
            if (string.IsNullOrEmpty(_shortText) && strategy.Short.HasValue)
            {
                _shortText = strategy.Short.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(ShortText));
            }
            if (string.IsNullOrEmpty(_exitShortText) && strategy.ExitShort.HasValue)
            {
                _exitShortText = strategy.ExitShort.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(ExitShortText));
            }
            if (string.IsNullOrEmpty(_stopLossShortText) && strategy.StopLossShort.HasValue)
            {
                _stopLossShortText = strategy.StopLossShort.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(StopLossShortText));
            }

            IsLong = strategy.IsLong;
            IsTPLong = strategy.IsTPLong;
            IsSLLong = strategy.IsSLLong;
            IsShort = strategy.IsShort;
            IsTPShort = strategy.IsTPShort;
            IsSLShort = strategy.IsSLShort;

            if (IsLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SignalOptions[1];
            else if (IsTPLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitLong) ?? SignalOptions[1];
            else if (IsSLLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossLong) ?? SignalOptions[1];
            else if (IsShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Short) ?? SignalOptions[1];
            else if (IsTPShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitShort) ?? SignalOptions[1];
            else if (IsSLShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossShort) ?? SignalOptions[1];
            else SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SignalOptions[1]; // Default Long
        }

        var savedBundles = UserStrategyMetadataRepository.Instance.GetSignalBundles(Symbol);
        if (savedBundles != null && savedBundles.Count > 0)
        {
            BundledConditions.Clear();
            foreach (var b in savedBundles)
            {
                BundledConditions.Add(b);
            }
        }

        UpdateMatrixColumns();
    }

    private async Task LoadTickerCandlesAsync()
    {
        if (string.IsNullOrWhiteSpace(Symbol)) return;

        var loadSymbol = Symbol; // capture to avoid race with Symbol changes
        try
        {
            if (_marketDataProvider != null)
            {
                _candles = await _marketDataProvider.GetTickersDataAsync(loadSymbol, TimeFrame.D1);
                _metadata = await _marketDataProvider.GetMetadataAsync(loadSymbol);
            }
            // Only apply if Symbol hasn't changed during the async load
            if (string.Equals(Symbol, loadSymbol, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_longText) && (_metadata.Long.HasValue || _metadata.EntryPrice.HasValue))
                {
                    _longText = (_metadata.Long ?? _metadata.EntryPrice)!.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(LongText));
                }
                if (string.IsNullOrEmpty(_exitLongText) && (_metadata.ExitLong.HasValue || _metadata.TargetPrice.HasValue))
                {
                    _exitLongText = (_metadata.ExitLong ?? _metadata.TargetPrice)!.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(ExitLongText));
                }
                if (string.IsNullOrEmpty(_stopLossLongText) && (_metadata.StopLossLong.HasValue || _metadata.StopLoss.HasValue))
                {
                    _stopLossLongText = (_metadata.StopLossLong ?? _metadata.StopLoss)!.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(StopLossLongText));
                }
                if (string.IsNullOrEmpty(_shortText) && _metadata.Short.HasValue)
                {
                    _shortText = _metadata.Short.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(ShortText));
                }
                if (string.IsNullOrEmpty(_exitShortText) && _metadata.ExitShort.HasValue)
                {
                    _exitShortText = _metadata.ExitShort.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(ExitShortText));
                }
                if (string.IsNullOrEmpty(_stopLossShortText) && _metadata.StopLossShort.HasValue)
                {
                    _stopLossShortText = _metadata.StopLossShort.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(StopLossShortText));
                }

                EvaluateSignals();
                RefreshHistoryRows();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load candle data for {loadSymbol}: {ex.Message}");
        }
    }

    /// <summary>Rebuilds HistoryRows from _candles, newest date first (History tab display order).</summary>
    private void RefreshHistoryRows()
    {
        SelectedHistoryRow = null;
        HistoryRows.Clear();
        foreach (var candle in _candles.OrderByDescending(c => c.Timestamp))
        {
            HistoryRows.Add(candle);
        }
        OnPropertyChanged(nameof(HasHistoryData));
    }

    private bool CanDeleteHistoryFromSelected() => SelectedHistoryRow.HasValue;

    /// <summary>
    /// Deletes the selected History row and every newer row for the current symbol, preserving
    /// time-series continuity (only the contiguous tail is ever removed). Requires user
    /// confirmation since this permanently rewrites the underlying Parquet file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteHistoryFromSelected))]
    private async Task DeleteHistoryFromSelectedAsync()
    {
        if (_isDisposed || !SelectedHistoryRow.HasValue || string.IsNullOrWhiteSpace(Symbol)) return;

        var cutoff = SelectedHistoryRow.Value.Timestamp;
        var symbol = Symbol;
        var countToDelete = HistoryRows.Count(r => r.Timestamp >= cutoff);
        if (countToDelete == 0) return;

        if (_dialogService == null)
        {
            // Fail-safe: never delete without an explicit user confirmation.
            System.Diagnostics.Debug.WriteLine("DeleteHistoryFromSelectedAsync aborted: DialogService unavailable for confirmation.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            LocalizationManager.Instance["History_ConfirmDelete_Title"],
            string.Format(LocalizationManager.Instance["History_ConfirmDelete_Message"], countToDelete, symbol, cutoff));
        if (!confirmed || _isDisposed) return;

        if (_marketDataProvider == null) return;

        try
        {
            await _marketDataProvider.DeleteTickerDataFromDateAsync(symbol, cutoff);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete history for {symbol}: {ex.Message}");
            return;
        }

        if (_isDisposed) return;

        await LoadTickerCandlesAsync();
        if (_isDisposed) return;

        // Notify any view currently displaying this symbol (main chart, watchlist row) to refresh.
        WeakReferenceMessenger.Default.Send(new TickerDataRefreshedMessage(symbol));
    }

    /// <summary>
    /// Runs the shared "Sync Progress" flow (same window used by the chart's "Sync Symbol" action)
    /// for the currently displayed symbol, then reloads the History tab and notifies other views.
    /// </summary>
    [RelayCommand]
    private Task SyncHistoryAsync()
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(Symbol) || _tickerSyncService == null) return Task.CompletedTask;

        var symbol = Symbol;
        return _tickerSyncService.SyncSingleTickerAsync(symbol, async () =>
        {
            // Guard against "Ghost" updates: the Sync Progress window can outlive this dialog.
            if (_isDisposed) return;
            await LoadTickerCandlesAsync();
            if (_isDisposed) return;
            WeakReferenceMessenger.Default.Send(new TickerDataRefreshedMessage(symbol));
        });
    }

    private async Task LoadAvailableTickersAsync()
    {
        try
        {
            if (_marketDataProvider != null)
            {
                var tickers = await _marketDataProvider.GetAvailableTickersAsync();
                if (tickers != null)
                {
                    AvailableTickers.Clear();
                    foreach (var t in tickers.OrderBy(x => x))
                        AvailableTickers.Add(t);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load available tickers for Dashboard: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddIndicatorToDraft()
    {
        if (IndicatorRegistrationViewModel != null && IndicatorRegistrationViewModel.RegisteredEntries.Count > 0)
        {
            foreach (var entry in IndicatorRegistrationViewModel.RegisteredEntries)
            {
                if (!DraftConditions.Contains(entry))
                {
                    DraftConditions.Add(entry);
                }
            }
            EvaluateSignals();
        }
    }

    [RelayCommand]
    private void RemoveDraftCondition(ScreenerIndicatorEntry? entry)
    {
        if (entry != null)
        {
            DraftConditions.Remove(entry);
            EvaluateSignals();
        }
    }

    [RelayCommand]
    private void ClearDraftConditions()
    {
        DraftConditions.Clear();
        EvaluateSignals();
    }

    [RelayCommand]
    private void BundleConditions()
    {
        if (DraftConditions.Count == 0) return;

        string bundleName = string.IsNullOrWhiteSpace(NewBundleName)
            ? $"Condition Set {BundledConditions.Count + 1}"
            : NewBundleName.Trim();

        SignalTargetType targetType = SelectedTargetForBundleOption?.Type ?? SignalTargetType.Long;
        var bundle = new BundledSignalCondition(bundleName, targetType, DraftConditions);
        BundledConditions.Add(bundle);

        DraftConditions.Clear();
        NewBundleName = $"Condition Set {BundledConditions.Count + 1}";

        EvaluateSignals();
    }

    [RelayCommand]
    private void RemoveBundle(BundledSignalCondition? bundle)
    {
        if (bundle != null)
        {
            BundledConditions.Remove(bundle);
            EvaluateSignals();
        }
    }

    [RelayCommand]
    private void EditBundle(BundledSignalCondition? bundle)
    {
        if (bundle == null) return;

        DraftConditions.Clear();
        if (IndicatorRegistrationViewModel != null)
        {
            IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        }

        if (bundle.Conditions != null)
        {
            foreach (var cond in bundle.Conditions)
            {
                DraftConditions.Add(cond);
                if (IndicatorRegistrationViewModel != null && !IndicatorRegistrationViewModel.RegisteredEntries.Contains(cond))
                {
                    IndicatorRegistrationViewModel.RegisteredEntries.Add(cond);
                }
            }

            var firstCond = bundle.Conditions.FirstOrDefault();
            if (firstCond?.LeftHand != null && IndicatorRegistrationViewModel != null)
            {
                var allGroup = IndicatorRegistrationViewModel.NavGroups.FirstOrDefault(g => g.IsAllFilters);
                if (allGroup != null)
                {
                    IndicatorRegistrationViewModel.SelectedGroupItem = allGroup;
                }

                var indType = firstCond.LeftHand.IndicatorType;
                var match = IndicatorRegistrationViewModel.FilteredIndicators.FirstOrDefault(i => i.IndicatorType == indType);
                if (match != null)
                {
                    IndicatorRegistrationViewModel.SelectedIndicator = match;
                }
            }
        }

        NewBundleName = bundle.Name;
        if (bundle.TargetType != SignalTargetType.None)
        {
            SelectedTargetForBundleOption = BundleTargetOptions.FirstOrDefault(o => o.Type == bundle.TargetType) ?? SelectedTargetForBundleOption;
        }

        BundledConditions.Remove(bundle);
        EvaluateSignals();
    }

    public void EvaluateSignals()
    {
        if (_candles == null || _candles.Count == 0) return;

        foreach (var bundle in BundledConditions)
        {
            bool isHit = SignalEvaluationEngine.EvaluateBundle(bundle, _candles, _metadata);
            bundle.IsHit = isHit;
            bundle.StatusText = isHit ? "True" : "False";
        }

        var longBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.Long).ToList();
        if (longBundles.Count > 0)
        {
            IsLong = longBundles.Any(b => b.IsHit);
            if (IsLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Long) ?? SelectedSignalOption;
        }

        var exitLongBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.ExitLong).ToList();
        if (exitLongBundles.Count > 0)
        {
            IsTPLong = exitLongBundles.Any(b => b.IsHit);
            if (IsTPLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitLong) ?? SelectedSignalOption;
        }

        var slLongBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.StopLossLong).ToList();
        if (slLongBundles.Count > 0)
        {
            IsSLLong = slLongBundles.Any(b => b.IsHit);
            if (IsSLLong == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossLong) ?? SelectedSignalOption;
        }

        var shortBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.Short).ToList();
        if (shortBundles.Count > 0)
        {
            IsShort = shortBundles.Any(b => b.IsHit);
            if (IsShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.Short) ?? SelectedSignalOption;
        }

        var exitShortBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.ExitShort).ToList();
        if (exitShortBundles.Count > 0)
        {
            IsTPShort = exitShortBundles.Any(b => b.IsHit);
            if (IsTPShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.ExitShort) ?? SelectedSignalOption;
        }

        var slShortBundles = BundledConditions.Where(b => b.TargetType == SignalTargetType.StopLossShort).ToList();
        if (slShortBundles.Count > 0)
        {
            IsSLShort = slShortBundles.Any(b => b.IsHit);
            if (IsSLShort == true) SelectedSignalOption = SignalOptions.FirstOrDefault(o => o.Type == SignalTargetType.StopLossShort) ?? SelectedSignalOption;
        }

        UpdateMatrixColumns();
    }

    private void UpdateMatrixColumns()
    {
        foreach (var col in MatrixColumns)
        {
            col.FlagState = col.TargetType switch
            {
                SignalTargetType.Long => IsLong,
                SignalTargetType.ExitLong => IsTPLong,
                SignalTargetType.StopLossLong => IsSLLong,
                SignalTargetType.Short => IsShort,
                SignalTargetType.ExitShort => IsTPShort,
                SignalTargetType.StopLossShort => IsSLShort,
                _ => null
            };

            col.Bundles.Clear();
            foreach (var bundle in BundledConditions.Where(b => b.TargetType == col.TargetType))
            {
                col.Bundles.Add(bundle);
            }
        }
    }

    /// <summary>
    /// Persists this dialog's fields to Symbol (the currently displayed ticker, which may differ
    /// from Ticker when the user has changed the symbol via the Symbol input). Notes is no longer
    /// editable from the Dashboard (that field is now an auto-derived preview of the Notes tab's
    /// latest article, maintained by TickerMetadataNotesCacheSynchronizer), so its current value
    /// is read back and passed through unchanged to avoid clobbering that cache.
    /// </summary>
    private Task PersistStrategyAsync()
    {
        var existingNotes = UserStrategyMetadataRepository.Instance.GetStrategy(Symbol)?.Notes;

        UserStrategyMetadataRepository.Instance.SaveStrategy(
            Symbol,
            Long,
            ExitLong,
            StopLossLong,
            Short,
            ExitShort,
            StopLossShort,
            existingNotes,
            IsLong,
            IsTPLong,
            IsSLLong,
            IsShort,
            IsTPShort,
            IsSLShort,
            Reminder);

        UserStrategyMetadataRepository.Instance.SaveSignalBundles(Symbol, BundledConditions);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_isDisposed) return;

        await PersistStrategyAsync();
        if (_isDisposed) return;

        OnApplyCallback?.Invoke(Long, ExitLong, StopLossLong, Short, ExitShort, StopLossShort, Reminder);
        CloseAction?.Invoke(true);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_isDisposed) return;

        await PersistStrategyAsync();
        if (_isDisposed) return;

        OnApplyCallback?.Invoke(Long, ExitLong, StopLossLong, Short, ExitShort, StopLossShort, Reminder);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseAction?.Invoke(false);
    }

    /// <summary>
    /// Unregisters this instance from the messenger to prevent "Ghost" Sync updates
    /// or re-broadcasts after the dialog has been closed. Also flips <see cref="_isDisposed"/>
    /// so any in-flight async command (Delete, Sync) stops mutating state once resumed.
    /// </summary>
    public void Dispose()
    {
        _isDisposed = true;
        WeakReferenceMessenger.Default.Unregister<TickerSelectedMessage>(this);
    }
}
