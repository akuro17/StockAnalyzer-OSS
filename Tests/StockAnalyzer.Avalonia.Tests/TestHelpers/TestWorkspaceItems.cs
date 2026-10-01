using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Builds minimal <see cref="WorkspaceViewItem"/>s for tests (title equals id, opaque view model).</summary>
internal static class TestWorkspaceItems
{
    /// <param name="id">Id and title of the item.</param>
    /// <param name="containerId">Tab Window container the item belongs to, if any.</param>
    /// <param name="originalPanel">Panel the item was torn off from; a non-null value also marks it detached.</param>
    internal static WorkspaceViewItem Create(string id, string? containerId = null, string? originalPanel = null) =>
        new()
        {
            Id = id,
            Title = id,
            ViewModel = new object(),
            ContainerId = containerId,
            OriginalPanelName = originalPanel,
            IsDetached = originalPanel != null
        };
}
