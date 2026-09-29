using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Registry for workspace tab types and their associated factories.
/// </summary>
public interface ITabRegistry
{
    /// <summary>
    /// Registers a new tab type.
    /// </summary>
    /// <param name="metadata">Tab metadata (ID, display name, etc.).</param>
    /// <param name="factory">Factory delegate to create the ViewModel instance.</param>
    void Register(TabMetadata metadata, Func<IServiceProvider, object> factory);

    /// <summary>
    /// Resolves a tab's metadata by its ID.
    /// </summary>
    TabMetadata GetMetadata(string id);

    /// <summary>
    /// Creates a ViewModel instance for the specified tab ID.
    /// </summary>
    object CreateViewModel(string id, IServiceProvider serviceProvider);

    /// <summary>
    /// Retrieves all registered tab metadata.
    /// </summary>
    IEnumerable<TabMetadata> GetAllMetadata();

    /// <summary>
    /// Locks the registry to prevent further registrations (typically called after startup).
    /// </summary>
    void Lock();
}
