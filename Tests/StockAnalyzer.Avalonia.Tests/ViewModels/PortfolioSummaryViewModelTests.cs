using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Xunit;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using Microsoft.Extensions.Logging;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("EditTransactionDialogCache State")]
public class PortfolioSummaryViewModelTests
{
    private readonly Mock<IPortfolioManager> _mockPortfolioManager;
    private readonly Mock<IMarketDataProvider> _mockMarketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly Mock<IMessenger> _mockMessenger;
    private readonly Mock<ILogger<PortfolioSummaryViewModel>> _mockLogger;
    private readonly Mock<IWatchlistManager> _mockWatchlistManager;
    private readonly Mock<StockAnalyzer.Avalonia.Services.IDialogService> _mockDialogService;
    private readonly Mock<IDesignTimeDetector> _mockDesignTimeDetector;
    private readonly Mock<ILocalizationService> _mockLocalizationService;
    private readonly Func<EditTransactionDialogViewModel> _dialogViewModelFactory;

    public PortfolioSummaryViewModelTests()
    {
        _mockPortfolioManager = new Mock<IPortfolioManager>();
        _mockMarketDataProvider = new Mock<IMarketDataProvider>();
        _dispatcherService = new SynchronousDispatcherService();
        _mockMessenger = new Mock<IMessenger>();
        _mockLogger = new Mock<ILogger<PortfolioSummaryViewModel>>();
        _mockWatchlistManager = new Mock<IWatchlistManager>();
        _mockDialogService = new Mock<StockAnalyzer.Avalonia.Services.IDialogService>();
        _mockDesignTimeDetector = new Mock<IDesignTimeDetector>();
        _mockLocalizationService = new Mock<ILocalizationService>();
        _dialogViewModelFactory = () => new EditTransactionDialogViewModel(_mockMarketDataProvider.Object, _dispatcherService, _mockLocalizationService.Object);
    }

    [Fact]
    public void BuildGroups_ShouldCreateRootNode_AtStartup()
    {
        var profiles = new List<WatchlistProfile>
        {
            new WatchlistProfile(Guid.NewGuid(), "Tech Portfolio", IndicatorColor.Bullish, isPortfolio: true),
            new WatchlistProfile(Guid.NewGuid(), "Non-Portfolio Watchlist", IndicatorColor.Bullish, isPortfolio: false)
        };

        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(profiles);

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        Assert.Single(viewModel.Groups);
        var rootNode = viewModel.Groups.First();
        Assert.Equal("Portfolios", rootNode.Name);
        Assert.True(rootNode.IsAggregate);

        // Only profiles with IsPortfolio == true should be children of Root
        Assert.Single(rootNode.Children);
        Assert.Equal("Tech Portfolio", rootNode.Children.First().Name);
        Assert.False(rootNode.Children.First().IsAggregate);
    }

    [Fact]
    public void OnWatchlistsChanged_ShouldDiffSyncNodesInPlace()
    {
        var activeProfiles = new List<WatchlistProfile>
        {
            new WatchlistProfile(Guid.NewGuid(), "Tech Portfolio", IndicatorColor.Bullish, isPortfolio: true)
        };

        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(activeProfiles);

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        var rootNode = viewModel.Groups.First();
        Assert.Single(rootNode.Children);

        // Trigger change: add another portfolio and change name of first
        var updatedProfileId = activeProfiles[0].Id;
        var updatedProfiles = new List<WatchlistProfile>
        {
            new WatchlistProfile(updatedProfileId, "Renamed Tech Portfolio", IndicatorColor.Bullish, isPortfolio: true),
            new WatchlistProfile(Guid.NewGuid(), "Second Portfolio", IndicatorColor.Bullish, isPortfolio: true)
        };

        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(updatedProfiles);

        // Trigger event
        _mockWatchlistManager.Raise(w => w.WatchlistsChanged += null, EventArgs.Empty);

        Assert.Equal(2, rootNode.Children.Count);
        Assert.Equal("Renamed Tech Portfolio", rootNode.Children.First(n => n.NodeId == updatedProfileId).Name);
        Assert.Contains(rootNode.Children, n => n.Name == "Second Portfolio");
    }

