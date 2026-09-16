using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Watchlist
{
    public class WatchlistColumnRegistryWrapper : IWatchlistColumnRegistry
    {
        public IReadOnlyList<WatchlistColumnMetadata> GetAllColumns()
        {
            return WatchlistColumnRegistry.AllColumns;
        }
    }
}
