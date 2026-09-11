#if DEBUG
using System;

namespace StockAnalyzer.Avalonia.Services;

public sealed class MockWindowManagementService : IWindowManagementService
{
    public ITearOffService TearOff { get; }
    public IPanelTabFactory TabFactory { get; }
    public IDetachedWindowFactory WindowFactory { get; }
    public IWindowBoundaryService BoundaryService { get; }

    public MockWindowManagementService()
    {
        TearOff = null!;
        TabFactory = new PanelTabFactory(null!, new TabRegistry());
        WindowFactory = null!;
        BoundaryService = new WindowBoundaryService();
    }
}
#endif
