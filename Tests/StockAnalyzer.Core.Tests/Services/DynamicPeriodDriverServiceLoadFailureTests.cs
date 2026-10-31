using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A registered-drivers file that cannot be read must never be replaced by an (almost) empty one, concurrent readers and
/// writers share one lock, and the file is published through the shared atomic writer (IoHydrationWebAiReview, T1).
/// </summary>
public class DynamicPeriodDriverServiceLoadFailureTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public DynamicPeriodDriverServiceLoadFailureTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "StockAnalyzer_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "dynamic_period_drivers.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private static CoreIndicatorSettings Driver(string id) => new() { Id = id, DisplayName = id, TypeEnum = IndicatorType.RSI };

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    public async Task CorruptFile_IsQuarantinedBeforeTheFirstSaveOverwritesIt(string content)
    {
        File.WriteAllText(_filePath, content);
        byte[] original = File.ReadAllBytes(_filePath);

        using var service = new DynamicPeriodDriverService(_filePath);
        Assert.Empty(service.GetDynamicPeriodDrivers());
        await service.SaveDynamicPeriodDriverAsync(Driver("new"));

        Assert.Equal(original, File.ReadAllBytes(_filePath + ".corrupted"));
        using var reloaded = new DynamicPeriodDriverService(_filePath);
        Assert.Equal(new[] { "new" }, reloaded.GetDynamicPeriodDrivers().Select(d => d.Id));
    }

    [Fact]
    public async Task UnreadableFile_SaveFailsWithIoExceptionAndKeepsTheStoredData()
    {
        if (!OperatingSystem.IsWindows()) return; // exclusive-open semantics are the portable way to make a read fail

        using (var seed = new DynamicPeriodDriverService(_filePath))
        {
            await seed.SaveDynamicPeriodDriverAsync(Driver("a"));
            await seed.SaveDynamicPeriodDriverAsync(Driver("b"));
        }
        byte[] stored = File.ReadAllBytes(_filePath);

        using (var held = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // An instance created while the file is exclusively held cannot read it.
            using var blocked = new DynamicPeriodDriverService(_filePath);
            await Assert.ThrowsAsync<IOException>(() => blocked.SaveDynamicPeriodDriverAsync(Driver("c")));
            await Assert.ThrowsAsync<IOException>(() => blocked.DeleteDynamicPeriodDriverAsync("a"));
            Assert.Empty(await blocked.GetDynamicPeriodDriversAsync());

            // Once the lock is gone the same instance loads the stored data and saves on top of it.
            held.Dispose();
            await blocked.SaveDynamicPeriodDriverAsync(Driver("c"));
            Assert.Equal(new[] { "a", "b", "c" }, blocked.GetDynamicPeriodDrivers().Select(d => d.Id));
        }

        Assert.NotEqual(stored, File.ReadAllBytes(_filePath));
        using var reloaded = new DynamicPeriodDriverService(_filePath);
        Assert.Equal(new[] { "a", "b", "c" }, reloaded.GetDynamicPeriodDrivers().Select(d => d.Id));
        Assert.False(File.Exists(_filePath + ".corrupted"));
    }

    [Fact]
    public async Task Save_PublishesThroughTheAtomicWriter_NoBomAndNoTemporaryFile()
    {
        using var service = new DynamicPeriodDriverService(_filePath);
        await service.SaveDynamicPeriodDriverAsync(Driver("a"));
        await service.SaveDynamicPeriodDriverAsync(Driver("b"));

        byte[] bytes = File.ReadAllBytes(_filePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(new[] { Path.GetFileName(_filePath) },
            Directory.GetFiles(_directory).Select(Path.GetFileName).Where(n => !n!.EndsWith(".bak", StringComparison.Ordinal)));
        Assert.Contains("\"a\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SynchronousReadersAndAsynchronousWriters_DoNotRace()
    {
        using var service = new DynamicPeriodDriverService(_filePath);
        for (int i = 0; i < 20; i++) await service.SaveDynamicPeriodDriverAsync(Driver("seed" + i));

        using var stop = new System.Threading.CancellationTokenSource();
        Exception? readerFailure = null;
        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    service.GetDynamicPeriodDrivers();
                    service.GetDynamicPeriodDriver("seed3");
                }
            }
            catch (Exception ex) { readerFailure = ex; }
        });

        for (int i = 0; i < 60; i++)
        {
            await service.SaveDynamicPeriodDriverAsync(Driver("w" + i));
            await service.DeleteDynamicPeriodDriverAsync("w" + i);
        }
        stop.Cancel();
        await reader;

        Assert.Null(readerFailure);
    }
}
