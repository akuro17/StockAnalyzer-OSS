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
    }
}
