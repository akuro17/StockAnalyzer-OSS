using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// A throw-away "install folder" in which nothing can be created, to reproduce an application whose <c>Data</c> folder is not writable
/// (for example installed below a read-only location). <see cref="Root"/> plays the application base directory: its <c>Data</c> sub-folder
/// does not exist and cannot be created. With <c>readOnly: false</c> the same folder is writable (the control for a measurement).
/// <para>
/// Windows: a Deny entry for the current user (CreateFiles, CreateDirectories, extended-attribute writes). Elsewhere: permission bits.
/// Running as an account that ignores permissions (root) cannot reproduce the situation, so <see cref="Create"/> proves the folder
/// really refuses a new file and throws otherwise, instead of letting a test pass vacuously.
/// </para>
/// <para>
/// <see cref="UseAsBaseDirectory"/> swaps the PROCESS-WIDE application base directory (what <c>AppDomain.CurrentDomain.BaseDirectory</c>
/// reports), because that is where <c>PathDiscovery</c> looks for <c>Data</c>. A test that uses it must run in a collection with
/// parallelization disabled. <see cref="Dispose"/> restores the ACL BEFORE deleting, otherwise the folder could never be removed.
/// This one file is compiled into every test assembly that needs it (linked from the test projects).
/// </para>
/// </summary>
internal sealed class ReadOnlyDataRootFixture : IDisposable
{
    private const string BaseDirectoryDataKey = "APP_CONTEXT_BASE_DIRECTORY";
    private const string DataFolderName = "Data";
    private const string PermissionProbeFileName = ".permission_probe";

    private readonly bool _readOnly;
    private object? _previousBaseDirectory;
    private bool _baseDirectorySwapped;
    private bool _disposed;

    private ReadOnlyDataRootFixture(string root, bool readOnly)
    {
        Root = root;
        _readOnly = readOnly;
    }

    /// <summary>The folder that plays the application base directory.</summary>
    internal string Root { get; }

    /// <summary>The <c>Data</c> folder below <see cref="Root"/>; it does not exist until something creates it.</summary>
    internal string DataRoot => Path.Combine(Root, DataFolderName);

    internal bool IsReadOnly => _readOnly;

    internal static ReadOnlyDataRootFixture Create(bool readOnly = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "ReadOnlyDataRoot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new ReadOnlyDataRootFixture(root, readOnly);
        if (readOnly)
        {
            try
            {
                fixture.DenyWriting();
                fixture.AssertWritingIsRefused();
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        return fixture;
    }

    /// <summary>Makes <see cref="Root"/> the application base directory until the returned scope (or the fixture) is disposed.</summary>
    internal IDisposable UseAsBaseDirectory()
    {
        if (_baseDirectorySwapped)
        {
            throw new InvalidOperationException("The base directory is already swapped by this fixture.");
        }

        _previousBaseDirectory = AppContext.GetData(BaseDirectoryDataKey);
        AppContext.SetData(BaseDirectoryDataKey, Root + Path.DirectorySeparatorChar);
        _baseDirectorySwapped = true;

        if (!string.Equals(Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory), Root, StringComparison.OrdinalIgnoreCase))
        {
            RestoreBaseDirectory();
            throw new InvalidOperationException("The application base directory could not be swapped; the measurement would not describe the fixture.");
        }

        return new BaseDirectoryScope(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RestoreBaseDirectory();
        if (_readOnly)
        {
            AllowWriting();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // best-effort: the OS temp cleaner removes the rest
        }
    }

    private void RestoreBaseDirectory()
    {
        if (!_baseDirectorySwapped)
        {
            return;
        }

        AppContext.SetData(BaseDirectoryDataKey, _previousBaseDirectory);
        _baseDirectorySwapped = false;
    }

    private void AssertWritingIsRefused()
    {
        string probe = Path.Combine(Root, PermissionProbeFileName);
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        throw new InvalidOperationException("This account can still write into the 'read-only' folder (permissions are not enforced, e.g. root): the read-only measurement cannot be reproduced here.");
    }

    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows user has no SID.");

    private static FileSystemAccessRule DenyRule() => new(
        CurrentUser(),
        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteExtendedAttributes,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Deny);

    private void DenyWriting()
    {
        if (OperatingSystem.IsWindows())
        {
            DenyWritingWindows();
        }
        else
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private void AllowWriting()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                AllowWritingWindows();
            }
            else if (Directory.Exists(Root))
            {
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // best-effort: nothing more can be done; the leftover folder is under the OS temp folder
        }
    }

    [SupportedOSPlatform("windows")]
    private void DenyWritingWindows()
    {
        var info = new DirectoryInfo(Root);
        var security = info.GetAccessControl();
        security.AddAccessRule(DenyRule());
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private void AllowWritingWindows()
    {
        var info = new DirectoryInfo(Root);
        if (!info.Exists)
        {
            return;
        }

        var security = info.GetAccessControl();
        security.RemoveAccessRule(DenyRule());
        info.SetAccessControl(security);
    }

    private sealed class BaseDirectoryScope : IDisposable
    {
        private readonly ReadOnlyDataRootFixture _owner;
        public BaseDirectoryScope(ReadOnlyDataRootFixture owner) => _owner = owner;
        public void Dispose() => _owner.RestoreBaseDirectory();
    }
}

/// <summary>
/// The one definition, for every test project, of "a data-storage state that says writable / read-only" (the answer of the start-up probe,
/// built by hand). Tests that need the REAL answer for a REAL folder use <see cref="ReadOnlyDataRootFixture"/> and the probe instead.
/// </summary>
internal static class TestDataStorageState
{
    internal const string DefaultDataRoot = "/app/Data";

    internal static IDataStorageState WritableState() =>
        new DataStorageState(new DataStorageStatus(true, DataStorageReadOnlyReason.None, DefaultDataRoot, null));

    internal static IDataStorageState ReadOnlyState(DataStorageReadOnlyReason reason = DataStorageReadOnlyReason.DataRootNotWritable, string dataRoot = DefaultDataRoot) =>
        new DataStorageState(new DataStorageStatus(false, reason, dataRoot, "x"));

    /// <summary>For tests that run the same case for both answers.</summary>
    internal static IDataStorageState StateOf(bool writable) => writable ? WritableState() : ReadOnlyState();
}
