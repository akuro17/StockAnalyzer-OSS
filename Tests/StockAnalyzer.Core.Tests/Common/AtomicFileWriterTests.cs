#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// A binary export must leave either the old file or the complete new one (IoHydrationWebAiReview, T4;
/// SA_ARCHITECTURE_RULES section 3, "Atomic Archive &amp; Binary Export Packaging").
/// </summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_atomic_file_" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_directory, "image.png");
    public AtomicFileWriterTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } }

    private string[] FileNames() => Directory.GetFileSystemEntries(_directory).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    [Fact]
    public async Task NewFile_IsCreated_WithoutTemporaryOrBackupFiles()
    {
        byte[] data = { 1, 2, 3, 4 };

        await AtomicFileWriter.WriteAllBytesAsync(Target, data);

        Assert.Equal(data, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "image.png" }, FileNames());
    }

    [Fact]
    public async Task ExistingFile_IsReplaced_WithoutLeavingABackup()
    {
        await File.WriteAllBytesAsync(Target, new byte[] { 9, 9, 9 });

        await AtomicFileWriter.WriteAllBytesAsync(Target, new byte[] { 1, 2 });

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "image.png" }, FileNames());
    }

    [Fact]
    public async Task MissingDirectory_IsCreated()
    {
        string nested = Path.Combine(_directory, "a", "b", "out.bin");

        await AtomicFileWriter.WriteAllBytesAsync(nested, new byte[] { 7 });

        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(nested));
    }

    [Fact]
    public async Task EmptyContent_WritesAnEmptyFile()
    {
        await AtomicFileWriter.WriteAllBytesAsync(Target, ReadOnlyMemory<byte>.Empty);

        Assert.True(File.Exists(Target));
        Assert.Empty(await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task Cancelled_LeavesTheTargetUnchanged_AndNoTemporaryFile()
    {
        byte[] original = { 5, 5, 5 };
        await File.WriteAllBytesAsync(Target, original);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFileWriter.WriteAllBytesAsync(Target, new byte[] { 1 }, cts.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "image.png" }, FileNames());
    }

    [Fact]
    public async Task FailedPublish_RemovesTheTemporaryFile_AndKeepsWhatWasThere()
    {
        Directory.CreateDirectory(Target); // a directory occupies the target name, so the final move fails

        await Assert.ThrowsAnyAsync<Exception>(() => AtomicFileWriter.WriteAllBytesAsync(Target, new byte[] { 1 }));

        Assert.Equal(new[] { "image.png" }, FileNames());
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public async Task ReaderOpenedBeforehand_KeepsTheOldContentWhole_WhileNewReadersSeeTheNewFile()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows is where an open handle decides whether Replace may run

        await File.WriteAllBytesAsync(Target, Enumerable.Repeat((byte)1, 4096).ToArray());
        using var reader = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        await AtomicFileWriter.WriteAllBytesAsync(Target, Enumerable.Repeat((byte)2, 8192).ToArray());

        var seenByOldReader = new byte[4096];
        reader.ReadExactly(seenByOldReader);
        Assert.All(seenByOldReader, b => Assert.Equal(1, b));
        Assert.Equal(8192, new FileInfo(Target).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidPath_IsRejected(string? path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => AtomicFileWriter.WriteAllBytesAsync(path!, new byte[] { 1 }));
    }
}
