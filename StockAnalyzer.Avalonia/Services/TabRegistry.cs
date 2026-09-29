using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Thread-safe implementation of ITabRegistry.
/// </summary>
public sealed class TabRegistry : ITabRegistry
{
    private readonly ILogger<TabRegistry> _logger;
    private readonly ConcurrentDictionary<string, (TabMetadata Metadata, Func<IServiceProvider, object> Factory)> _registry = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLocked;

    public TabRegistry(ILogger<TabRegistry>? logger = null)
    {
        _logger = logger ?? NullLogger<TabRegistry>.Instance;
    }

    public void Register(TabMetadata metadata, Func<IServiceProvider, object> factory)
    {
        if (_isLocked)
        {
            throw new InvalidOperationException("Cannot register tabs after the registry has been locked.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Id))
        {
            throw new ArgumentException("Tab ID cannot be null or whitespace.", nameof(metadata));
        }

        if (!_registry.TryAdd(metadata.Id, (metadata, factory)))
        {
            throw new InvalidOperationException($"Tab with ID '{metadata.Id}' is already registered.");
        }
    }

    public TabMetadata GetMetadata(string id)
    {
        if (_registry.TryGetValue(id, out var entry))
        {
            return entry.Metadata;
        }

        throw new KeyNotFoundException($"Tab with ID '{id}' not found in registry.");
    }

    public object CreateViewModel(string id, IServiceProvider serviceProvider)
    {
        if (_registry.TryGetValue(id, out var entry))
        {
            return entry.Factory(serviceProvider);
        }

        throw new KeyNotFoundException($"Tab with ID '{id}' not found in registry.");
    }

    public IEnumerable<TabMetadata> GetAllMetadata()
    {
        return _registry.Values.Select(v => v.Metadata);
    }

    public void Lock()
    {
        _isLocked = true;
    }
}
