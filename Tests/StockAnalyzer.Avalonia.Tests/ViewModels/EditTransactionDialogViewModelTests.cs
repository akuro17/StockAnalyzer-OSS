using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Tests.Services;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class EditTransactionDialogViewModelTests
{
    private readonly Mock<IMarketDataProvider> _mockMarketDataProvider;
    private readonly IDispatcherService _dispatcherService;

    private readonly Mock<ILocalizationService> _mockLocalizationService;

    public EditTransactionDialogViewModelTests()
    {
        _mockMarketDataProvider = new Mock<IMarketDataProvider>();
        _dispatcherService = new SynchronousDispatcherService();
        _mockLocalizationService = new Mock<ILocalizationService>();
        EditTransactionDialogViewModel.ResetCache();
    }

    [Fact]
    public void Constructor_WithNullMarketDataProvider_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new EditTransactionDialogViewModel(null!, _dispatcherService, _mockLocalizationService.Object));
    }

    [Fact]
    public void Constructor_WithNullDispatcherService_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, null!, _mockLocalizationService.Object));
    }

    [Fact]
    public void Constructor_WithNullLocalizationService_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, null!));
    }

    [Fact]
    public async Task Initialize_ShouldLoadAvailableTickersFromProvider()
    {
        var tickers = new List<string> { "AAPL", "MSFT", "GOOG" };
        _mockMarketDataProvider
            .Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync(tickers);

        // Act
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        
        // Wait a short moment for the async Initialization method to complete
        await Task.Delay(50);

        // Verify that suggestion search matches appropriately
        vm.Ticker = "A";
        
        // Wait a short moment for the debounce/post Task.Delay inside OnTickerChanged
        await Task.Delay(250);

        Assert.Single(vm.Suggestions);
        Assert.Equal("AAPL", vm.Suggestions[0]);
    }

    [Fact]
    public void Dispose_ShouldCancelSearchAndPreventSave()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        
        vm.Dispose();

        vm.ExecutedAt = DateTime.Today;
        vm.Type = TransactionType.Deposit;
        vm.CashAmount = 100m;

        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void TransactionTypes_ShouldIncludeDepositAndWithdrawal()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        Assert.Contains(TransactionType.Deposit, vm.TransactionTypes);
        Assert.Contains(TransactionType.Withdrawal, vm.TransactionTypes);
    }

    [Fact]
    public void SaveCommand_ShouldBeExecutable_WhenValidInputProvided()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        // Initially disabled due to default invalid inputs (e.g. empty Ticker, 0 Quantity/Price)
        Assert.False(vm.SaveCommand.CanExecute(null));

        // Act - set valid inputs
        vm.Ticker = "AA";
        vm.Quantity = 10m;
        vm.PricePerUnit = 60m;

        // Assert
        Assert.True(vm.SaveCommand.CanExecute(null));
        
        vm.SaveCommand.Execute(null);
        Assert.NotNull(vm.Result);
        Assert.Equal("AA", vm.Result.Ticker);
        Assert.Equal(10m, vm.Result.Quantity);
        Assert.Equal(60m, vm.Result.PricePerUnit);
    }

    [Fact]
    public void DefaultCurrency_ShouldBeUSD()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        Assert.Equal(CurrencyCode.USD, vm.SelectedCurrency);
        Assert.False(vm.IsExchangeRateVisible);
        Assert.False(vm.IsExchangeRateEnabled);
    }

    [Fact]
    public void ChangingCurrencyToNonUSD_ShouldEnableExchangeRate()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        
        vm.SelectedCurrency = CurrencyCode.JPY;
        
        Assert.True(vm.IsExchangeRateVisible);
        Assert.True(vm.IsExchangeRateEnabled);
    }

    [Fact]
    public void Save_WithNonUSD_ShouldIncludeAppliedRateInTransaction()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        
        vm.Ticker = "7203";
        vm.Quantity = 100m;
        vm.PricePerUnit = 2000m;
        vm.SelectedCurrency = CurrencyCode.JPY;
        vm.AppliedRateValue = 0.0067m;

        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(CurrencyCode.JPY, vm.Result.Price.Currency);
        Assert.NotNull(vm.Result.AppliedRate);
        Assert.Equal(0.0067m, vm.Result.AppliedRate.Value.Rate);
        Assert.Equal(CurrencyCode.JPY, vm.Result.AppliedRate.Value.BaseCurrency);
        Assert.Equal(CurrencyCode.USD, vm.Result.AppliedRate.Value.QuoteCurrency);
    }

    [Fact]
    public void Save_ShouldPersistStateForNextDialog()
    {
        var vm1 = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm1.Ticker = "AAPL";
        vm1.Quantity = 10m;
        vm1.PricePerUnit = 150m;
        vm1.SelectedCurrency = CurrencyCode.EUR;
        vm1.AppliedRateValue = 1.1m;
        
        vm1.SaveCommand.Execute(null);

        // Next new dialog should restore the saved values
        var vm2 = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        Assert.Equal(CurrencyCode.EUR, vm2.SelectedCurrency);
        Assert.Equal(1.1m, vm2.AppliedRateValue);
    }

    [Fact]
    public void Constructor_WithExitType_ShouldDisableCurrencySelection()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, TransactionType.ExitLong, _mockLocalizationService.Object);
        Assert.False(vm.IsCurrencySelectionEnabled);
        
        vm.SelectedCurrency = CurrencyCode.JPY;
        Assert.False(vm.IsExchangeRateEnabled); // Exchange rate field should also be disabled even if JPY is selected
    }
}
