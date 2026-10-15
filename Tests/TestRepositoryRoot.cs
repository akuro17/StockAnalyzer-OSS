using System;
using System.IO;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// Locates the repository root for tests that read source or documentation files. This one file is compiled into every test
/// assembly (linked from the test projects), so the lookup exists once.
/// </summary>
internal static class TestRepositoryRoot
{
    private const string SolutionFileName = "StockAnalyzer.sln";

    /// <summary>The first folder at or above the test output that contains the solution file.</summary>
    internal static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"Repository root ({SolutionFileName}) not found above the test output.");
    }
}
