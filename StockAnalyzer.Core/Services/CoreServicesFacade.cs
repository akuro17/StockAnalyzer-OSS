using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Core.Services;

public class CoreServicesFacade : ICoreServicesFacade
{
    private readonly ILogger<CoreServicesFacade> _logger;

    public IStockAnalyzerSettings Settings { get; }
    public IWatchlistManager WatchlistManager { get; }
    public IMarketDataProvider MarketDataProvider { get; }
    public IPythonService PythonService { get; }

    public CoreServicesFacade(
        IStockAnalyzerSettings settings,
        IWatchlistManager watchlistManager,
        IMarketDataProvider marketDataProvider,
        IPythonService pythonService,
        ILogger<CoreServicesFacade>? logger = null)
    {
        _logger = logger ?? NullLogger<CoreServicesFacade>.Instance;
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        WatchlistManager = watchlistManager ?? throw new ArgumentNullException(nameof(watchlistManager));
        MarketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        PythonService = pythonService ?? throw new ArgumentNullException(nameof(pythonService));
    }
}
