namespace StockAnalyzer.Avalonia.ViewModels;

public interface IWorkspaceViewModelFacade
{
    DataWindowViewModel DataWindow { get; }
    TickerListViewModel TickerList { get; }
    DrawingToolSidebarViewModel Sidebar { get; }
    
    void BindChart(ChartViewModel chartViewModel);
}
