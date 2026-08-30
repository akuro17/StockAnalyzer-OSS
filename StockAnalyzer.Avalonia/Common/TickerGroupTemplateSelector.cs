using Avalonia.Controls;
using Avalonia.Controls.Templates;
using StockAnalyzer.Avalonia.ViewModels.TickerList;

namespace StockAnalyzer.Avalonia.Common;

public class TickerGroupTemplateSelector : IDataTemplate
{
    public IDataTemplate? CategoryTemplate { get; set; }
    public IDataTemplate? AllTickersTemplate { get; set; }
    public IDataTemplate? WatchlistTemplate { get; set; }
    public IDataTemplate? PortfolioTemplate { get; set; }
    public IDataTemplate? ActionTemplate { get; set; }

    public Control? Build(object? data)
    {
        if (data is CategoryNode) return CategoryTemplate?.Build(data);
        if (data is AllTickersNode) return AllTickersTemplate?.Build(data);
        if (data is WatchlistNode) return WatchlistTemplate?.Build(data);
        if (data is PortfolioNode) return PortfolioTemplate?.Build(data);
        if (data is ActionNode) return ActionTemplate?.Build(data);
        
        return null;
    }

    public bool Match(object? data)
    {
        return data is TickerGroupNode;
    }
}
