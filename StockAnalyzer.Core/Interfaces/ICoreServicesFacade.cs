using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Interfaces;

public interface ICoreServicesFacade
{
    IStockAnalyzerSettings Settings { get; }
    IWatchlistManager WatchlistManager { get; }
    IMarketDataProvider MarketDataProvider { get; }
    IPythonService PythonService { get; }
}
