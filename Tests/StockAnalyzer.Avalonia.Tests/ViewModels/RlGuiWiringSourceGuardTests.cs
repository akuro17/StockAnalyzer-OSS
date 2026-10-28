#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Source guards for RL window wiring that only exists in AXAML / the dialog factory: a window the headless tests never open must not
/// silently lose its dialog service, show a button that cannot work, or drop the scope readout row of the main chart.
/// The AXAML guards read the file as XML (elements and attributes), so they do not depend on the order or the line breaks of the attributes.
/// </summary>
public sealed class RlGuiWiringSourceGuardTests
{
    private const string CommandAttribute = "Command";
    private const string VisibleAttribute = "IsVisible";
    private const string AutomationIdAttribute = "AutomationProperties.AutomationId";
    private const string GridColumnAttribute = "Grid.Column";
    private const string GridRowAttribute = "Grid.Row";
    private const string RowDefinitionsAttribute = "RowDefinitions";

    private static string Source(params string[] relative) =>
        File.ReadAllText(Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", Path.Combine(relative)));

    private static XDocument Xaml(params string[] relative) => XDocument.Parse(Source(relative));

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    /// <summary>Every element of the given local name whose attribute value contains the text.</summary>
    private static List<XElement> Elements(XDocument xaml, string element, string attribute, string valueContains) =>
        xaml.Descendants()
            .Where(e => e.Name.LocalName == element && Attr(e, attribute) is { } value && value.Contains(valueContains, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void TheRlTrainingDialogIsCreatedWithTheDialogService()
    {
        string source = Source("Services", "DialogService.ReinforcementLearning.cs");

        Assert.Matches(@"new RlTrainingDialogViewModel\([^;]*dialogService:\s*this\b", source);
    }

    [Fact]
    public void TheRlTrainingWindowHasNoImportCheckpointButton()
    {
        XDocument xaml = Xaml("Views", "RlTrainingWindow.axaml");

        Assert.Empty(Elements(xaml, "Button", CommandAttribute, "ImportCheckpointCommand"));
        Assert.Single(Elements(xaml, "Button", CommandAttribute, "ExportCheckpointCommand"));
    }

    [Fact]
    public void TheModelArchiveButtonsAreExportWhenAModelIsSelectedAndAlwaysVisibleImport()
    {
        XDocument xaml = Xaml("Views", "RlTrainingWindow.axaml");

        XElement export = Assert.Single(Elements(xaml, "Button", CommandAttribute, "ExportGenerationCommand"));
        XElement import = Assert.Single(Elements(xaml, "Button", CommandAttribute, "ImportGenerationCommand"));

        Assert.Equal("{Binding HasSelectedModel}", Attr(export, VisibleAttribute));
        Assert.Contains("RlUiConstants.GenerationImport", Attr(import, AutomationIdAttribute));
        Assert.Null(Attr(import, VisibleAttribute));
    }

    [Fact]
    public void TheGenerationListShowsTheOverfitGapColumn()
    {
        XDocument xaml = Xaml("Views", "RlTrainingWindow.axaml");

        XElement gap = Assert.Single(Elements(xaml, "TextBlock", "Text", "OverfitGapDisplay"));
        XElement badge = Assert.Single(Elements(xaml, "Border", "Background", "QualityBadgeColor"));

        Assert.Equal("3", Attr(gap, GridColumnAttribute));
        Assert.Equal("4", Attr(badge, GridColumnAttribute));
    }

    [Fact]
    public void TheGenerationBadgeThresholdsComeFromTheNamedConstants()
    {
        string source = Source("ViewModels", "RlTrainingDialogViewModel.cs");

        Assert.DoesNotContain("0.05m", source);
        Assert.DoesNotContain("0.10m", source);
        Assert.Contains("RlUiConstants.GenerationRobustGapMaximum", source);
        Assert.Contains("RlUiConstants.GenerationOverfitGapMinimum", source);
    }

    [Fact]
    public void TheMainWindowChartGridHasARowForTheScopeReadout()
    {
        XDocument xaml = Xaml("Views", "MainWindow.axaml");

        XElement readout = Assert.Single(Elements(xaml, "TextBlock", AutomationIdAttribute, "RlUiConstants.ScopeReadout"));
        XElement grid = readout.Parent ?? throw new InvalidOperationException("The scope readout has no parent element.");
        int rowCount = Attr(grid, RowDefinitionsAttribute)!.Split(',').Length;

        Assert.True(int.TryParse(Attr(readout, GridRowAttribute), out int row));
        Assert.Equal(rowCount - 1, row);
    }
}
