#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// A reader opened by <see cref="AtomicJsonFile.LoadAsync{T}"/> must not make a concurrent atomic save fail
/// (File.Replace needs delete sharing on Windows). Result and exception types of the load are unchanged.
/// </summary>
public sealed class AtomicJsonFileLoadShareTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_atomic_json_load_" + Guid.NewGuid().ToString("N"));
    private sealed record Sample(string Name, int Value);
    private string Target => Path.Combine(_directory, "sample.json");
    public AtomicJsonFileLoadShareTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } }

    [Fact]
    public void SaveSucceedsWhileAReaderOfLoadAsyncIsStillOpen_AndTheReaderKeepsTheOldContent()
    {
        AtomicJsonFile.Save(Target, new Sample("old", 1));

        using (var reader = AtomicJsonFile.OpenForRead(Target))
        {
            AtomicJsonFile.Save(Target, new Sample("new", 2));

            using var text = new StreamReader(reader);
            Assert.Equal(new Sample("old", 1), JsonSerializer.Deserialize<Sample>(text.ReadToEnd()));
        }

        Assert.Equal(new Sample("new", 2), JsonSerializer.Deserialize<Sample>(File.ReadAllText(Target)));
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheStoredValue()
    {
        AtomicJsonFile.Save(Target, new Sample("a", 7));
        Assert.Equal(new Sample("a", 7), await AtomicJsonFile.LoadAsync<Sample>(Target));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => AtomicJsonFile.LoadAsync<Sample>(Path.Combine(_directory, "missing.json")));
    }

    [Fact]
    public async Task LoadAsync_EmptyFile_ThrowsJsonException()
    {
        File.WriteAllText(Target, string.Empty);
        await Assert.ThrowsAsync<JsonException>(() => AtomicJsonFile.LoadAsync<Sample>(Target));
    }
}
