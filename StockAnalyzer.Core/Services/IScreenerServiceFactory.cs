namespace StockAnalyzer.Core.Services;

/// <summary>
/// A <see cref="IScreenerService"/> that can build a copy of itself (same settings, same metadata providers, same logger) that reads its bars from another
/// <see cref="IDataService"/>. The read-only gateway uses it to run one scan over the bars known on a past date without touching the shared instance.
/// </summary>
public interface IScreenerServiceFactory
{
    /// <summary>A screener identical to this one except that it loads bars from <paramref name="dataService"/>.</summary>
    IScreenerService CreateScreener(IDataService dataService);
}
