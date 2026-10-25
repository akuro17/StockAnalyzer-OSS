using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Read-only access to the saved workspace (watchlists, portfolios, custom categories, Tickers-tab filters). Nothing is created or written.</summary>
public interface IReadOnlyWorkspaceSource
{
    /// <summary>The saved workspace, or null when there is no workspace file yet.</summary>
    /// <exception cref="DataUnavailableException">The file exists but cannot be read or parsed (for example while it is being replaced).</exception>
    Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default);
}