    [Fact]
    public async Task AddTransactionCommand_ShouldShowDialog_AndRebuildPortfolio_WhenConfirmed()
    {
        var activeProfiles = new List<WatchlistProfile>();
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(activeProfiles);

        var testPortfolio = new Portfolio(100000m, history: new List<Transaction>());
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis))
            .Returns(testPortfolio);

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        var expectedTransaction = new Transaction(
            DateTimeOffset.UtcNow,
            TransactionType.Long,
            "AAPL",
            10,
            150m,
            1500m
        );

        _mockDialogService.Setup(d => d.ShowEditTransactionDialogAsync(It.IsAny<EditTransactionDialogViewModel>()))
            .ReturnsAsync(expectedTransaction);

        // Act
        await viewModel.AddTransactionCommand.ExecuteAsync("Long");

        // Assert
        _mockDialogService.Verify(d => d.ShowEditTransactionDialogAsync(It.IsAny<EditTransactionDialogViewModel>()), Times.Once);
        _mockPortfolioManager.Verify(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.Is<IReadOnlyList<Transaction>>(list => list.Contains(expectedTransaction)), PortfolioAccountingDefaults.CostBasis), Times.Once);
        _mockPortfolioManager.Verify(p => p.SavePortfolioAsync(It.IsAny<Portfolio>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTransactionsCommand_ShouldShowConfirmation_AndRemoveTransactions_WhenConfirmed()
    {
        // Arrange
        var activeProfiles = new List<WatchlistProfile>();
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(activeProfiles);

        var t1 = new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Deposit, null, 0, 0, 1000m);
        var t2 = new Transaction(DateTimeOffset.UtcNow, TransactionType.Long, "AAPL", 10, 150m, 1500m);

        var testPortfolio = new Portfolio(1000m, history: new List<Transaction> { t1, t2 });
        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(testPortfolio);

        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis))
            .Returns((decimal cash, IReadOnlyList<Transaction> hist, CostBasisMethod method) => new Portfolio(cash, history: hist.ToList()));

        _mockDialogService.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        // Act
        await viewModel.DeleteTransactionsCommand.ExecuteAsync(new List<Transaction> { t1 });

        // Assert
        _mockDialogService.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockPortfolioManager.Verify(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.Is<IReadOnlyList<Transaction>>(list => !list.Contains(t1) && list.Contains(t2)), PortfolioAccountingDefaults.CostBasis), Times.Once);
        _mockPortfolioManager.Verify(p => p.SavePortfolioAsync(It.IsAny<Portfolio>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    private PortfolioSummaryViewModel CreateViewModelForAddCommand()
    {
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis))
            .Returns(new Portfolio(0m, history: new List<Transaction>()));
        return new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("Foo")]
    public async Task AddTransactionCommand_WithATypeThatIsNoMember_DoesNotOpenTheDialog(string type)
    {
        var viewModel = CreateViewModelForAddCommand();

        await viewModel.AddTransactionCommand.ExecuteAsync(type);

        _mockDialogService.Verify(d => d.ShowEditTransactionDialogAsync(It.IsAny<EditTransactionDialogViewModel>()), Times.Never);
    }

    [Theory]
    [InlineData("Deposit", TransactionType.Deposit)]
    [InlineData("Withdrawal", TransactionType.Withdrawal)]
    [InlineData("Long", TransactionType.Long)]
    [InlineData("Buy", TransactionType.Long)]   // an alias of a defined member stays accepted
    public async Task AddTransactionCommand_WithADefinedType_OpensTheDialogOfThatType(string type, TransactionType expected)
    {
        var viewModel = CreateViewModelForAddCommand();
        TransactionType? opened = null;
        _mockDialogService.Setup(d => d.ShowEditTransactionDialogAsync(It.IsAny<EditTransactionDialogViewModel>()))
            .Returns<EditTransactionDialogViewModel>(dialog =>
            {
                opened = dialog.Type;
                return Task.FromResult<Transaction?>(null);
            });

        await viewModel.AddTransactionCommand.ExecuteAsync(type);

        Assert.Equal(expected, opened);
    }

    [Fact]
    public async Task EditTransactionCommand_ShouldOpenTheDialogWithCurrencyAndRate_AndKeepIdAndRelatedId()
    {
        // Arrange: a stored JPY transaction; the dialog's own default currency (USD) differs from it.
        EditTransactionDialogViewModel.ResetCache();
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var id = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var executedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var original = new Transaction(
            executedAt, TransactionType.ExitLong, "7203", 100m, 2100m, 0m, 15m, "memo", null, null, id, relatedId,
            price: new Money(2100m, CurrencyCode.JPY),
            commission: new Money(15m, CurrencyCode.JPY),
            appliedRate: new ExchangeRate(CurrencyCode.JPY, CurrencyCode.USD, 0.0067m, executedAt.UtcDateTime));

        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Portfolio(1000m, history: new List<Transaction> { original }));
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis))
            .Returns((decimal cash, IReadOnlyList<Transaction> hist, CostBasisMethod method) => new Portfolio(cash, history: hist.ToList()));

        CurrencyCode? currencyShownInDialog = null;
        decimal? rateShownInDialog = null;
        _mockDialogService.Setup(d => d.ShowEditTransactionDialogAsync(It.IsAny<EditTransactionDialogViewModel>()))
            .Returns<EditTransactionDialogViewModel>(dialog =>
            {
                currencyShownInDialog = dialog.SelectedCurrency;
                rateShownInDialog = dialog.AppliedRateValue;
                dialog.SaveCommand.Execute(null);
                return Task.FromResult<Transaction?>(dialog.Result);
            });

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        // Act
        await viewModel.EditTransactionCommand.ExecuteAsync(original);

        // Assert: the dialog showed the stored currency and rate, and the saved row replaced the old one under the same identity.
        Assert.Equal(CurrencyCode.JPY, currencyShownInDialog);
        Assert.Equal(0.0067m, rateShownInDialog);
        _mockPortfolioManager.Verify(p => p.RebuildPortfolio(
            It.IsAny<decimal>(),
            It.Is<IReadOnlyList<Transaction>>(list =>
                list.Count == 1
                && !ReferenceEquals(list[0], original) // the row saved by the dialog, not the stored one read for the first evaluation
                && list[0].Id == id
                && list[0].RelatedTransactionId == relatedId
                && list[0].Price.Currency == CurrencyCode.JPY
                && list[0].AppliedRate.HasValue
                && list[0].AppliedRate.Value.Rate == 0.0067m),
            PortfolioAccountingDefaults.CostBasis),
            Times.Once);
        _mockPortfolioManager.Verify(p => p.SavePortfolioAsync(It.IsAny<Portfolio>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Evaluate_ShouldUseLastEnteredExchangeRatesFromHistory()
    {
        // Arrange
        var activeProfiles = new List<WatchlistProfile>();
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(activeProfiles);

        var now = DateTime.UtcNow;
        var t1 = new Transaction(
            now.AddDays(-2), TransactionType.Buy, "AAPL", 10, 150m, 1500m,
            appliedRate: new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 140m, now.AddDays(-2))
        );
        var t2 = new Transaction(
            now.AddDays(-1), TransactionType.Buy, "MSFT", 5, 200m, 1000m,
            appliedRate: new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 145m, now.AddDays(-1))
        );

        var testPortfolio = new Portfolio(1000m, history: new List<Transaction> { t1, t2 });
        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(testPortfolio);
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis))
            .Returns((decimal cash, IReadOnlyList<Transaction> hist, CostBasisMethod method) => new Portfolio(cash, history: hist.ToList()));

        IReadOnlyDictionary<CurrencyCode, ExchangeRate> capturedRates = null;
        _mockPortfolioManager.Setup(p => p.Evaluate(
                It.IsAny<Portfolio>(),
                It.IsAny<IReadOnlyDictionary<string, decimal>>(),
                It.IsAny<IReadOnlyDictionary<CurrencyCode, ExchangeRate>>(),
                It.IsAny<CurrencyCode>()))
            .Callback<Portfolio, IReadOnlyDictionary<string, decimal>, IReadOnlyDictionary<CurrencyCode, ExchangeRate>, CurrencyCode>(
                (p, prices, rates, baseCurrency) => capturedRates = rates)
            .Returns(new PortfolioEvaluationResult(new PortfolioMetrics(0, 0, 0, 0, 0), new Dictionary<string, decimal>(), new Dictionary<string, decimal>()));

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        // Act
        var method = typeof(PortfolioSummaryViewModel).GetMethod("EvaluateAndSelectNodeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method.Invoke(viewModel, new object[] { new TreeNode(Guid.NewGuid(), "Root", true) });

        // Assert
        Assert.NotNull(capturedRates);
        Assert.True(capturedRates.ContainsKey(CurrencyCode.USD));
        Assert.Equal(145m, capturedRates[CurrencyCode.USD].Rate);
    }

    private async Task<PortfolioSummaryViewModel> EvaluateWithResultAsync(Portfolio portfolio, IReadOnlyList<CurrencyCode>? unconverted)
    {
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        _mockMarketDataProvider.Setup(m => m.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, decimal>());
        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(portfolio);
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis)).Returns(portfolio);
        _mockPortfolioManager.Setup(p => p.Evaluate(
                It.IsAny<Portfolio>(),
                It.IsAny<IReadOnlyDictionary<string, decimal>>(),
                It.IsAny<IReadOnlyDictionary<CurrencyCode, ExchangeRate>>(),
                It.IsAny<CurrencyCode>()))
            .Returns(new PortfolioEvaluationResult(
                new PortfolioMetrics(0, 0, 0, 0, 0),
                new Dictionary<string, decimal>(),
                new Dictionary<string, decimal>(),
                unconverted));

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        await SelectRootAndRefreshAsync(viewModel);
        return viewModel;
    }

    // Public surface only: selecting a node starts the evaluation, the forced refresh waits for it (the refresh semaphore serializes both).
    private static async Task SelectRootAndRefreshAsync(PortfolioSummaryViewModel viewModel)
    {
        viewModel.SelectedNode = new TreeNode(Guid.NewGuid(), "Root", true);
        await viewModel.RefreshDataAsync(force: true);
    }

    private static Portfolio PortfolioWithUsdAndEurPositions() => new Portfolio(
        0m,
        new Dictionary<string, Position>
        {
            { "AAPL", new Position("AAPL", 1m, 100m, false, new Money(100m, CurrencyCode.USD)) },
            { "SAP", new Position("SAP", 1m, 100m, false, new Money(100m, CurrencyCode.EUR)) },
            { "7203", new Position("7203", 1m, 100m, false, new Money(100m, CurrencyCode.JPY)) },
        });

    // History for the transaction-list tests: two cash flows and two trades of different tickers.
    private static (Portfolio Portfolio, Transaction Deposit, Transaction Withdrawal, Transaction Aapl, Transaction Msft) CashFlowsAndTwoTrades()
    {
        var now = DateTimeOffset.UtcNow;
        var deposit = new Transaction(now.AddDays(-4), TransactionType.Deposit, null, 0m, 0m, 1000m);
        var withdrawal = new Transaction(now.AddDays(-3), TransactionType.Withdrawal, null, 0m, 0m, 100m);
        var aapl = new Transaction(now.AddDays(-2), TransactionType.Long, "AAPL", 1m, 10m, 0m);
        var msft = new Transaction(now.AddDays(-1), TransactionType.Long, "MSFT", 1m, 20m, 0m);
        return (new Portfolio(0m, history: new List<Transaction> { deposit, withdrawal, aapl, msft }), deposit, withdrawal, aapl, msft);
    }

    // Selects either the root node or the first profile node, then waits for the evaluation; returns the rows of the Transactions list.
    private async Task<IReadOnlyList<Transaction>> TransactionRowsAsync(Portfolio portfolio, WatchlistProfile profile, bool selectProfile)
    {
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { profile });
        _mockWatchlistManager.Setup(w => w.GetProfileById(profile.Id)).Returns(profile);
        _mockMarketDataProvider.Setup(m => m.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, decimal>());
        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(portfolio);
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis)).Returns(portfolio);
        _mockPortfolioManager.Setup(p => p.Evaluate(
                It.IsAny<Portfolio>(),
                It.IsAny<IReadOnlyDictionary<string, decimal>>(),
                It.IsAny<IReadOnlyDictionary<CurrencyCode, ExchangeRate>>(),
                It.IsAny<CurrencyCode>()))
            .Returns(new PortfolioEvaluationResult(new PortfolioMetrics(0, 0, 0, 0, 0), new Dictionary<string, decimal>(), new Dictionary<string, decimal>()));

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object);

        viewModel.SelectedNode = selectProfile
            ? new TreeNode(profile.Id, profile.Name, isAggregate: false)
            : new TreeNode(Guid.NewGuid(), "Root", isAggregate: true);
        await viewModel.RefreshDataAsync(force: true);
        return viewModel.Transactions.ToList();
    }

    [Fact]
    public async Task Transactions_OnTheRootNode_ShowEveryRow()
    {
        var (portfolio, deposit, withdrawal, aapl, msft) = CashFlowsAndTwoTrades();
        var profile = new WatchlistProfile(Guid.NewGuid(), "Tech", IndicatorColor.Bullish, isPortfolio: true,
            items: new[] { new WatchlistItem("AAPL", DateTimeOffset.UtcNow) });

        var rows = await TransactionRowsAsync(portfolio, profile, selectProfile: false);

        Assert.Equal(new[] { deposit, withdrawal, aapl, msft }, rows);
    }

    [Fact]
    public async Task Transactions_OnAProfileNode_ShowTheCashFlowsAndOnlyTheTradesOfItsTickers()
    {
        var (portfolio, deposit, withdrawal, aapl, _) = CashFlowsAndTwoTrades();
        var profile = new WatchlistProfile(Guid.NewGuid(), "Tech", IndicatorColor.Bullish, isPortfolio: true,
            items: new[] { new WatchlistItem("AAPL", DateTimeOffset.UtcNow) });

        var rows = await TransactionRowsAsync(portfolio, profile, selectProfile: true);

        Assert.Equal(new[] { deposit, withdrawal, aapl }, rows);   // the MSFT trade is not part of this profile
    }

    [Fact]
    public async Task Transactions_OnAProfileNodeWithoutTickers_ShowEveryRow()
    {
        var (portfolio, deposit, withdrawal, aapl, msft) = CashFlowsAndTwoTrades();
        var profile = new WatchlistProfile(Guid.NewGuid(), "Empty", IndicatorColor.Bullish, isPortfolio: true);

        var rows = await TransactionRowsAsync(portfolio, profile, selectProfile: true);

        Assert.Equal(new[] { deposit, withdrawal, aapl, msft }, rows);
    }

    [Fact]
    public async Task Evaluate_WithUnconvertedCurrencies_ShowsNoticeWithCodesAndMarksOnlyThoseRows()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithUsdAndEurPositions(), new[] { CurrencyCode.EUR, CurrencyCode.USD });

        Assert.True(viewModel.HasUnconvertedNotice);
        Assert.Contains("EUR, USD", viewModel.UnconvertedNotice);
        Assert.True(viewModel.Positions.Single(p => p.Symbol == "AAPL").IsConversionUnavailable);
        Assert.True(viewModel.Positions.Single(p => p.Symbol == "SAP").IsConversionUnavailable);
        Assert.False(viewModel.Positions.Single(p => p.Symbol == "7203").IsConversionUnavailable);
    }

    [Fact]
    public async Task Evaluate_WithoutUnconvertedCurrencies_HidesNoticeAndMarksNoRow()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithUsdAndEurPositions(), null);

        Assert.False(viewModel.HasUnconvertedNotice);
        Assert.Null(viewModel.UnconvertedNotice);
        Assert.All(viewModel.Positions, p => Assert.False(p.IsConversionUnavailable));
    }

    [Fact]
    public async Task Evaluate_AfterUnconvertedCurrenciesDisappear_ClearsNoticeAndRowMarkers()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithUsdAndEurPositions(), new[] { CurrencyCode.USD });
        Assert.True(viewModel.HasUnconvertedNotice);

        _mockPortfolioManager.Setup(p => p.Evaluate(
                It.IsAny<Portfolio>(),
                It.IsAny<IReadOnlyDictionary<string, decimal>>(),
                It.IsAny<IReadOnlyDictionary<CurrencyCode, ExchangeRate>>(),
                It.IsAny<CurrencyCode>()))
            .Returns(new PortfolioEvaluationResult(new PortfolioMetrics(0, 0, 0, 0, 0), new Dictionary<string, decimal>(), new Dictionary<string, decimal>()));
        await SelectRootAndRefreshAsync(viewModel);

        Assert.False(viewModel.HasUnconvertedNotice);
        Assert.All(viewModel.Positions, p => Assert.False(p.IsConversionUnavailable));
    }

    [Fact]
    public async Task Positions_ShowTheCurrencyAndTheCostBasisInThatCurrency()
    {
        var portfolio = new Portfolio(
            0m,
            new Dictionary<string, Position>
            {
                { "AAPL", new Position("AAPL", 10m, 150.25m, false, new Money(150.25m, CurrencyCode.USD)) },
                { "7203", new Position("7203", 0.5m, 2000m, false, new Money(2000m, CurrencyCode.JPY)) },
            });

        var viewModel = await EvaluateWithResultAsync(portfolio, null);

        var aapl = viewModel.Positions.Single(p => p.Symbol == "AAPL");
        Assert.Equal("USD", aapl.Currency);
        Assert.Equal(1502.5m, aapl.CostBasis);
        var toyota = viewModel.Positions.Single(p => p.Symbol == "7203");
        Assert.Equal("JPY", toyota.Currency);
        Assert.Equal(1000m, toyota.CostBasis);
    }

    private static Portfolio PortfolioWithCash(params (CurrencyCode Currency, decimal Balance)[] balances) =>
        new(balances.FirstOrDefault(b => b.Currency == CurrencyCode.JPY).Balance,
            cashBalances: balances.ToDictionary(b => b.Currency, b => b.Balance));

    // Evaluates another portfolio on a view model that is already showing one (the 60-second refresh of the real application).
    private async Task RefreshWithPortfolioAsync(PortfolioSummaryViewModel viewModel, Portfolio portfolio)
    {
        _mockPortfolioManager.Setup(p => p.LoadPortfolioAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(portfolio);
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>(), PortfolioAccountingDefaults.CostBasis)).Returns(portfolio);
        await SelectRootAndRefreshAsync(viewModel);
    }

    [Fact]
    public async Task CashStrip_ShowsOnlyTheCurrenciesWithCash_AndIsVisibleFromTwoCurrencies()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 1_000_000m), (CurrencyCode.USD, 500m)), null);

        Assert.True(viewModel.IsCashStripVisible);
        Assert.Equal(new[] { "JPY", "USD" }, viewModel.CurrencyCashBalances.Select(c => c.Currency));
        Assert.Equal(new[] { 1_000_000m, 500m }, viewModel.CurrencyCashBalances.Select(c => c.Balance));
    }

    [Fact]
    public async Task CashStrip_IsOrderedByCurrencyCode()
    {
        var viewModel = await EvaluateWithResultAsync(
            PortfolioWithCash((CurrencyCode.USD, 5m), (CurrencyCode.JPY, 7m), (CurrencyCode.EUR, 3m)), null);

        Assert.Equal(new[] { "EUR", "JPY", "USD" }, viewModel.CurrencyCashBalances.Select(c => c.Currency));
    }

    [Fact]
    public async Task CashStrip_AfterASpentBalanceBecomesZero_LeavesTheItemOutAndHidesTheStrip()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 1_000_000m), (CurrencyCode.USD, 500m)), null);
        var jpyItem = viewModel.CurrencyCashBalances.Single(c => c.Currency == "JPY");

        // The engine keeps the spent currency in the dictionary with the value 0: two keys, but only one currency holds cash.
        await RefreshWithPortfolioAsync(viewModel, PortfolioWithCash((CurrencyCode.JPY, 1_000_000m), (CurrencyCode.USD, 0m)));

        Assert.Equal(new[] { "JPY" }, viewModel.CurrencyCashBalances.Select(c => c.Currency));
        Assert.False(viewModel.IsCashStripVisible);
        Assert.Same(jpyItem, viewModel.CurrencyCashBalances[0]);   // updated in place, not rebuilt
    }

    [Fact]
    public async Task CashStrip_WhenTheCurrencyIsFundedAgain_IsVisibleAgainAndKeepsTheOtherItem()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 1_000_000m), (CurrencyCode.USD, 0m)), null);
        Assert.False(viewModel.IsCashStripVisible);
        var jpyItem = viewModel.CurrencyCashBalances.Single();

        await RefreshWithPortfolioAsync(viewModel, PortfolioWithCash((CurrencyCode.JPY, 900_000m), (CurrencyCode.USD, 200m)));

        Assert.True(viewModel.IsCashStripVisible);
        Assert.Equal(2, viewModel.CurrencyCashBalances.Count);
        Assert.Same(jpyItem, viewModel.CurrencyCashBalances.Single(c => c.Currency == "JPY"));
        Assert.Equal(900_000m, jpyItem.Balance);
        Assert.Equal(200m, viewModel.CurrencyCashBalances.Single(c => c.Currency == "USD").Balance);
    }

    [Fact]
    public async Task CashStrip_WithoutAnyCash_IsEmptyAndHidden()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 0m), (CurrencyCode.USD, 0m)), null);

        Assert.Empty(viewModel.CurrencyCashBalances);
        Assert.False(viewModel.IsCashStripVisible);
    }

    [Fact]
    public async Task CashStrip_NewCurrencyInTheMiddle_KeepsTheOrderAndTheExistingItems()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 10m), (CurrencyCode.USD, 20m)), null);
        var items = viewModel.CurrencyCashBalances.ToList();

        await RefreshWithPortfolioAsync(viewModel, PortfolioWithCash((CurrencyCode.JPY, 10m), (CurrencyCode.USD, 20m), (CurrencyCode.EUR, 30m)));

        Assert.Equal(new[] { "EUR", "JPY", "USD" }, viewModel.CurrencyCashBalances.Select(c => c.Currency));
        Assert.Same(items[0], viewModel.CurrencyCashBalances[1]);
        Assert.Same(items[1], viewModel.CurrencyCashBalances[2]);
    }

    [Fact]
    public async Task CashBalance_IsTheCashOfTheEvaluatedMetrics()
    {
        var viewModel = await EvaluateWithResultAsync(PortfolioWithCash((CurrencyCode.JPY, 1m)), null);
        _mockPortfolioManager.Setup(p => p.Evaluate(
                It.IsAny<Portfolio>(),
                It.IsAny<IReadOnlyDictionary<string, decimal>>(),
                It.IsAny<IReadOnlyDictionary<CurrencyCode, ExchangeRate>>(),
                It.IsAny<CurrencyCode>()))
            .Returns(new PortfolioEvaluationResult(new PortfolioMetrics(1000, 0, 0, 123m, 12.3m), new Dictionary<string, decimal>(), new Dictionary<string, decimal>()));

        await SelectRootAndRefreshAsync(viewModel);

        Assert.Equal(123m, viewModel.CashBalance);
    }

    [Fact]
    public void Receive_ColumnChooserAppliedMessage_UpdatesIsNotesColumnVisible()
    {
        // Arrange
        _mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var viewModel = new PortfolioSummaryViewModel(
            _mockPortfolioManager.Object,
            _mockMarketDataProvider.Object,
            _dispatcherService,
            _mockMessenger.Object,
            _mockLogger.Object,
            _mockWatchlistManager.Object,
            _mockDialogService.Object,
            _dialogViewModelFactory,
            _mockDesignTimeDetector.Object,
            _mockLocalizationService.Object
        );

        Assert.True(viewModel.IsNotesColumnVisible);

        // Act: Message without Notes
        viewModel.Receive(new StockAnalyzer.Avalonia.Common.ColumnChooserAppliedMessage(new List<string> { "Symbol", "Price" }));

        // Assert
        Assert.False(viewModel.IsNotesColumnVisible);

        // Act: Message with Notes
        viewModel.Receive(new StockAnalyzer.Avalonia.Common.ColumnChooserAppliedMessage(new List<string> { "Symbol", "Notes", "Price" }));

        // Assert
        Assert.True(viewModel.IsNotesColumnVisible);
    }
}
