using System;
using System.IO;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Configurations for the workspace coordinator.
/// </summary>
public sealed class WorkspaceCoordinatorOptions
{
    /// <summary>
    /// Gets or sets the absolute path to the workspace settings JSON file.
    /// Default is a file named "default_workspace.json" in the base application directory.
    /// </summary>
    public string WorkspacePath { get; set; } = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath(LayoutConstants.DefaultWorkspaceFileName);
}
