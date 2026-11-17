using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// An <see cref="IReadOnlyWorkspaceSource"/> that holds no workspace and keeps the default interface member <c>GetVersion()</c> (null),
/// like every implementer written before the member existed.
/// </summary>
public sealed class LoadOnlyWorkspaceSource : IReadOnlyWorkspaceSource
{
    public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceSettings?>(null);
}

/// <summary>An <see cref="IReadOnlyWorkspaceSource"/> that holds no workspace and reports a version a test can change (the saved workspace file was edited).</summary>
public sealed class VersionedWorkspaceSource : IReadOnlyWorkspaceSource
{
    public string? Version { get; set; }

    public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceSettings?>(null);

    public string? GetVersion() => Version;
}
