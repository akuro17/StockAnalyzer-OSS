using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Read-only access to the saved workspace (watchlists, portfolios, custom categories, Tickers-tab filters). Nothing is created or written.</summary>
public interface IReadOnlyWorkspaceSource
{
    /// <summary>The saved workspace, or null when there is no workspace file yet.</summary>
    /// <exception cref="DataUnavailableException">The file exists but cannot be read or parsed (for example while it is being replaced).</exception>
    Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A token that changes whenever the saved workspace changes, obtained without reading its content (for the file source: the last write time and size),
    /// so that a cache of something computed from the workspace (for example the members of a ticker group) can be keyed with it. Null when the source cannot tell,
    /// in which case such a cache is keyed without it. The default is null, so existing implementations need no change.
    /// </summary>
    string? GetVersion() => null;
}
