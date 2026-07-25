namespace StockAnalyzer.Avalonia.Services;

public interface IWindowManagementService
{
    ITearOffService TearOff { get; }
    IPanelTabFactory TabFactory { get; }
    IDetachedWindowFactory WindowFactory { get; }
    IWindowBoundaryService BoundaryService { get; }
}
