using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Locks SA_UI_INTERACTION.md section 24 ("Shared List Scroll Gutter"): <c>IndicatorRegistrationView</c>'s
/// filter catalog <c>ListBox</c> uses the same permanent non-overlay scrollbar + reserved gutter as
/// <c>IndicatorSettingsWindow</c>'s Library catalog, rather than Avalonia's default overlay/auto-hide bar.
/// Source scan (not headless) for the same reason as <see cref="IndicatorNameTrimConsistencyTests"/>: the
/// catalog lives in a DI-heavy view hosted inside the ticker-notes dialog. The gutter value itself is
/// asserted to come from the shared <c>ListScrollGutter</c> resource, never a repeated literal.
/// </summary>
public class IndicatorRegistrationCatalogScrollbarTests
{
    // The catalog ListBox: from the "Items List" marker to the end of that ListBox's opening tag.
    private static readonly Regex CatalogListBoxOpenTag =
        new(@"<!--\s*Items List\s*-->.*?<ListBox\b(?<attrs>[^>]*)>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string ReadView() =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(
            TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "IndicatorRegistrationView.axaml"));

    [Fact]
    public void FilterCatalogListBox_HasPermanentScrollbar_AndSharedGutter()
    {
        var xaml = ReadView();

        var m = CatalogListBoxOpenTag.Match(xaml);
        Assert.True(m.Success, "Could not locate the filter catalog ListBox opening tag in IndicatorRegistrationView.axaml.");

        var attrs = m.Groups["attrs"].Value;

        Assert.Contains("ScrollViewer.AllowAutoHide=\"False\"", attrs);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", attrs);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", attrs);

        // Gutter must reference the shared resource, not a hardcoded Thickness.
        Assert.Contains("Padding=\"{DynamicResource ListScrollGutter}\"", attrs);
        Assert.DoesNotContain("Padding=\"0,0,6,0\"", attrs);
    }
}
