using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service contract for loading and persisting McpServer user settings (launch options and discussion parameters).
/// </summary>
public interface IMcpServerSettingsManager : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the current in-memory McpServer settings.
    /// </summary>
    McpServerSettings CurrentSettings { get; }

    /// <summary>
    /// Loads the persisted settings from user_mcpserver_settings.json, falling back to system defaults.
    /// </summary>
    Task<McpServerSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the given settings to user_mcpserver_settings.json atomically.
    /// </summary>
    Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the current settings to the default values.
    /// </summary>
    void ResetToDefault();
}
