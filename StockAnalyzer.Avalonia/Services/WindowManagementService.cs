using System;

namespace StockAnalyzer.Avalonia.Services;

public sealed class WindowManagementService : IWindowManagementService
{
    public ITearOffService TearOff { get; }
    public IPanelTabFactory TabFactory { get; }
    public IDetachedWindowFactory WindowFactory { get; }
    public IWindowBoundaryService BoundaryService { get; }

    public WindowManagementService(
        ITearOffService tearOff,
        IPanelTabFactory tabFactory,
        IDetachedWindowFactory windowFactory,
        IWindowBoundaryService boundaryService)
    {
        TearOff = tearOff ?? throw new ArgumentNullException(nameof(tearOff));
        TabFactory = tabFactory ?? throw new ArgumentNullException(nameof(tabFactory));
        WindowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        BoundaryService = boundaryService ?? throw new ArgumentNullException(nameof(boundaryService));
    }
}
