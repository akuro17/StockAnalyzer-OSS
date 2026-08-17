namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Defines the lifecycle states of the entire workspace.
/// </summary>
public enum WorkspaceLifecycleState
{
    Initializing,
    LoadingWorkspace,
    Ready,
    ShuttingDown,
    Disposed
}
