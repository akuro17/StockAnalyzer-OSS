using System;
using System.IO;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// The managed Python environment installs only what the shipped scripts import. These tests keep the removed packages (arch, scikit-learn,
/// scipy, statsmodels, pywin32) from coming back into the defaults, and keep the scripts from importing them at module level without the defaults following.
/// scikit-learn is deliberately absent: only the optional ONNX training tools use it, and they install it themselves (OnnxTrainingPackages).
/// </summary>
public class PythonEssentialPackagesTests
{
    private static readonly string[] RemovedPackages = { "arch", "scikit-learn", "scipy", "statsmodels", "pywin32", "pandas-ta" };
    private static readonly string[] RuntimePackages = { "polars", "pandas", "yfinance", "pyarrow" };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (StockAnalyzer.sln) not found.");
    }

    [Fact]
    public void DefaultEssentialPackages_ExcludeRemovedPackagesAndKeepRuntimeOnes()
    {
        var packages = new PythonSettings().EssentialPackages;

        Assert.All(RemovedPackages, removed => Assert.DoesNotContain(removed, packages, StringComparer.OrdinalIgnoreCase));
        Assert.All(RuntimePackages, needed => Assert.Contains(needed, packages, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("server.py")]
    [InlineData("smart_screener.py")]
    public void ShippedScripts_DoNotImportRemovedPackages(string scriptName)
    {
        string scripts = Path.Combine(FindRepositoryRoot(), "StockAnalyzer.Core", "Scripts", scriptName);
        Assert.True(File.Exists(scripts), $"Script not found: {scripts}");

        // Module-level imports only (no leading whitespace): they run at startup and would break every request on a default install.
        var forbidden = new Regex(@"^(import|from)\s+(arch|sklearn|scipy|statsmodels|win32\w*|pywintypes|pandas_ta)\b", RegexOptions.Multiline);
        var match = forbidden.Match(File.ReadAllText(scripts));

        Assert.False(match.Success, $"{scriptName} imports a package at module level that is no longer installed by default: {match.Value.Trim()}");
    }
}
