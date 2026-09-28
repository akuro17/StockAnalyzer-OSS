using System;

namespace StockAnalyzer.Avalonia.ViewModels;

public class WorkspaceViewModelFacade : IWorkspaceViewModelFacade
{
    public DataWindowViewModel DataWindow { get; }
    public TickerListViewModel TickerList { get; }
    public DrawingToolSidebarViewModel Sidebar { get; }
    public DrawingObjectsViewModel DrawingObjects { get; }

    public WorkspaceViewModelFacade(
        DataWindowViewModel dataWindow,
        TickerListViewModel tickerList,
        DrawingToolSidebarViewModel sidebar,
        DrawingObjectsViewModel drawingObjects)
    {
        DataWindow = dataWindow ?? throw new ArgumentNullException(nameof(dataWindow));
        TickerList = tickerList ?? throw new ArgumentNullException(nameof(tickerList));
        Sidebar = sidebar ?? throw new ArgumentNullException(nameof(sidebar));
        DrawingObjects = drawingObjects ?? throw new ArgumentNullException(nameof(drawingObjects));
    }

    public void BindChart(ChartViewModel chartViewModel)
    {
        if (chartViewModel == null) throw new ArgumentNullException(nameof(chartViewModel));
        DataWindow.SetChartViewModel(chartViewModel);
        Sidebar.SetChartViewModel(chartViewModel);
        DrawingObjects.SetChartViewModel(chartViewModel);
    }
}
