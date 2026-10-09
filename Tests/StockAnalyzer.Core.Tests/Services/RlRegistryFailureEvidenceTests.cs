#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    private const int ConcurrentFailureRegistryCount = 8;
    private const int FailuresPerRegistry = 3;
    private static readonly TimeSpan HeldStoreTimeout = TimeSpan.FromMilliseconds(100);
    private string CoordinationFolder => Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder);
    private string FailureEvidencePath => Path.Combine(CoordinationFolder, ModelGenerationRegistry.Coordination.FailureEvidenceFileName(Environment.ProcessId));

    /// <summary>Registries are created first (their constructors enter the store); then the store lock is held so later calls time out and record failure evidence.</summary>
    private FileStream HoldStoreLock()
        => new(Path.Combine(CoordinationFolder, ModelGenerationRegistry.Coordination.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    private ModelGenerationRegistry TimeoutRegistry() => new(RegistryRoot) { StoreLockTimeout = HeldStoreTimeout };

    [Fact]
    public async Task M05_ConcurrentFailureRecordersLeaveOnlyCompleteRecordsAndNoTemporaryFile()
    {
        var registries = Enumerable.Range(0, ConcurrentFailureRegistryCount).Select(_ => TimeoutRegistry()).ToArray();
        using var held = HoldStoreLock();
        int partialReads = 0; bool writersDone = false;
        // A reader that opens the record at any moment must never see a truncated or partial document.
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref writersDone))
            {
                try
                {
                    using var stream = new FileStream(FailureEvidencePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var _ = JsonDocument.Parse(stream);
                }
                catch (JsonException) { Interlocked.Increment(ref partialReads); }
                catch (IOException) { } // absent or momentarily owned by the atomic replace
                catch (UnauthorizedAccessException) { }
            }
        });
        try
        {
            await Task.WhenAll(registries.Select(registry => Task.Run(() =>
            {
                for (int i = 0; i < FailuresPerRegistry; i++) Assert.Throws<TimeoutException>(() => registry.CaptureRlSnapshot());
            })));
        }
        finally { Volatile.Write(ref writersDone, true); await reader; foreach (var registry in registries) registry.Dispose(); }
        Assert.Equal(0, partialReads);
        using var record = JsonDocument.Parse(File.ReadAllText(FailureEvidencePath));
        Assert.Equal("transaction", record.RootElement.GetProperty("operation").GetString());
        Assert.Equal(Environment.ProcessId, record.RootElement.GetProperty("process_id").GetInt32());
        Assert.Equal(nameof(TimeoutException), record.RootElement.GetProperty("error_type").GetString());
        Assert.Empty(Directory.GetFiles(CoordinationFolder, ModelGenerationRegistry.Coordination.FailureEvidencePrefix + "*" + AtomicJsonFile.TemporarySuffix));
    }

    [Fact]
    public void M05_FailedEvidenceSavePreservesThePreviousRecordAndNeverReplacesThePrimaryError()
    {
        using var first = TimeoutRegistry(); using var second = TimeoutRegistry();
        using var held = HoldStoreLock();
        Assert.Throws<TimeoutException>(() => first.CaptureRlSnapshot());
        string previous = File.ReadAllText(FailureEvidencePath);
        Directory.CreateDirectory(FailureEvidencePath + AtomicJsonFile.TemporarySuffix); // the temporary file cannot be created, so the save must fail
        Assert.Throws<TimeoutException>(() => second.CaptureRlSnapshot());
        Assert.Equal(previous, File.ReadAllText(FailureEvidencePath));
    }

    /// <summary>Outcome-level check: on Windows a junction cannot be distinguished from a failed temp write by outcome alone, so this asserts the safety result, not the guard's call path.</summary>
    [Fact]
    public void M05_LinkedAuxiliaryEvidencePathWritesNothingOutsideTheRegistryRootAndKeepsThePrimaryError()
    {
        if (!OperatingSystem.IsWindows()) return; // directory junctions need no privilege on Windows; other platforms rely on the same reparse-point guard
        using var registry = TimeoutRegistry();
        using var held = HoldStoreLock();
        string outside = Path.Combine(_root, "outside_evidence_target"); Directory.CreateDirectory(outside);
        string link = FailureEvidencePath + AtomicJsonFile.TemporarySuffix;
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        { mklink.WaitForExit(); Assert.Equal(0, mklink.ExitCode); }
        try
        {
            Assert.Throws<TimeoutException>(() => registry.CaptureRlSnapshot());
            Assert.False(File.Exists(FailureEvidencePath));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }
}
