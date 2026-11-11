using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// DrawingObjectsViewModel gets its settings-panel registry (and dialog service) by constructor injection and never looks the
/// application's service provider up itself.
/// </summary>
public class DrawingObjectsViewModelPanelRegistryTests
{
    private static DrawingObjectsViewModel Create(IDrawingSettingsPanelRegistry? registry = null) =>
        new(new DispatcherService(), dialogService: null, iconDrawingService: null, layerService: null, presetService: null, panelRegistry: registry);

    [Fact]
    public void PanelRegistry_IsTheInjectedRegistry()
    {
        var registry = new DrawingSettingsPanelRegistry();

        using var vm = Create(registry);

        Assert.Same(registry, vm.PanelRegistry);
    }

    [Fact]
    public void PanelRegistry_FallsBackToAnEmptyRegistry_WhenNoneIsInjected()
    {
        using var vm = Create();

        Assert.NotNull(vm.PanelRegistry);
        Assert.IsType<DrawingSettingsPanelRegistry>(vm.PanelRegistry);
        Assert.Same(vm.PanelRegistry, vm.PanelRegistry);
    }

    [Fact]
    public async System.Threading.Tasks.Task ContainerResolvedViewModel_ReceivesTheRegisteredRegistryAndDialogService()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        services.AddViewModels();
        await using var provider = services.BuildServiceProvider();

        using var vm = provider.GetRequiredService<DrawingObjectsViewModel>();

        Assert.Same(provider.GetRequiredService<IDrawingSettingsPanelRegistry>(), vm.PanelRegistry);
        var dialogField = typeof(DrawingObjectsViewModel).GetField("_dialogService", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dialogField);
        Assert.Same(provider.GetRequiredService<IDialogService>(), dialogField!.GetValue(vm));
    }
}
