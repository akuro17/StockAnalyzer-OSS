using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// D-2' Run 2 / S2: the folder helpers no longer redirect to the OS temp folder or the base directory when the data folder cannot
/// be created. They return the (uncreated) path below <c>Data</c>; callers fail at their own write and read "no file".
/// These tests swap the application base directory and change environment variables, so they share the non-parallel collection.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public class PathDiscoveryNoRedirectTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_noredirect_" + Guid.NewGuid().ToString("N"));

    public PathDiscoveryNoRedirectTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void DataRoot_IsTheDataFolderOfTheBaseDirectory_AndNothingIsCreated()
    {
        var root = PathDiscovery.ResolveDataRootWithoutCreating(_folder);

        Assert.Equal(PathDiscovery.ResolveDataPathWithoutCreating(null, "Data", baseDirectory: _folder), root);
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "Data")), Path.GetFullPath(root));
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    /// <summary>The ten folder routes of E3 plus the model generations folder: (name, resolver, folder below Data that must be created).</summary>
    public static IEnumerable<object[]> Routes() => new[]
    {
        new object[] { "Config", "Config" },
        new object[] { "Portfolios", "Portfolios" },
        new object[] { "BacktestReportSettings", "Backtest" },
        new object[] { "BacktestDefaultSettings", "Backtest" },
        new object[] { "BacktestReportExport", Path.Combine("Backtest", "Reports") },
        new object[] { "IndicatorDefaults", "IndicatorDefaults" },
        new object[] { "SourceIndicators", "SourceIndicators" },
        new object[] { "DynamicPeriodDrivers", "DynamicPeriodDrivers" },
        new object[] { "Templates", "Templates" },
        new object[] { "Experiments", Path.Combine("Experiments", "run-1") },
        new object[] { "ModelGenerations", "Models" },
    };

    private static string Resolve(string route) => route switch
    {
        "Config" => PathDiscovery.ResolveConfigPath("x.json"),
        "Portfolios" => PathDiscovery.ResolvePortfolioPath("x.json"),
        "BacktestReportSettings" => PathDiscovery.ResolveBacktestReportSettingsPath("x.json"),
        "BacktestDefaultSettings" => PathDiscovery.ResolveBacktestDefaultSettingsPath("x.json"),
        "BacktestReportExport" => PathDiscovery.ResolveBacktestReportExportPath("x.json"),
        "IndicatorDefaults" => PathDiscovery.ResolveIndicatorDefaultsPath("x.json"),
        "SourceIndicators" => PathDiscovery.ResolveSourceIndicatorsPath("x.json"),
        "DynamicPeriodDrivers" => PathDiscovery.ResolveDynamicPeriodDriversPath("x.json"),
        "Templates" => Path.Combine(PathDiscovery.ResolveTemplatesDirectory(), "x.json"),
        "Experiments" => Path.Combine(PathDiscovery.ResolveExperimentsDirectory("run-1"), "x.json"),
        "ModelGenerations" => PathDiscovery.ResolveModelGenerationsDirectory(),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void WritableInstall_ReturnsThePathBelowData_AndCreatesTheFolder_AsBefore(string route, string folderBelowData)
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            var path = Resolve(route);

            Assert.StartsWith(fixture.DataRoot, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(Path.Combine(fixture.DataRoot, folderBelowData.Split(Path.DirectorySeparatorChar)[0])));
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void ReadOnlyInstall_StaysBelowData_NeverRedirectsToTempOrTheBaseDirectory_AndDoesNotThrow(string route, string folderBelowData)
    {
        _ = folderBelowData;
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            var path = Resolve(route);

            Assert.StartsWith(fixture.DataRoot, path, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(fixture.DataRoot)); // nothing could be created, and nothing was created elsewhere in its place
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        }
    }

    [Fact]
    public void ReadOnlyInstall_ResolveDataPath_StillReturnsTheUncreatedPath_Unchanged()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            Assert.Equal(fixture.DataRoot, PathDiscovery.ResolveDataPath(null, "Data"));
        }
    }

    [Fact]
    public void FolderBlockedByAFile_NormalRun_ReturnsTheBlockedPathWithoutThrowing()
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            Directory.CreateDirectory(fixture.DataRoot);
            File.WriteAllText(Path.Combine(fixture.DataRoot, "Config"), "a file where the folder should be");

            var path = PathDiscovery.ResolveConfigPath("x.json");

            Assert.Equal(Path.Combine(fixture.DataRoot, "Config", "x.json"), path);
        }
    }

    [Fact]
    public void FolderBlockedByAFile_UnderUiAutomation_StillThrows()
    {
        var previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
        var previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _folder);
            var dataRoot = PathDiscovery.ResolveDataPath(null, "Data");
            File.WriteAllText(Path.Combine(dataRoot, "Config"), "a file where the folder should be");

            Assert.ThrowsAny<IOException>(() => PathDiscovery.ResolveConfigPath("x.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, previousEnabled);
        }
    }

    [Fact]
    public void WritableInstall_ExistingFolder_IsReturnedAsIs()
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            var existing = Path.Combine(fixture.DataRoot, "Config");
            Directory.CreateDirectory(existing);

            Assert.Equal(Path.Combine(existing, "x.json"), PathDiscovery.ResolveConfigPath("x.json"));
        }
    }
}
