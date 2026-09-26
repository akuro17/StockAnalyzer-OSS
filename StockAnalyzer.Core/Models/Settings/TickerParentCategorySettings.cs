using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings
{
    /// <summary>
    /// A user-defined parent category shown in the Tickers tab's left-column category list.
    /// The built-in "All Ticker" / "Watchlist" / "Portfolio" categories are not persisted here;
    /// this only holds categories the user added beyond those defaults. A parent category is a
    /// pure grouping container - it never holds tickers directly; those live in its
    /// <see cref="Folders"/> (see <see cref="TickerListFolderSettings"/>).
    /// </summary>
    public class TickerParentCategorySettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New Category";

        /// <summary>
        /// List folders nested under this parent category. Selecting the parent category displays
        /// the union of tickers across all of these, mirroring the fixed Watchlists/Portfolios
        /// category's aggregation of its own child nodes.
        /// </summary>
        public List<TickerListFolderSettings> Folders { get; set; } = new();
    }
}
