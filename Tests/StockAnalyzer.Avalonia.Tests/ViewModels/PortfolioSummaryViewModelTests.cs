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
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>()))
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
        _mockPortfolioManager.Verify(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.Is<IReadOnlyList<Transaction>>(list => list.Contains(expectedTransaction))), Times.Once);
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

        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>()))
            .Returns((decimal cash, IReadOnlyList<Transaction> hist) => new Portfolio(cash, history: hist.ToList()));

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
        _mockPortfolioManager.Verify(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.Is<IReadOnlyList<Transaction>>(list => !list.Contains(t1) && list.Contains(t2))), Times.Once);
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
        _mockPortfolioManager.Setup(p => p.RebuildPortfolio(It.IsAny<decimal>(), It.IsAny<IReadOnlyList<Transaction>>()))
            .Returns((decimal cash, IReadOnlyList<Transaction> hist) => new Portfolio(cash, history: hist.ToList()));

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
