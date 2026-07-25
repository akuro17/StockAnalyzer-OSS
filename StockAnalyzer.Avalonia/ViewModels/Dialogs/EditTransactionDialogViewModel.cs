using System;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class EditTransactionDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILocalizationService _localizationService;
    private readonly SearchResultState _searchResultState = new();
    private IReadOnlyList<string>? _masterTickers;
    private CancellationTokenSource? _searchCts;
    private bool _isDisposed;

    [ObservableProperty]
    private string? _selectedSuggestion;

    public ObservableCollection<string> Suggestions { get; } = new();
    private TransactionType _type;
    private void UpdateType(TransactionType value)
    {
        _type = value;
        _selectedTypeMenuItem = TransactionTypeItems?.Find(t => t.Type == value);
        IsCurrencySelectionEnabled = (value != TransactionType.ExitLong && value != TransactionType.ExitShort);
        
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(SelectedTypeMenuItem));
        OnPropertyChanged(nameof(IsBuyOrSell));
        OnPropertyChanged(nameof(IsDepositOrWithdrawal));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public TransactionType Type
    {
        get => _type;
        set
        {
            if (_type != value)
            {
                UpdateType(value);
            }
        }
    }

    private static readonly TransactionType[] CachedTransactionTypes = new[] { TransactionType.Long, TransactionType.Short, TransactionType.ExitLong, TransactionType.ExitShort, TransactionType.Deposit, TransactionType.Withdrawal };
    public TransactionType[] TransactionTypes => CachedTransactionTypes;

    public List<TransactionTypeMenuItem> TransactionTypeItems { get; private set; } = new();

    private void InitializeTypes()
    {
        TransactionTypeItems = new List<TransactionTypeMenuItem>
        {
            new(TransactionType.Long, _localizationService.GetString("Enum_TransactionType_Long") ?? "Long"),
            new(TransactionType.ExitLong, _localizationService.GetString("Enum_TransactionType_ExitLong") ?? "Exit Long"),
            new(null, "──────────"), // Separator
            new(TransactionType.Short, _localizationService.GetString("Enum_TransactionType_Short") ?? "Short"),
            new(TransactionType.ExitShort, _localizationService.GetString("Enum_TransactionType_ExitShort") ?? "Exit Short"),
            new(null, "──────────"), // Separator
            new(TransactionType.Deposit, _localizationService.GetString("Enum_TransactionType_Deposit") ?? "Deposit"),
            new(TransactionType.Withdrawal, _localizationService.GetString("Enum_TransactionType_Withdrawal") ?? "Withdrawal")
        };
    }

    private TransactionTypeMenuItem? _selectedTypeMenuItem;
    public TransactionTypeMenuItem? SelectedTypeMenuItem
    {
        get => _selectedTypeMenuItem;
        set
        {
            if (value != null && !value.IsSeparator && value.Type.HasValue)
            {
                if (SetProperty(ref _selectedTypeMenuItem, value))
                {
                    UpdateType(value.Type.Value);
                }
            }
            else
            {
                OnPropertyChanged(nameof(SelectedTypeMenuItem));
            }
        }
    }

    
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _ticker = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal _quantity;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal _pricePerUnit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal _fee;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal _cashAmount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private DateTime? _executedAt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _notes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal? _targetPrice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal? _stopLoss;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private CurrencyCode _selectedCurrency = CurrencyCode.USD;

    partial void OnSelectedCurrencyChanged(CurrencyCode value)
    {
        OnPropertyChanged(nameof(IsExchangeRateVisible));
        OnPropertyChanged(nameof(IsExchangeRateEnabled));
        if (value == CurrencyCode.USD)
        {
            AppliedRateValue = null;
        }
        SaveCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal? _appliedRateValue;

    public bool IsExchangeRateVisible => SelectedCurrency != CurrencyCode.USD;
    public bool IsExchangeRateEnabled => SelectedCurrency != CurrencyCode.USD && IsCurrencySelectionEnabled;

    [ObservableProperty]
    private bool _isCurrencySelectionEnabled = true;

    partial void OnIsCurrencySelectionEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExchangeRateEnabled));
    }

    private static CurrencyCode _lastSelectedCurrency = CurrencyCode.USD;
    private static decimal? _lastAppliedRateValue;

    public static void ResetCache()
    {
        _lastSelectedCurrency = CurrencyCode.USD;
        _lastAppliedRateValue = null;
    }

    private static readonly CurrencyCode[] CachedCurrencies = new[] { CurrencyCode.USD, CurrencyCode.JPY, CurrencyCode.EUR };
    public CurrencyCode[] Currencies => CachedCurrencies;

    public bool IsBuyOrSell => Type == TransactionType.Long || Type == TransactionType.Short || Type == TransactionType.ExitLong || Type == TransactionType.ExitShort;
    public bool IsDepositOrWithdrawal => Type == TransactionType.Deposit || Type == TransactionType.Withdrawal;

    public Transaction? Result { get; private set; }

    public EditTransactionDialogViewModel(IMarketDataProvider marketDataProvider, IDispatcherService dispatcherService, ILocalizationService localizationService)
    {
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        InitializeTypes();
        Type = TransactionType.Long;
        ExecutedAt = DateTime.UtcNow;
        SelectedCurrency = _lastSelectedCurrency;
        AppliedRateValue = _lastAppliedRateValue;
        InitializeAsync();
    }

    public EditTransactionDialogViewModel(IMarketDataProvider marketDataProvider, IDispatcherService dispatcherService, TransactionType type, ILocalizationService localizationService)
    {
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        InitializeTypes();
        Type = type;
        ExecutedAt = DateTime.UtcNow;
        if (type == TransactionType.ExitLong || type == TransactionType.ExitShort)
        {
            IsCurrencySelectionEnabled = false;
        }
        else
        {
            SelectedCurrency = _lastSelectedCurrency;
            AppliedRateValue = _lastAppliedRateValue;
        }
        InitializeAsync();
    }

    public EditTransactionDialogViewModel(IMarketDataProvider marketDataProvider, IDispatcherService dispatcherService, Transaction original, ILocalizationService localizationService)
    {
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        InitializeTypes();
        Type = original.Type;
        Ticker = original.Ticker ?? string.Empty;
        Quantity = original.Quantity;
        PricePerUnit = original.PricePerUnit;
        Fee = original.Fee;
        CashAmount = original.CashAmount;
        ExecutedAt = original.ExecutedAt.DateTime;
        Notes = original.Notes;
        TargetPrice = original.TargetPrice;
        StopLoss = original.StopLoss;
        SelectedCurrency = original.Price.Currency;
        AppliedRateValue = original.AppliedRate?.Rate;
        if (original.Type == TransactionType.ExitLong || original.Type == TransactionType.ExitShort)
        {
            IsCurrencySelectionEnabled = false;
        }
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        try
        {
            _masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
        }
        catch
        {
            _masterTickers = Array.Empty<string>();
        }
    }

    partial void OnTickerChanged(string value)
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

        Task.Factory.StartNew(static async state =>
        {
            var vm = (EditTransactionDialogViewModel)state!;
            var token = vm._searchCts?.Token ?? CancellationToken.None;
            try
            {
                await Task.Delay(150, token);
                vm.UpdateSearch(vm.Ticker, token);
            }
            catch (TaskCanceledException)
            {
            }
        }, this, token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
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

        string[] matches = ArrayPool<string>.Shared.Rent(10);
        int matchCount = 0;

        try
        {
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
                    if (matchCount < 10)
                    {
                        matches[matchCount++] = ticker;
                    }
                }
            }

            _searchResultState.Vm = this;
            _searchResultState.Matches = matches;
            _searchResultState.MatchCount = matchCount;
            _searchResultState.Ct = ct;

            _dispatcherService.Post(static state =>
            {
                try
                {
                    if (state.Ct.IsCancellationRequested) return;

                    state.Vm.Suggestions.Clear();
                    for (int i = 0; i < state.MatchCount; i++)
                    {
                        state.Vm.Suggestions.Add(state.Matches[i]);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(state.Matches, clearArray: true);
                }
            }, _searchResultState);
        }
        catch
        {
            ArrayPool<string>.Shared.Return(matches, clearArray: true);
            throw;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        ExchangeRate? rate = null;
        if (SelectedCurrency != CurrencyCode.USD && AppliedRateValue.HasValue)
        {
            rate = new ExchangeRate(SelectedCurrency, CurrencyCode.USD, AppliedRateValue.Value, ExecutedAt.GetValueOrDefault(DateTime.UtcNow));
        }

        if (Type != TransactionType.ExitLong && Type != TransactionType.ExitShort)
        {
            _lastSelectedCurrency = SelectedCurrency;
            _lastAppliedRateValue = AppliedRateValue;
        }

        Result = new Transaction(
            new DateTimeOffset(ExecutedAt.GetValueOrDefault(DateTime.UtcNow)),
            Type,
            IsBuyOrSell ? Ticker.Trim().ToUpperInvariant() : null,
            IsBuyOrSell ? Quantity : 0m,
            IsBuyOrSell ? PricePerUnit : 0m,
            IsDepositOrWithdrawal ? CashAmount : 0m,
            IsBuyOrSell ? Fee : 0m,
            IsBuyOrSell ? Notes : null,
            IsBuyOrSell ? TargetPrice : null,
            IsBuyOrSell ? StopLoss : null,
            price: new Money(IsBuyOrSell ? PricePerUnit : (IsDepositOrWithdrawal ? CashAmount : 0m), SelectedCurrency),
            commission: new Money(IsBuyOrSell ? Fee : 0m, SelectedCurrency),
            appliedRate: rate
        );
    }

    private bool CanSave()
    {
        if (_isDisposed) return false;
        if (ExecutedAt == null || ExecutedAt.Value.Date > DateTime.Today) return false;

        if (IsBuyOrSell)
        {
            if (string.IsNullOrWhiteSpace(Ticker)) return false;
            if (Quantity <= 0) return false;
            if (PricePerUnit < 0) return false;
            if (Fee < 0) return false;
            if (TargetPrice != null && TargetPrice.Value < 0) return false;
            if (StopLoss != null && StopLoss.Value < 0) return false;
        }
        else if (IsDepositOrWithdrawal)
        {
            if (CashAmount <= 0) return false;
        }

        if (SelectedCurrency != CurrencyCode.USD)
        {
            if (AppliedRateValue == null || AppliedRateValue.Value <= 0) return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private class SearchResultState
    {
        public EditTransactionDialogViewModel Vm { get; set; } = null!;
        public string[] Matches { get; set; } = null!;
        public int MatchCount { get; set; }
        public CancellationToken Ct { get; set; }
    }
}

public class TransactionTypeMenuItem
{
    public TransactionType? Type { get; }
    public string DisplayText { get; }
    public bool IsSeparator => Type == null;

    public TransactionTypeMenuItem(TransactionType? type, string displayText)
    {
        Type = type;
        DisplayText = displayText;
    }
}
