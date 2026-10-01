using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

public class TearOffService : ITearOffService
{
    private readonly IMessenger _messenger;
    private readonly IDetachedWindowFactory _windowFactory;
    private readonly IContainerRegistry _containerRegistry;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TearOffService> _logger;
    private readonly Func<object?>? _mainWindowProvider;

    public TearOffService(
        IMessenger messenger, 
        IDetachedWindowFactory windowFactory, 
        IContainerRegistry containerRegistry,
        IDispatcherService dispatcherService,
        ILogger<TearOffService>? logger = null,
        Func<object?>? mainWindowProvider = null)
    {
        _messenger = messenger;
        _windowFactory = windowFactory;
        _containerRegistry = containerRegistry;
        _dispatcherService = dispatcherService;
        _logger = logger ?? NullLogger<TearOffService>.Instance;
        _mainWindowProvider = mainWindowProvider;
    }

    public void TearOff(WorkspaceViewItem item)
    {
        if (item == null) return;

        _logger.LogInformation("[TabTearOff] Initiating transaction: Tab={TabId}", item.Id);

        // Perform on UI thread to ensure visual tree operations and window creation are safe
        _dispatcherService.Post(() => 
        {
            // 1. Transaction Start: Request removal from MainWindow collections
            var response = _messenger.Send(new TearOffRequestMessage(item));
            
            if (!response.Response)
            {
                _logger.LogWarning("[TabTearOff] Failed: Item Tab={TabId} not found in any main panel", item.Id);
                return;
            }

            try
            {
                // 2. The "Effect": Materialize and Show Detached Window
                if (global::StockAnalyzer.Avalonia.App.Current.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && 
                    desktop.MainWindow != null)
                {
                    // Use the factory to create a new window instance (Factory will wrap item in DetachedWindowViewModel)
                    var window = _windowFactory.CreateWindow(desktop.MainWindow, item);
                    
                    // Show as a top-level window (no owner) to allow independent Z-order
                    _windowFactory.ShowWindow(window);
                    
                    _logger.LogInformation("[TabTearOff] Success: Tab={TabId} moved to standalone window", item.Id);
                }
                else
                {
                    throw new InvalidOperationException("Atomic Tear-off requires a Desktop Application Lifetime and a visible MainWindow.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TabTearOff] Critical error for Tab={TabId}. Initiating Rollback.", item.Id);
                
                // 3. Rollback: If window creation or display fails, restore the item to the main window
                _messenger.Send(new RestoreRequestMessage(item));
            }
        });
    }

    public void Restore(WorkspaceViewItem item)
    {
        if (item == null) return;
        
        _logger.LogInformation("[TabTearOff] Restoring: Tab={TabId} to main window", item.Id);
        _messenger.Send(new RestoreRequestMessage(item));
    }

    public void RestoreDetached(WorkspaceViewItem item)
    {
        if (item == null) return;

        _logger.LogInformation("[TabTearOff] Restoring detached state: Tab={TabId}", item.Id);

        _dispatcherService.Post(() => 
        {
            try
            {
                if (global::StockAnalyzer.Avalonia.App.Current.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && 
                    desktop.MainWindow != null)
                {
                    var window = _windowFactory.CreateWindow(desktop.MainWindow, item);
                    
                    // Apply saved geometry with clamping safety via factory
                    if (!double.IsNaN(item.DetachedX))
                    {
                        _windowFactory.ApplyGeometry(window, item.DetachedX, item.DetachedY, item.DetachedWidth, item.DetachedHeight);
                    }
                    
                    // Show as a top-level window (no owner) to allow independent Z-order
                    _windowFactory.ShowWindow(window);
                    _logger.LogInformation("[TabTearOff] Detached restoration success: Tab={TabId}", item.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TabTearOff] Startup restoration failed for Tab={TabId}. Falling back to Redock.", item.Id);
                _messenger.Send(new RestoreRequestMessage(item));
            }
        });
    }

    public void RestoreDetachedGroup(System.Collections.Generic.IEnumerable<WorkspaceViewItem> items)
    {
        if (items == null || !items.Any()) return;

        var itemList = items.ToList();
        var firstItem = itemList[0];

        _logger.LogInformation("[TabTearOff] Restoring detached group: Count={Count}, LeadTab={TabId}", itemList.Count, firstItem.Id);

        _dispatcherService.Post(() => 
        {
            try
            {
                if (ShowGroupWindow(itemList))
                {
                    _logger.LogInformation("[TabTearOff] Detached group restoration success: LeadTab={TabId}", firstItem.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TabTearOff] Group restoration failed. Falling back to individual Redocks.");
                foreach (var item in itemList)
                {
                    _messenger.Send(new RestoreRequestMessage(item));
                }
            }
        });
    }

    public bool TryRehostDetached(System.Collections.Generic.IReadOnlyList<WorkspaceViewItem> items)
    {
        if (items == null || items.Count == 0) return false;

        _logger.LogInformation("[TabTearOff] Re-hosting group: Count={Count}, LeadTab={TabId}", items.Count, items[0].Id);

        var previousContainerIds = items.Select(i => i.ContainerId).ToList();
        try
        {
            return ShowGroupWindow(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TabTearOff] Re-host failed for LeadTab={TabId}. Undoing partial changes; no fallback redock is sent.", items[0].Id);
            for (int i = 0; i < items.Count; i++)
            {
                if (!string.IsNullOrEmpty(items[i].ContainerId) && items[i].ContainerId != previousContainerIds[i])
                {
                    _containerRegistry.Unregister(items[i].ContainerId!);
                }
                items[i].ContainerId = previousContainerIds[i];
            }
            return false;
        }
    }

    // The desktop application's main window, or the one supplied by the constructor's provider (the desktop lifetime cannot be
    // replaced once an Application is initialized, so a provider is the only way to exercise this path without a real desktop).
    private object? ResolveMainWindow() =>
        _mainWindowProvider != null
            ? _mainWindowProvider()
            : (global::StockAnalyzer.Avalonia.App.Current?.ApplicationLifetime as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// Creates one window hosting all <paramref name="itemList"/> items (the first item creates it, the rest are added to its
    /// container), applies the first item's saved geometry and shows it. Returns false when there is no desktop main window;
    /// throws when the window cannot be created or shown.
    /// </summary>
    private bool ShowGroupWindow(System.Collections.Generic.IReadOnlyList<WorkspaceViewItem> itemList)
    {
        var firstItem = itemList[0];

        var mainWindow = ResolveMainWindow();
        if (mainWindow == null)
        {
            return false;
        }

        // 1. Create the window with the first item
        var window = _windowFactory.CreateWindow(mainWindow, firstItem);

        // 2. Get the newly created container's ViewModel
        if (window is global::Avalonia.Controls.Window avaloniaWin && 
            avaloniaWin.DataContext is ViewModels.IDetachedWindowContainer groupVm)
        {
            // 3. Add remaining items to the same container
            for (int i = 1; i < itemList.Count; i++)
            {
                var nextItem = itemList[i];
                nextItem.ContainerId = groupVm.ContainerId;
                groupVm.AddItem(nextItem);
            }
        }
        else if (itemList.Count > 1)
        {
            // The remaining items could not join the window's container: reporting success would silently drop them.
            throw new InvalidOperationException("The detached window does not expose a container for the remaining group items.");
        }

        // 4. Apply saved geometry from the first item (which should represent the window)
        if (!double.IsNaN(firstItem.DetachedX))
        {
            _windowFactory.ApplyGeometry(window, firstItem.DetachedX, firstItem.DetachedY, firstItem.DetachedWidth, firstItem.DetachedHeight);
        }

        // 5. Show the window
        _windowFactory.ShowWindow(window);
        return true;
    }

    public void Redock(WorkspaceViewItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.ContainerId)) return;

        _logger.LogInformation("[TabTearOff] Redocking: Tab={TabId} from Container={ContainerId}", item.Id, item.ContainerId);

        _dispatcherService.Post(() => 
        {
            try
            {
                // 1. Locate source container
                string containerId = item.ContainerId!;
                var container = _containerRegistry.GetContainer(containerId);
                if (container == null)
                {
                    _logger.LogWarning("[TabTearOff] Redock failed: Source container {ContainerId} not found.", containerId);
                    return;
                }

                // 2. Remove item from source container
                container.RemoveItem(item);
                item.ContainerId = null;
                item.IsDetached = false;

                // Ensure a default panel if it was a newly added tab
                if (string.IsNullOrEmpty(item.OriginalPanelName))
                {
                    item.OriginalPanelName = "Bottom";
                }

                // 3. Notify MainWindow to host it
                _messenger.Send(new RestoreRequestMessage(item));

                // 4. Cleanup: If window is now empty, close it
                if (container.Items.Count == 0)
                {
                    _logger.LogInformation("[TabTearOff] Container {ContainerId} is empty. Signaling closure.", containerId);
                    _messenger.Send(new CloseDetachedWindowMessage(containerId));
                }

                _logger.LogInformation("[TabTearOff] Redock success: Tab={TabId}", item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TabTearOff] Redock failed for Tab={TabId}.", item.Id);
            }
        });
    }
}
