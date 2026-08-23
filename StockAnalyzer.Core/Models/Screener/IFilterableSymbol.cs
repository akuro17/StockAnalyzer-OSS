namespace StockAnalyzer.Core.Models.Screener
{
    /// <summary>
    /// Represents a symbol entity that can be evaluated by WatchlistFilterEngine.
    /// Decouples presentation ViewModels from filtering business logic.
    /// </summary>
    public interface IFilterableSymbol
    {
        string Symbol { get; }
        string? Tag { get; }
        object? GetPropertyValue(string propertyName);
    }
}
