using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Locks SA_UI_INTERACTION.md "Indicator Name Display Trim/Wrap Standard" (section 26): the indicator
/// <c>DisplayName</c> must break at the same place on every screen. Dense catalog rows
/// (<c>ShortName</c> + <c>DisplayName</c> stacked in a virtualized <c>ListBox</c> inside a star column)
/// trim <em>both</em> lines with <c>CharacterEllipsis</c>; detail-pane headings for the
/// selected indicator wrap and never ellipsise. This is a source scan rather than a headless render
/// because the three catalogs live in <c>IsVisible</c>-toggled panels and heterogeneous DI-heavy
/// view models; the headless layout behaviour of the reference catalog is already covered by
/// <see cref="FeatureChannelPickerCatalogLayoutTests"/>.
/// </summary>
public class IndicatorNameTrimConsistencyTests
{
    private static readonly Regex TextBlockElement =
        new(@"<TextBlock\b[^>]*?/>", RegexOptions.Compiled | RegexOptions.Singleline);

    // The indicator catalog row template: the DataTemplate whose x:DataType is the catalog item type
    // (optionally namespace-prefixed) and which stacks a ShortName line above a DisplayName line.
    private static readonly Regex CatalogRowTemplate =
        new(@"<DataTemplate\b[^>]*x:DataType=""[^""]*(?:IndicatorCatalogItem|ScreenerCatalogItem)""[^>]*>(.*?)</DataTemplate>",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static string ReadView(string relativePath) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "Views", relativePath));

    /// <summary>Every <c>TextBlock</c> whose <c>Text</c> binds <c>ShortName</c> or <c>DisplayName</c>
    /// inside an indicator catalog row template must carry <c>TextTrimming="CharacterEllipsis"</c>.</summary>
    [Theory]
    [InlineData("Dialogs/FeatureChannelPickerView.axaml")]
    [InlineData("IndicatorSettingsWindow.axaml")]
    [InlineData("IndicatorRegistrationView.axaml")]
    public void CatalogRowLines_AllTrimWithCharacterEllipsis(string view)
    {
        var xaml = ReadView(view);

        var template = CatalogRowTemplate.Match(xaml);
        Assert.True(template.Success, $"{view}: could not locate the indicator catalog row DataTemplate.");

        var rowLines = TextBlockElement.Matches(template.Groups[1].Value)
            .Select(m => m.Value)
            .Where(tb => tb.Contains("Text=\"{Binding ShortName}\"") || tb.Contains("Text=\"{Binding DisplayName}\""))
            .ToList();

        Assert.True(rowLines.Count >= 2, $"{view}: expected the catalog row template's ShortName+DisplayName TextBlocks, found {rowLines.Count}.");

        foreach (var tb in rowLines)
        {
            Assert.True(
                tb.Contains("TextTrimming=\"CharacterEllipsis\""),
                $"{view}: catalog row TextBlock is missing TextTrimming=\"CharacterEllipsis\":\n{tb}");
        }
    }

    /// <summary>The detail-pane heading that names the selected indicator must wrap, never ellipsise.</summary>
    [Theory]
    [InlineData("Dialogs/FeatureChannelPickerView.axaml", "SelectedCatalogItem.DisplayName")]
    [InlineData("IndicatorSettingsWindow.axaml", "SelectedIndicator.DisplayName")]
    [InlineData("IndicatorSettingsWindow.axaml", "LibraryIndicatorPreviewName")]
    [InlineData("IndicatorRegistrationView.axaml", "LeftSelectedIndicator.DisplayName")]
    [InlineData("IndicatorRegistrationView.axaml", "RightSelectedIndicator.DisplayName")]
    public void SelectedIndicatorHeading_WrapsAndDoesNotEllipsise(string view, string binding)
    {
        var xaml = ReadView(view);

        var headings = TextBlockElement.Matches(xaml)
            .Select(m => m.Value)
            .Where(tb => tb.Contains("{Binding " + binding + "}") || tb.Contains("{Binding " + binding + ","))
            .ToList();

        Assert.True(headings.Count >= 1, $"{view}: expected a heading TextBlock bound to {binding}, found none.");

        foreach (var tb in headings)
        {
            Assert.True(tb.Contains("TextWrapping=\"Wrap\""), $"{view}: heading bound to {binding} must set TextWrapping=\"Wrap\":\n{tb}");
            Assert.DoesNotContain("TextTrimming=", tb);
        }
    }
}
