using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using StockAnalyzer.Core.Common;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// D-2' Run 1 / S1: the start-up probe of the data folder. It shares the collection of the tests that change the UI-automation
/// environment variables and the application base directory, because some tests here do the same (nothing else runs beside them).
/// </summary>
[Collection("PathDiscovery UIA environment")]
public class DataStorageProbeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_probe_" + Guid.NewGuid().ToString("N"));

    public DataStorageProbeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    private static string[] ProbeFilesIn(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, DataStorageProbe.ProbeFilePrefix + "*") : Array.Empty<string>();

    // ---- writable ---------------------------------------------------------------------------------------------

    [Fact]
    public void WritableFolder_IsWritable_AndLeavesNoProbeFile()
    {
        var status = DataStorageProbe.Evaluate(_folder);

        Assert.True(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.None, status.Reason);
        Assert.Equal(_folder, status.DataRoot);
        Assert.Null(status.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void MissingButCreatableFolder_IsCreatedAndWritable()
    {
        var root = Path.Combine(_folder, "Data");

        var status = DataStorageProbe.Evaluate(root);

        Assert.True(status.IsWritable);
        Assert.True(Directory.Exists(root));
        Assert.Empty(ProbeFilesIn(root));
    }

    [Fact]
    public void Probe_DoesNotTouchExistingFiles()
    {
        var existing = Path.Combine(_folder, "settings.json");
        File.WriteAllText(existing, "keep");

        DataStorageProbe.Evaluate(_folder);

        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Single(Directory.GetFileSystemEntries(_folder));
    }

    // ---- not creatable / not writable ------------------------------------------------------------------------

    [Fact]
    public void ParentIsAFile_RootCannotBeCreated()
    {
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");

        var status = DataStorageProbe.Evaluate(Path.Combine(blocker, "Data"));

        Assert.False(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.DataRootCannotBeCreated, status.Reason);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public void RootWithAnIllegalCharacter_IsReadOnlyMode_NotAStartupCrash()
    {
        // Directory.CreateDirectory rejects a NUL character with an ArgumentException; that must end in the read-only answer like every other
        // storage failure, otherwise a bad configured path would show the start-up error window instead of starting.
        var status = DataStorageProbe.Evaluate(Path.Combine(_folder, "bad\0name"));

        Assert.False(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.DataRootCannotBeCreated, status.Reason);
        Assert.Contains(nameof(ArgumentException), status.Detail);
    }

    [Fact]
    public void ReadOnlyInstallFolder_MissingDataRootCannotBeCreated()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();

        var status = DataStorageProbe.Evaluate(fixture.DataRoot);

        Assert.False(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.DataRootCannotBeCreated, status.Reason);
        Assert.Equal(fixture.DataRoot, status.DataRoot);
        Assert.False(Directory.Exists(fixture.DataRoot));
    }

    [Fact]
    public void ExistingFolderThatRefusesNewFiles_IsNotWritable_AndLeavesNoProbeFile()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();

        var status = DataStorageProbe.Evaluate(fixture.Root);

        Assert.False(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.DataRootNotWritable, status.Reason);
        Assert.Empty(ProbeFilesIn(fixture.Root));
    }

    [Fact]
    public void FailureDetail_NamesTheExceptionAndHidesTheUserProfilePath()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();

        var status = DataStorageProbe.Evaluate(fixture.Root);

        Assert.StartsWith(nameof(UnauthorizedAccessException), status.Detail);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(profile, status.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- probe name collision ---------------------------------------------------------------------------------

    [Fact]
    public void FirstProbeNameTaken_RetriesOnceWithANewName_AndKeepsTheForeignFile()
    {
        var taken = Path.Combine(_folder, DataStorageProbe.ProbeFilePrefix + "taken");
        File.WriteAllText(taken, "not ours");
        var names = new[] { Path.GetFileName(taken), DataStorageProbe.ProbeFilePrefix + "free" };
        var next = 0;

        var status = DataStorageProbe.Evaluate(_folder, () => names[next++]);

        Assert.True(status.IsWritable);
        Assert.Equal(2, next);
        Assert.Equal("not ours", File.ReadAllText(taken));
        Assert.Single(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void BothProbeNamesTaken_IsNotWritable_AndNeitherForeignFileIsDeleted()
    {
        var first = Path.Combine(_folder, DataStorageProbe.ProbeFilePrefix + "one");
        var second = Path.Combine(_folder, DataStorageProbe.ProbeFilePrefix + "two");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        var names = new[] { Path.GetFileName(first), Path.GetFileName(second) };
        var next = 0;

        var status = DataStorageProbe.Evaluate(_folder, () => names[next++]);

        Assert.False(status.IsWritable);
        Assert.Equal(DataStorageReadOnlyReason.DataRootNotWritable, status.Reason);
        Assert.Equal(2, next); // exactly one retry
        Assert.Equal("a", File.ReadAllText(first));
        Assert.Equal("b", File.ReadAllText(second));
    }

    // ---- delete failure alone ---------------------------------------------------------------------------------

    [Fact]
    public void ProbeFileThatCannotBeDeleted_StillCountsAsWritable_WithAWarningDetail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // reproduced with an ACL entry; the permission-bit equivalent has no "may create but not delete" state
        }

        var info = new DirectoryInfo(_folder);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(
            user,
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var status = DataStorageProbe.Evaluate(_folder);

            Assert.True(status.IsWritable);
            Assert.Equal(DataStorageReadOnlyReason.None, status.Reason);
            Assert.Contains("could not be deleted", status.Detail);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    // ---- arguments --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankRoot_Throws(string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => DataStorageProbe.Evaluate(root!));
    }

    [Fact]
    public void State_HoldsTheStatusItWasGiven()
    {
        var status = new DataStorageStatus(false, DataStorageReadOnlyReason.DataRootNotWritable, "x", "d");

        Assert.Same(status, new DataStorageState(status).Current);
        Assert.Throws<ArgumentNullException>(() => new DataStorageState(null!));
    }

    // ---- the data root the application actually probes --------------------------------------------------------

    [Fact]
    public void ApplicationDataRoot_UnderAReadOnlyInstallFolder_IsTheFixturesDataFolder_AndReadOnly()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            var dataRoot = PathDiscovery.ResolveDataRootWithoutCreating();
            var status = DataStorageProbe.Evaluate(dataRoot);

            Assert.Equal(fixture.DataRoot, dataRoot);
            Assert.False(status.IsWritable);
            Assert.Equal(DataStorageReadOnlyReason.DataRootCannotBeCreated, status.Reason);
        }

        Assert.NotEqual(fixture.Root, Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory)); // restored
    }

    [Fact]
    public void ApplicationDataRoot_UnderAWritableInstallFolder_IsWritable_AndTheFolderIsCreated()
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            var status = DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating());

            Assert.True(status.IsWritable);
        }

        Assert.True(Directory.Exists(fixture.DataRoot));
        Assert.Empty(ProbeFilesIn(fixture.DataRoot));
    }

    [Fact]
    public void UiAutomation_ProbesTheIsolatedRoot_NotTheApplicationFolder()
    {
        var isolated = Path.Combine(_folder, "isolated");
        var previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
        var previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, isolated);

            var dataRoot = PathDiscovery.ResolveDataRootWithoutCreating();
            var status = DataStorageProbe.Evaluate(dataRoot);

            Assert.StartsWith(isolated, dataRoot, StringComparison.OrdinalIgnoreCase);
            Assert.True(status.IsWritable);
            Assert.Equal(dataRoot, status.DataRoot);
            Assert.Empty(ProbeFilesIn(dataRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, previousEnabled);
        }
    }

    [Fact]
    public void UiAutomation_ReadOnlyIsolatedRoot_IsReportedReadOnly()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        var previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
        var previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, fixture.Root);

            var status = DataStorageProbe.Evaluate(PathDiscovery.ResolveDataRootWithoutCreating());

            Assert.False(status.IsWritable);
            Assert.True(status.Reason is DataStorageReadOnlyReason.DataRootCannotBeCreated or DataStorageReadOnlyReason.DataRootNotWritable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, previousRoot);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, previousEnabled);
        }
    }

    // ---- the fixture itself -----------------------------------------------------------------------------------

    [Fact]
    public void Fixture_ReadOnlyRefusesNewFilesAndFolders_AndCleansUpAfterItself()
    {
        string root;
        using (var fixture = ReadOnlyDataRootFixture.Create())
        {
            root = fixture.Root;
            Assert.True(fixture.IsReadOnly);
            Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(root, "a.txt"), "x"));
            Assert.ThrowsAny<Exception>(() => Directory.CreateDirectory(Path.Combine(root, "sub")));
        }

        Assert.False(Directory.Exists(root)); // the deny entry was removed before deleting
    }

    [Fact]
    public void Fixture_BaseDirectorySwap_IsRestoredEvenWhenTheScopeIsNotDisposed()
    {
        var before = AppDomain.CurrentDomain.BaseDirectory;
        using (var fixture = ReadOnlyDataRootFixture.Create(readOnly: false))
        {
            fixture.UseAsBaseDirectory();
            Assert.Equal(fixture.Root, Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory));
        }

        Assert.Equal(before, AppDomain.CurrentDomain.BaseDirectory);
    }
}
