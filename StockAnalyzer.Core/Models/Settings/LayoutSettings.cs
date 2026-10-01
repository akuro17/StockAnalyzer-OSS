using System;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Layout limits bound from the "Layout" section of appsettings.json (through <see cref="UI.LayoutStateStore"/>, the layout SSoT).
/// The defaults live here only; the configuration file follows them.
/// </summary>
public class LayoutSettings
{
    /// <summary>
    /// Most tabs one panel region (or one Tab Window) may hold. Adding, redocking and the selected-tab index all honor it.
    /// Rationale of the default: a bounded tab strip keeps the UI responsive and the strip readable.
    /// </summary>
    public int MaxPanelTabs { get; set; } = 16;

    /// <summary>
    /// Farthest a single drag may move a tab (in positions); a farther request is limited to this distance, then clamped to the tab range.
    /// Rationale of the default: it is far above any real tab count, so it only bounds a corrupt or extreme request.
    /// </summary>
    public int MaxTabReorderDistance { get; set; } = 100;

    /// <summary>Throws when a configured value cannot be honored (a limit is never silently replaced).</summary>
    public void Validate()
    {
        if (MaxPanelTabs < 1)
            throw new InvalidOperationException($"Layout:MaxPanelTabs must be at least 1. Given: {MaxPanelTabs}");
        if (MaxTabReorderDistance < 0)
            throw new InvalidOperationException($"Layout:MaxTabReorderDistance must not be negative. Given: {MaxTabReorderDistance}");
    }
}
