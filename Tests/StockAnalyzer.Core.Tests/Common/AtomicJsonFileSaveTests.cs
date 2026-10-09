#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>The synchronous Save must behave exactly like SaveAsync; SaveAsync itself is untouched.</summary>
public sealed class AtomicJsonFileSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_atomic_json_save_" + Guid.NewGuid().ToString("N"));
    private sealed record Sample(string Name, int Value);
    private string File1 => Path.Combine(_directory, "sample.json");
    public AtomicJsonFileSaveTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } }

    [Fact]
    public void FirstSaveCreatesTheFileWithoutTemporaryOrBackupFiles()
    {
        AtomicJsonFile.Save(File1, new Sample("a", 1));
        Assert.Equal(new Sample("a", 1), JsonSerializer.Deserialize<Sample>(File.ReadAllText(File1)));
        Assert.False(File.Exists(File1 + AtomicJsonFile.TemporarySuffix));
        Assert.False(File.Exists(File1 + AtomicJsonFile.BackupSuffix));
    }

    [Fact]
    public void ReplacementKeepsThePreviousRecordAsBackupUnlessPreserveBackupIsRequested()
    {
        AtomicJsonFile.Save(File1, new Sample("old", 1));
        AtomicJsonFile.Save(File1, new Sample("new", 2));
        Assert.Equal(new Sample("new", 2), JsonSerializer.Deserialize<Sample>(File.ReadAllText(File1)));
        Assert.Equal(new Sample("old", 1), JsonSerializer.Deserialize<Sample>(File.ReadAllText(File1 + AtomicJsonFile.BackupSuffix)));
        File.Delete(File1 + AtomicJsonFile.BackupSuffix);
        AtomicJsonFile.Save(File1, new Sample("newer", 3), preserveBackup: true);
        Assert.False(File.Exists(File1 + AtomicJsonFile.BackupSuffix));
    }

    [Fact]
    public void FailedSaveThrowsAndLeavesThePreviousRecordIntact()
    {
        AtomicJsonFile.Save(File1, new Sample("kept", 7));
        Directory.CreateDirectory(File1 + AtomicJsonFile.TemporarySuffix);
        Assert.ThrowsAny<Exception>(() => AtomicJsonFile.Save(File1, new Sample("lost", 8)));
        Assert.Equal(new Sample("kept", 7), JsonSerializer.Deserialize<Sample>(File.ReadAllText(File1)));
    }

    [Fact]
    public async Task SaveProducesTheSameBytesAsSaveAsync()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string sync = Path.Combine(_directory, "sync.json"), async = Path.Combine(_directory, "async.json");
        AtomicJsonFile.Save(sync, new Sample("same", 5), options);
        await AtomicJsonFile.SaveAsync(async, new Sample("same", 5), options);
        Assert.Equal(File.ReadAllBytes(async), File.ReadAllBytes(sync));
    }
}
