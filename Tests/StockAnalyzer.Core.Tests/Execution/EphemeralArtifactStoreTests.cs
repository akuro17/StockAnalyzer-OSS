#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Execution;
using Xunit;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>
/// The session folder of the ephemeral sandbox: a 12-character id, and a start that never adopts a folder that already exists (a live session of
/// another process or an abandoned one). The throw-away root is below the real sandbox root, as in the gateway tests.
/// </summary>
public sealed class EphemeralArtifactStoreTests : IDisposable
{
    private const string MarkerFileName = "marker.txt";

    private readonly string _root = Path.Combine(EphemeralSandbox.Root, "storetest-" + Guid.NewGuid().ToString("N"));
    private readonly List<EphemeralArtifactStore> _stores = new();
    private readonly List<FileStream> _held = new();

    public void Dispose()
    {
        foreach (FileStream stream in _held)
        {
            stream.Dispose();
        }

        foreach (EphemeralArtifactStore store in _stores)
        {
            store.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EphemeralArtifactStore CreateStore(Func<string>? newSessionId = null)
    {
        var store = new EphemeralArtifactStore(_root, new SandboxedExecutionSettings(), NullLogger.Instance, newSessionId);
        _stores.Add(store);
        return store;
    }

    private string MakeFolder(string name, bool withLockFile = false, bool holdLock = false)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MarkerFileName), "someone else's file");
        if (holdLock)
        {
            _held.Add(new FileStream(Path.Combine(folder, EphemeralArtifactStore.SessionLockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        else if (withLockFile)
        {
            File.WriteAllText(Path.Combine(folder, EphemeralArtifactStore.SessionLockFileName), string.Empty);
        }

        return folder;
    }

    private static bool LockIsHeld(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, EphemeralArtifactStore.SessionLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public void TheSessionIdIsTwelveHexCharactersAndDiffersFromStoreToStore()
    {
        Assert.Equal(12, EphemeralArtifactStore.SessionIdLength);

        string[] ids = Enumerable.Range(0, 20).Select(_ => CreateStore().SessionId).ToArray();

        Assert.All(ids, id =>
        {
            Assert.Equal(EphemeralArtifactStore.SessionIdLength, id.Length);
            Assert.Matches("^[0-9a-f]{12}$", id);
        });
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void AStartCreatesOneFolderWithItsHeldLockAndALaterCallDoesNothing()
    {
        EphemeralArtifactStore store = CreateStore();
        string id = store.SessionId;

        store.EnsureSession();
        store.EnsureSession();

        Assert.Equal(id, store.SessionId);
        Assert.Equal(new[] { Path.Combine(_root, id) }, Directory.EnumerateDirectories(_root).ToArray());
        Assert.True(LockIsHeld(store.SessionDirectory));
        store.Dispose();
        Assert.False(Directory.Exists(Path.Combine(_root, id)));
    }

    [Theory]
    [InlineData(false, false)] // an abandoned folder without a lock file
    [InlineData(true, false)] // an abandoned folder with a lock file nobody holds
    [InlineData(false, true)] // a live session: its lock is held
    public void AFolderThatAlreadyExistsIsNeverAdoptedAndTheNextIdIsUsed(bool withLockFile, bool holdLock)
    {
        EphemeralArtifactStore store = CreateStore();
        string taken = store.SessionId;
        string foreign = MakeFolder(taken, withLockFile, holdLock);

        store.EnsureSession();

        Assert.NotEqual(taken, store.SessionId);
        Assert.Equal(EphemeralArtifactStore.SessionIdLength, store.SessionId.Length);
        Assert.True(LockIsHeld(store.SessionDirectory));
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(foreign, MarkerFileName)));
        Assert.Equal(withLockFile || holdLock, File.Exists(Path.Combine(foreign, EphemeralArtifactStore.SessionLockFileName)));
        if (holdLock)
        {
            Assert.True(LockIsHeld(foreign)); // the other session's lock is still held
        }
    }

    [Fact]
    public void ADisposedStoreLeavesTheFolderItDidNotAdoptAlone()
    {
        EphemeralArtifactStore store = CreateStore();
        string foreign = MakeFolder(store.SessionId);
        store.EnsureSession();
        string own = store.SessionDirectory;

        store.Dispose();

        Assert.False(Directory.Exists(own));
        Assert.True(File.Exists(Path.Combine(foreign, MarkerFileName)));
    }

    [Fact]
    public void WhenEveryAttemptCollidesAnIOExceptionIsThrownNothingIsCreatedAndTheStoreCanBeDisposed()
    {
        const string Taken = "aaaaaaaaaaaa";
        string foreign = MakeFolder(Taken);
        EphemeralArtifactStore store = CreateStore(() => Taken);

        Assert.Throws<IOException>(store.EnsureSession);

        Assert.Equal(new[] { foreign }, Directory.EnumerateDirectories(_root).ToArray());
        Assert.Equal(new[] { MarkerFileName }, Directory.EnumerateFiles(foreign).Select(Path.GetFileName).ToArray());
        store.Dispose();
        Assert.True(File.Exists(Path.Combine(foreign, MarkerFileName)));
    }

    [Fact]
    public void TheOrphanCleanUpStillRemovesAnOldThirtyTwoCharacterFolderAndKeepsAHeldOne()
    {
        string oldName = new('d', 32);
        string heldName = new('e', 32);
        string abandoned = MakeFolder(oldName, withLockFile: true);
        string live = MakeFolder(heldName, holdLock: true);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-1000));
        Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddHours(-1000));
        EphemeralArtifactStore store = CreateStore();

        int deleted = store.CleanOrphanSessions(DateTime.UtcNow);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(live));
    }
}
