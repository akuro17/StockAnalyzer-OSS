using System;
using System.IO;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Locates the repository root (the directory containing <c>StockAnalyzer.sln</c>) so tests can read
/// source files (*.axaml, locale JSON) that are not copied to the test output directory.
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> _root = new(Locate);

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

        throw new InvalidOperationException("Could not locate StockAnalyzer.sln from " + AppContext.BaseDirectory);
    }
}
