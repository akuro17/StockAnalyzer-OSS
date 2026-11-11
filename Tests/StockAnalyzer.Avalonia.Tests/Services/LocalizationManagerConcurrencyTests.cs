using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

// Reloads the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class LocalizationManagerConcurrencyTests
{
    // A key every shipped locale defines; background code (ChartViewModel prediction text) reads it while a language reload may run.
    private const string ExistingKey = "Chart_PredictedClassWithProbability";
    private const int ReloadCount = 300;

    [Fact]
    public async Task Reload_NeverShowsAnEmptyOrPartialStringTableToAConcurrentReader()
    {
        var manager = LocalizationManager.Instance;
        var language = manager.CurrentLanguage;
        manager.Initialize("en");
        int misses = 0, faults = 0, reads = 0;
        using var writerDone = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!writerDone.IsCancellationRequested)
            {
                try
                {
                    if (!manager.TryGet(ExistingKey, out var template) || string.IsNullOrWhiteSpace(template)) Interlocked.Increment(ref misses);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref faults);
                }
                Interlocked.Increment(ref reads);
            }
        });

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < ReloadCount; i++) manager.Initialize("en");
            });
        }
        finally
        {
            writerDone.Cancel();
            await reader;
            manager.Initialize(language);
        }

        Assert.True(reads > 0, "the reader never ran, so the test proved nothing");
        Assert.Equal((0, 0), (misses, faults));
    }
}
