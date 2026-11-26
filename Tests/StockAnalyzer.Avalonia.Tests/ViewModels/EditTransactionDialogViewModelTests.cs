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

[Collection("EditTransactionDialogCache State")]
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

    // The suggestion search runs after a 150 ms debounce on a thread-pool thread. A fixed wait of 250 ms left 100 ms of room: when the pool
    // was busy (a full-suite run executes thousands of tests in parallel) the search came later and the test failed once in several runs
    // (2026-11-15, "The collection was empty"). The tests wait for the outcome with a generous budget instead.
    private const int SuggestionBudgetMilliseconds = 10_000;
    private const int PoolBlockersPerProcessor = 4;
    private const int PoolBusyMilliseconds = 600;

    private EditTransactionDialogViewModel CreateViewModelWithTickers()
    {
        _mockMarketDataProvider
            .Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync(new List<string> { "AAPL", "MSFT", "GOOG" });
        return new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
    }

    [Fact]
    public void Initialize_ShouldLoadAvailableTickersFromProvider()
    {
        var vm = CreateViewModelWithTickers();

        // Verify that suggestion search matches appropriately
        vm.Ticker = "A";

        Assert.True(SpinWait.SpinUntil(() => vm.Suggestions.Count > 0, TimeSpan.FromMilliseconds(SuggestionBudgetMilliseconds)),
            "the suggestions did not appear");
        Assert.Single(vm.Suggestions);
        Assert.Equal("AAPL", vm.Suggestions[0]);
    }

    [Fact]
    public void Suggestions_StillAppear_WhenEveryThreadPoolThreadIsBusyForAWhile()
    {
        // Regression of the intermittent failure above: the debounce continuation cannot run while the pool is held, so a fixed wait that is
        // shorter than the holding time (here 250 ms against 600 ms) fails, and a wait for the outcome does not.
        var vm = CreateViewModelWithTickers();
        using var release = new ManualResetEventSlim();
        for (int i = 0; i < Environment.ProcessorCount * PoolBlockersPerProcessor; i++)
        {
            _ = Task.Run(() => release.Wait());
        }

        var releaser = new Thread(() => { Thread.Sleep(PoolBusyMilliseconds); release.Set(); });
        releaser.Start();
        try
        {
            vm.Ticker = "A";

            Assert.True(SpinWait.SpinUntil(() => vm.Suggestions.Count > 0, TimeSpan.FromMilliseconds(SuggestionBudgetMilliseconds)),
                "the suggestions did not appear");
            Assert.Single(vm.Suggestions);
            Assert.Equal("AAPL", vm.Suggestions[0]);
        }
        finally
        {
            release.Set();
            releaser.Join();
        }
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

    private static Transaction CreateStoredJpyExit(Guid id, Guid relatedId, DateTimeOffset executedAt) =>
        new(
            executedAt, TransactionType.ExitLong, "7203", 100m, 2100m, 0m, 15m, "memo", null, null, id, relatedId,
            price: new Money(2100m, CurrencyCode.JPY),
            commission: new Money(15m, CurrencyCode.JPY),
            appliedRate: new ExchangeRate(CurrencyCode.JPY, CurrencyCode.USD, 0.0067m, executedAt.UtcDateTime));

    [Fact]
    public void LoadFrom_ThenSave_KeepsIdRelatedIdCurrencyAndRate()
    {
        var id = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var original = CreateStoredJpyExit(id, relatedId, DateTimeOffset.UtcNow.AddDays(-3));
        // The dialog opens with the last used currency, which is not the currency of the stored transaction.
        EditTransactionDialogViewModel.ResetCache();
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        vm.LoadFrom(original);
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(id, vm.Result.Id);
        Assert.Equal(relatedId, vm.Result.RelatedTransactionId);
        Assert.Equal(CurrencyCode.JPY, vm.Result.Price.Currency);
        Assert.Equal(CurrencyCode.JPY, vm.Result.Commission.Currency);
        Assert.NotNull(vm.Result.AppliedRate);
        Assert.Equal(0.0067m, vm.Result.AppliedRate.Value.Rate);
        Assert.Equal(CurrencyCode.JPY, vm.Result.AppliedRate.Value.BaseCurrency);
        Assert.Equal(CurrencyCode.USD, vm.Result.AppliedRate.Value.QuoteCurrency);
        Assert.Equal("memo", vm.Result.Notes);
        Assert.Equal(15m, vm.Result.Fee);
        Assert.Equal(TransactionType.ExitLong, vm.Result.Type);
    }

    [Fact]
    public void LoadFrom_ThenSave_KeepsTheStoredInstantWhenTheDateIsNotTouched()
    {
        var original = CreateStoredJpyExit(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-3));
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        vm.LoadFrom(original);

        // Kind decides how Save() builds the DateTimeOffset: only a UTC value keeps the stored instant on every machine.
        Assert.Equal(DateTimeKind.Utc, vm.ExecutedAt!.Value.Kind);
        vm.SaveCommand.Execute(null);
        Assert.Equal(original.ExecutedAt, vm.Result!.ExecutedAt);
    }

    [Fact]
    public void LoadFrom_OnACashFlow_KeepsIdCurrencyAndRate()
    {
        var id = Guid.NewGuid();
        var executedAt = DateTimeOffset.UtcNow.AddDays(-5);
        var original = new Transaction(
            executedAt, TransactionType.Deposit, null, 0m, 0m, 50000m, id: id,
            price: new Money(50000m, CurrencyCode.EUR),
            commission: new Money(0m, CurrencyCode.EUR),
            appliedRate: new ExchangeRate(CurrencyCode.EUR, CurrencyCode.USD, 1.08m, executedAt.UtcDateTime));
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        vm.LoadFrom(original);
        vm.SaveCommand.Execute(null);

        Assert.Equal(id, vm.Result!.Id);
        Assert.Null(vm.Result.RelatedTransactionId);
        Assert.Equal(CurrencyCode.EUR, vm.Result.Price.Currency);
        Assert.Equal(1.08m, vm.Result.AppliedRate!.Value.Rate);
        Assert.Equal(50000m, vm.Result.CashAmount);
    }

    [Theory]
    [InlineData(TransactionType.Deposit)]
    [InlineData(TransactionType.Withdrawal)]
    public void SwitchingFromATradeToACashFlow_KeepsTheCurrencyAndRateTheUserChose(TransactionType cashFlow)
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm.SelectedCurrency = CurrencyCode.JPY;
        vm.AppliedRateValue = 0.0067m;

        vm.Type = cashFlow;

        Assert.Equal(CurrencyCode.JPY, vm.SelectedCurrency);
        Assert.Equal(0.0067m, vm.AppliedRateValue);
        Assert.True(vm.IsExchangeRateVisible);
        Assert.True(vm.IsExchangeRateEnabled);
        Assert.True(vm.IsDepositOrWithdrawal);
        Assert.False(vm.IsBuyOrSell);
    }

    [Fact]
    public void ACashFlowInAForeignCurrency_CanBeSavedOnlyOnceTheRateIsEntered()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm.ExecutedAt = DateTime.Today;
        vm.Type = TransactionType.Deposit;
        vm.CashAmount = 100m;
        vm.SelectedCurrency = CurrencyCode.JPY;   // the rate box is empty

        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.IsExchangeRateVisible);   // the field that unblocks Save is reachable (it is no longer tied to the trade panel)

        vm.AppliedRateValue = 0.0067m;

        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.Equal(CurrencyCode.JPY, vm.Result!.Price.Currency);
        Assert.Equal(0.0067m, vm.Result.AppliedRate!.Value.Rate);
    }

    [Theory]
    [InlineData(TransactionType.Deposit)]
    [InlineData(TransactionType.Withdrawal)]
    public void ACashFlow_KeepsItsNotes(TransactionType cashFlow)
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm.ExecutedAt = DateTime.Today;
        vm.Type = cashFlow;
        vm.CashAmount = 100m;
        vm.Notes = "salary";

        vm.SaveCommand.Execute(null);

        Assert.Equal("salary", vm.Result!.Notes);
    }

    [Theory]
    [InlineData(TransactionType.Deposit)]
    [InlineData(TransactionType.Withdrawal)]
    public void SwitchingFromATradeToACashFlow_SavesNoFeeAndNoTradeFields(TransactionType cashFlow)
    {
        // The Fee box is hidden for a cash flow, so a value typed while the type was still a trade must not travel into the saved row:
        // the engine ignores it today, and a later rule that honours the fee would otherwise move cash for a number the user cannot see.
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm.ExecutedAt = DateTime.Today;
        vm.Ticker = "AAPL";
        vm.Quantity = 3m;
        vm.PricePerUnit = 5m;
        vm.Fee = 12.34m;
        vm.TargetPrice = 9m;
        vm.StopLoss = 1m;
        vm.Type = cashFlow;
        vm.CashAmount = 100m;

        vm.SaveCommand.Execute(null);

        var result = vm.Result!;
        Assert.Equal(0m, result.Fee);
        Assert.Equal(0m, result.Commission.Amount);
        Assert.Equal(vm.SelectedCurrency, result.Commission.Currency);
        Assert.Null(result.Ticker);
        Assert.Equal(0m, result.Quantity);
        Assert.Equal(0m, result.PricePerUnit);
        Assert.Null(result.TargetPrice);
        Assert.Null(result.StopLoss);
        Assert.Equal(100m, result.CashAmount);
    }

    [Theory]
    [InlineData(TransactionType.Deposit)]
    [InlineData(TransactionType.Withdrawal)]
    public void EditingAStoredCashFlowThatCarriesAFee_SavesNoFee(TransactionType cashFlow)
    {
        // A row written by an older version may hold a fee on a cash flow (the engine never used it). Saving after an edit clears it.
        var executedAt = DateTimeOffset.UtcNow.AddDays(-4);
        var original = new Transaction(
            executedAt, cashFlow, null, 0m, 0m, 100m, 7m, null, null, null, Guid.NewGuid(), null,
            price: new Money(100m, CurrencyCode.USD), commission: new Money(7m, CurrencyCode.USD));
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        vm.LoadFrom(original);
        vm.SaveCommand.Execute(null);

        Assert.Equal(0m, vm.Result!.Fee);
        Assert.Equal(0m, vm.Result.Commission.Amount);
        Assert.Equal(original.Id, vm.Result.Id);
    }

    [Fact]
    public void TheMinimumsOfTheNumericBoxes_AreTheValuesOfPortfolioInputLimits()
    {
        var vm = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);

        Assert.Equal(PortfolioInputLimits.MinTradeQuantity, vm.MinTradeQuantity);
        Assert.Equal(PortfolioInputLimits.MinCashAmount, vm.MinCashAmount);
        Assert.Equal(PortfolioInputLimits.MinExchangeRate, vm.MinExchangeRate);
        Assert.Equal(PortfolioInputLimits.MinNonNegativeAmount, vm.MinNonNegativeAmount);
    }

    [Fact]
    public void Save_InAddMode_GeneratesANewIdEachTimeAndNoRelatedId()
    {
        var vm1 = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm1.Ticker = "AAPL";
        vm1.Quantity = 1m;
        vm1.PricePerUnit = 10m;
        vm1.SaveCommand.Execute(null);
        var vm2 = new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
        vm2.Ticker = "AAPL";
        vm2.Quantity = 1m;
        vm2.PricePerUnit = 10m;
        vm2.SaveCommand.Execute(null);

        Assert.NotEqual(Guid.Empty, vm1.Result!.Id);
        Assert.NotEqual(vm1.Result.Id, vm2.Result!.Id);
        Assert.Null(vm1.Result.RelatedTransactionId);
    }
}
