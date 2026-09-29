using System;
using System.IO;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Common;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Guards the single source of truth for the link icons: the SVG files under Assets/Icons are the design
/// source, <see cref="SharedIconGeometries"/> holds the path data actually rendered by PathIcon, and no view
/// may re-declare the same geometry inline.
/// </summary>
public class LinkIconGeometrySsotTests
{
    private const string SolutionMarker = "StockAnalyzer.sln";

    /// <summary>Resolves a repository file anchored on the solution root (never on the current working directory).</summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, SolutionMarker)))
        {
            dir = dir.Parent;
        }
        Assert.True(dir != null, $"Solution root ('{SolutionMarker}') not found above '{AppContext.BaseDirectory}'.");

        var path = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(path), $"Missing repository file '{relativePath}'.");
        return path;
    }

    private static string PathData(string relativeSvgPath)
    {
        var svg = File.ReadAllText(FindRepoFile(relativeSvgPath));
        var match = Regex.Match(svg, "<path[^>]*\\sd=\"([^\"]+)\"");
        Assert.True(match.Success, $"No <path d=...> in {relativeSvgPath}");
        return match.Groups[1].Value;
    }

    [Fact]
    public void AddLink_Constant_MatchesSvgSource()
    {
        Assert.Equal(PathData(Path.Combine("StockAnalyzer.Avalonia", "Assets", "Icons", "add_link.svg")), SharedIconGeometries.AddLink);
    }

    [Fact]
    public void LinkOff_Constant_MatchesSvgSource()
    {
        Assert.Equal(PathData(Path.Combine("StockAnalyzer.Avalonia", "Assets", "Icons", "link_off.svg")), SharedIconGeometries.LinkOff);
    }

    [Fact]
    public void NoteTimelineView_UsesTheSharedLinkOffGeometry_InsteadOfAnInlineCopy()
    {
        var xaml = File.ReadAllText(FindRepoFile(Path.Combine("StockAnalyzer.Avalonia", "Views", "Notes", "NoteTimelineView.axaml")));

        Assert.DoesNotContain(SharedIconGeometries.LinkOff, xaml);
        Assert.Contains("SharedIconGeometries.LinkOff", xaml);
    }
}
