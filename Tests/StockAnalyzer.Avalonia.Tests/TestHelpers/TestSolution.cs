using System;
using System.IO;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// Single source for locating the repository root (the directory that contains
/// <c>StockAnalyzer.sln</c>) from a test run. Several tests need to read source files
/// (<c>*.axaml</c>, <c>Assets/Icons/*.svg</c>, locale JSON) that are not copied to the test
/// output directory; they previously each carried their own verbatim copy of this walk.
/// </summary>
internal static class TestSolution
{
    private static readonly Lazy<string> _root = new(Locate);

    /// <summary>Absolute path of the directory containing <c>StockAnalyzer.sln</c>.</summary>
    public static string Root => _root.Value;

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("StockAnalyzer.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate StockAnalyzer.sln from " + AppContext.BaseDirectory);
    }
}
