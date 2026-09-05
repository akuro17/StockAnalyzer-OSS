using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Registry for tracking active detached window view models to enable cross-container tab movement.
/// </summary>
public interface IContainerRegistry
{
    void Register(string id, DetachedWindowViewModel viewModel);
    void Unregister(string id);
    DetachedWindowViewModel? GetContainer(string id);
    IEnumerable<DetachedWindowViewModel> GetAllContainers();
}

public class ContainerRegistry : IContainerRegistry
{
    private readonly Dictionary<string, DetachedWindowViewModel> _containers = new();

    public void Register(string id, DetachedWindowViewModel viewModel)
    {
        _containers[id] = viewModel;
    }

    public void Unregister(string id)
    {
        _containers.Remove(id);
    }

    public DetachedWindowViewModel? GetContainer(string id)
    {
        return _containers.TryGetValue(id, out var vm) ? vm : null;
    }

    public IEnumerable<DetachedWindowViewModel> GetAllContainers()
    {
        return _containers.Values;
    }
}
