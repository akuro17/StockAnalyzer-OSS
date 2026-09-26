using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings
{
    /// <summary>
    /// A user-defined list folder nested under a <see cref="TickerParentCategorySettings"/> parent
    /// category. Unlike the parent category itself (a pure grouping container), a list folder is
    /// the actual ticker-holding leaf - structurally parallel to a Watchlist/Portfolio profile.
    /// </summary>
    public class TickerListFolderSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New List";

        /// <summary>
        /// Individual ticker symbols manually assigned directly into this list folder.
        /// </summary>
        public List<string> ChildTickers { get; set; } = new();
    }
}
