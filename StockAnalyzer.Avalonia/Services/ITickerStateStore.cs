using System.Collections.Generic;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.Services
{
    /// <summary>
    /// Abstracts the state source for ticker groups and active display items.
    /// Decouples ScreenerViewModel from concrete TickerListViewModel instances.
    /// </summary>
    public interface ITickerStateStore
    {
        IEnumerable<TickerGroupNode> Groups { get; }
        IEnumerable<IFilterableSymbol> DisplayItems { get; }

        /// <summary>
        /// Resolves the concrete ticker set a given tree node represents (Watchlist/Portfolio ->
        /// its members; AllTickers -> every known ticker; Filter -> its parent node's tickers with
        /// the filter's own rules applied; Category -> the union of its children). Pure read: does
        /// not touch the store's own selection state (e.g. TickerListViewModel.SelectedNode), so
        /// calling it never affects the Tickers tab's own grid/UI.
        /// </summary>
        IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node);
    }
}
