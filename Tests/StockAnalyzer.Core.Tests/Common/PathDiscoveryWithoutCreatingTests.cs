using System;
using System.IO;
using StockAnalyzer.Core.Common;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// The "WithoutCreating" resolvers must return what the creating resolvers return and must never create a folder.
/// They share the environment-variable collection because they read the UI-test variables like <c>ResolveDataPath</c> does.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class PathDiscoveryWithoutCreatingTests : IDisposable
{
    private const string SolutionFileName = "StockAnalyzer.sln";

    private readonly string _root = TestDataIsolationBootstrap.NewTempDirectory("PathDiscoveryReadOnly");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private string NewBinDirectory()
    {
        string bin = Path.Combine(_root, "bin", "net8.0");
        Directory.CreateDirectory(bin);
        return bin;
    }

    [Fact]
    public void SolutionRootAboveBase_ReturnsDataFolderBelowSolutionAndCreatesNothing()
    {
        string bin = NewBinDirectory();
        File.WriteAllText(Path.Combine(_root, SolutionFileName), string.Empty);
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);

        string resolved = PathDiscovery.ResolveDataPathWithoutCreating("Data/Daily", baseDirectory: bin);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Data", "Daily")), resolved);
        Assert.False(Directory.Exists(Path.Combine(_root, "Data")));
        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));
    }

    [Fact]
    public void DataFolderAboveBaseWithoutSolution_ReturnsThatDataFolderAndCreatesNothing()
    {
        string bin = NewBinDirectory();
        Directory.CreateDirectory(Path.Combine(_root, "Data"));
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);

        string resolved = PathDiscovery.ResolveDataPathWithoutCreating("Data/Weekly", baseDirectory: bin);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Data", "Weekly")), resolved);
        Assert.False(Directory.Exists(resolved));
        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));
    }

    [Fact]
    public void NothingFound_ReturnsPathBelowDataAndCreatesNothing()
    {
        string bin = NewBinDirectory();
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);

        string resolved = PathDiscovery.ResolveDataPathWithoutCreating("Data/Monthly", baseDirectory: bin);

        Assert.EndsWith(Path.Combine("Data", "Monthly"), resolved);
        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));
    }

    [Fact]
    public void AbsolutePath_IsReturnedVerbatimAndNotCreated()
    {
        string absolute = Path.Combine(_root, "elsewhere", "Daily");

        Assert.Equal(absolute, PathDiscovery.ResolveDataPathWithoutCreating(absolute));
        Assert.False(Directory.Exists(absolute));
    }

    [Fact]
    public void RelativeNonDataFolderBelowBase_IsReturnedWhenItHoldsMatchingFiles()
    {
        string bin = NewBinDirectory();
        string custom = Path.Combine(bin, "Custom");
        Directory.CreateDirectory(custom);
        File.WriteAllBytes(Path.Combine(custom, "x.parquet"), Array.Empty<byte>());

        Assert.Equal(custom, PathDiscovery.ResolveDataPathWithoutCreating("Custom", filePattern: "*.parquet", baseDirectory: bin));
    }

    [Theory]
    [InlineData("Data", null)]
    [InlineData("Data/Daily", "*.parquet")]
    [InlineData("Data/Weekly", null)]
    [InlineData("Data/Monthly", null)]
    [InlineData("Data/Metadata", null)]
    public void ExistingFolder_MatchesTheCreatingResolver(string requested, string? pattern)
    {
        string withoutCreating = PathDiscovery.ResolveDataPathWithoutCreating(requested, requested, pattern);
        if (!Directory.Exists(withoutCreating))
        {
            return; // A clean checkout has no Data folder; the creating resolver would create it, which a test must not do to the repository.
        }

        Assert.Equal(PathDiscovery.ResolveDataPath(requested, requested, pattern), withoutCreating);
    }

    [Fact]
    public void ExistingSourceAndDriverFolders_MatchTheCreatingResolvers()
    {
        const string fileName = "probe.json";
        string source = PathDiscovery.ResolveSourceIndicatorsPathWithoutCreating(fileName);
        if (Directory.Exists(Path.GetDirectoryName(source)))
        {
            Assert.Equal(PathDiscovery.ResolveSourceIndicatorsPath(fileName), source);
        }

        string drivers = PathDiscovery.ResolveDynamicPeriodDriversPathWithoutCreating(fileName);
        if (Directory.Exists(Path.GetDirectoryName(drivers)))
        {
            Assert.Equal(PathDiscovery.ResolveDynamicPeriodDriversPath(fileName), drivers);
        }
    }

    [Theory]
    [InlineData("..\\evil.json")]
    [InlineData("../evil.json")]
    public void SourceAndDriverResolvers_SanitiseTheFileName(string fileName)
    {
        string source = PathDiscovery.ResolveSourceIndicatorsPathWithoutCreating(fileName);
        string drivers = PathDiscovery.ResolveDynamicPeriodDriversPathWithoutCreating(fileName);

        Assert.Equal("SourceIndicators", Path.GetFileName(Path.GetDirectoryName(source)));
        Assert.Equal("DynamicPeriodDrivers", Path.GetFileName(Path.GetDirectoryName(drivers)));
    }

    [Fact]
    public void UiTestMode_ReturnsTheIsolatedPathWithoutCreatingTheRoot()
    {
        string? previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
        string? previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _root);

            string resolved = PathDiscovery.ResolveDataPathWithoutCreating("Data/Daily");

            Assert.Equal(PathDiscovery.ResolveUiTestDataPath(_root, "Data/Daily"), resolved);
            Assert.False(Directory.Exists(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, previousEnabled);
        }
    }
}
