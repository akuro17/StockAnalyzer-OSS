using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Watchlist
{
    public interface IWatchlistColumnRegistry
    {
        IReadOnlyList<WatchlistColumnMetadata> GetAllColumns();
    }
}
