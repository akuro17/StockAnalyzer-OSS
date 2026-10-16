namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the Screener Filters condition tree size (<c>Screener:MaxFilterTreeNodes</c>, <c>Screener:MaxFilterTreeDepth</c>). The bands are
/// code, never configuration: a configuration file cannot widen them. <see cref="ScreenerSettingsValidator"/> compares the settings with them at startup;
/// the settings class holds the operational values only. The hard bands are the owner's decision of 2026-10-16 (not derived from a measurement); the owner
/// gave no warning zone, so each standard band equals its hard band.
/// </summary>
public static class ScreenerFilterTreeLimits
{
    /// <summary>The shipped value of <c>Screener:MaxFilterTreeNodes</c>; the one definition behind the settings class and the interface default member.</summary>
    public const int DefaultMaxFilterTreeNodes = 64;

    /// <summary>The shipped value of <c>Screener:MaxFilterTreeDepth</c>; the one definition behind the settings class and the interface default member.</summary>
    public const int DefaultMaxFilterTreeDepth = 8;

    /// <summary>The smallest usable tree: the root group and one leaf.</summary>
    private const int SmallestUsableTreeNodes = 2;

    /// <summary>The root group is level 1.</summary>
    private const int RootDepth = 1;

    public static readonly SettingBand MaxFilterTreeNodes = new(SmallestUsableTreeNodes, 256, SmallestUsableTreeNodes, 256);

    public static readonly SettingBand MaxFilterTreeDepth = new(RootDepth, 16, RootDepth, 16);
}
