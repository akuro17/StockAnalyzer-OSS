using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Services
{
    public interface IDetachedTabManager : IDisposable
    {
        /// <summary>
        /// Gets the read-only list of active detached tabs.
        /// Must only be accessed from the UI thread.
        /// </summary>
        IReadOnlyList<WorkspaceViewItem> DetachedTabs { get; }

        /// <summary>
        /// Registers a detached tab. If already registered, does nothing (ensures idempotency).
        /// Can only be called on the UI thread.
        /// </summary>
        /// <returns>true if newly registered; false if ignored as a duplicate.</returns>
        bool RegisterActiveDetachedTab(WorkspaceViewItem item);

        /// <summary>
        /// Removes a detached tab from the active tracking list.
        /// Can only be called on the UI thread.
        /// </summary>
        /// <returns>true if successfully removed; false if the target was not found.</returns>
        bool RemoveActiveDetachedTab(WorkspaceViewItem item);

        /// <summary>
        /// Restores and instantiates detached windows from the saved workspace settings.
        /// Runs strictly on the UI thread.
        /// </summary>
        void Restore(WorkspaceSettings settings);

        /// <summary>
        /// Captures the state of currently active detached windows into the destination list (overwrite semantics).
        /// Runs strictly on the UI thread.
        /// </summary>
        void Capture(List<DetachedTabInfo> destination, IReadOnlyList<StockAnalyzer.Core.Models.CoreIndicatorSettings>? fallbackIndicators = null);
    }
}
