using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services.Drawing;

/// <summary>
/// Regression tests for two bugs reported against the chart-drawing-persistence feature
/// (see Y:\Temp\sa_step_log_ChartDrawingPersistence.md):
/// 1) An empty (never-drawn-on) ticker/timeframe should not create a JSON file (previously
///    happened for the app's default startup symbol on every launch).
/// 2) SaveAsync must fully complete the write before returning, so a Load() immediately
///    afterward (as ChartViewModel does when switching back to a symbol) cannot race an
///    in-flight fire-and-forget write and see a stale/missing file.
/// </summary>
public class ChartDrawingRepositoryTests : IDisposable
{
    // A ticker unlikely to collide with anything real; cleaned up in Dispose().
    private const string TestTicker = "ZZTEST-CHARTDRAWINGREPO";
    private readonly List<string> _createdFiles = new();

    private static string ResolveFilePath(TimeframeType timeframe)
    {
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        return Path.Combine(dir, $"{TestTicker}.{timeframe}.json");
    }

    public void Dispose()
    {
        foreach (var file in _createdFiles)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task SaveAsync_EmptyDataAndNoExistingFile_DoesNotCreateFile()
    {
        var repo = new ChartDrawingRepository();
        var path = ResolveFilePath(TimeframeType.Daily);
        _createdFiles.Add(path);
        Assert.False(File.Exists(path));

        var empty = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>()
        };
        await repo.SaveAsync(TestTicker, TimeframeType.Daily, empty);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveAsync_EmptyDataButFileAlreadyExists_StillOverwritesFile()
    {
        var repo = new ChartDrawingRepository();
        var path = ResolveFilePath(TimeframeType.Weekly);
        _createdFiles.Add(path);

        var withDrawing = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>
            {
                new TrendLineObject(new ChartPoint(DateTime.UtcNow, 1m), new ChartPoint(DateTime.UtcNow.AddDays(1), 2m))
            }
        };
        await repo.SaveAsync(TestTicker, TimeframeType.Weekly, withDrawing);
        Assert.True(File.Exists(path));

        var empty = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>()
        };
        await repo.SaveAsync(TestTicker, TimeframeType.Weekly, empty);

        Assert.True(File.Exists(path)); // Not deleted...
        var restored = repo.Load(TestTicker, TimeframeType.Weekly);
        Assert.NotNull(restored);
        Assert.Empty(restored![ChartDrawingContextType.Standard]); // ...but correctly reflects the clear.
    }

    [Fact]
    public async Task SaveAsync_CompletesWriteBeforeReturning_SoImmediateLoadSeesIt()
    {
        var repo = new ChartDrawingRepository();
        var path = ResolveFilePath(TimeframeType.Monthly);
        _createdFiles.Add(path);

        var data = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>
            {
                new TrendLineObject(new ChartPoint(DateTime.UtcNow, 10m), new ChartPoint(DateTime.UtcNow.AddDays(2), 20m))
            }
        };

        await repo.SaveAsync(TestTicker, TimeframeType.Monthly, data);

        // No delay/retry: SaveAsync's completion must guarantee the file is already on disk.
        var restored = repo.Load(TestTicker, TimeframeType.Monthly);
        Assert.NotNull(restored);
        Assert.Single(restored![ChartDrawingContextType.Standard]);
    }

    /// <summary>
    /// Reproduces (with a single ChartDrawingRepository instance, as it is registered as a DI
    /// singleton shared across every open ChartViewModel/chart tab) the exact failure observed
    /// via diagnostic logging in the real app: concurrent SaveAsync calls targeting the same
    /// ticker+timeframe file threw "System.IO.IOException: The process cannot access the file
    /// '...json.tmp' because it is being used by another process", silently dropping saves
    /// (caught and logged, never surfaced) whenever two chart tabs/instances raced on the same
    /// file. All concurrent saves must now complete without throwing, and Load() afterward must
    /// return a valid (non-corrupted) result.
    /// </summary>
    [Fact]
    public async Task ConcurrentSaveAsyncCallsToSameKey_DoNotThrow_AndLeaveFileReadable()
    {
        var repo = new ChartDrawingRepository();
        var path = ResolveFilePath(TimeframeType.Daily);
        _createdFiles.Add(path);

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            var data = new Dictionary<ChartDrawingContextType, List<IChartObject>>
            {
                [ChartDrawingContextType.Standard] = new List<IChartObject>
                {
                    new TrendLineObject(new ChartPoint(DateTime.UtcNow, i), new ChartPoint(DateTime.UtcNow.AddDays(1), i + 1))
                }
            };
            tasks.Add(repo.SaveAsync(TestTicker, TimeframeType.Daily, data));
        }

        // Must not throw (previously: concurrent File.Replace/File.WriteAllText on the shared
        // .tmp path threw IOException on the losing task(s)).
        await Task.WhenAll(tasks);

        var restored = repo.Load(TestTicker, TimeframeType.Daily);
        Assert.NotNull(restored);
        Assert.Single(restored![ChartDrawingContextType.Standard]); // whichever write landed last, but it must be a complete, valid record
    }

    // --- Task 3: edge-case handling (corruption fallback, filename validation) ---

    [Fact]
    public void Load_CorruptedFile_FallsBackToNullAndBacksUpTheCorruptedFile()
    {
        var repo = new ChartDrawingRepository();
        var path = ResolveFilePath(TimeframeType.Daily);
        var backupPath = path + ".corrupted";
        _createdFiles.Add(path);
        _createdFiles.Add(backupPath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json ]]]");

        var result = repo.Load(TestTicker, TimeframeType.Daily);

        Assert.Null(result); // falls back to "no drawings" rather than throwing/crashing
        Assert.True(File.Exists(backupPath)); // corrupted original preserved for diagnosis
        Assert.Equal("{ this is not valid json ]]]", File.ReadAllText(backupPath));
    }

    [Theory]
    [InlineData("AAA/BBB")]
    [InlineData("AAA*BBB")]
    [InlineData("AAA?BBB")]
    public async Task SaveAsyncAndLoad_TickerNormalizingToInvalidFileName_AreNoOps(string unsafeTicker)
    {
        var repo = new ChartDrawingRepository();

        var data = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>
            {
                new TrendLineObject(new ChartPoint(DateTime.UtcNow, 1m), new ChartPoint(DateTime.UtcNow.AddDays(1), 2m))
            }
        };

        // Must not throw (Path.Combine/File I/O with invalid file name chars would otherwise).
        await repo.SaveAsync(unsafeTicker, TimeframeType.Daily, data);
        var result = repo.Load(unsafeTicker, TimeframeType.Daily);

        Assert.Null(result);
    }

    /// <summary>
    /// SAで制約確認 Phase 2: a ticker normalizing to a Windows-reserved device name (e.g. "CON")
    /// passes the upstream ticker-format regex (^[A-Z0-9\-\.]{1,15}$) but must still be refused
    /// here, since creating e.g. "CON.Daily.json" is unsafe/reserved on Windows.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("con")] // case-insensitive
    [InlineData("COM1")]
    [InlineData("LPT1")]
    public async Task SaveAsyncAndLoad_TickerNormalizingToReservedDeviceName_AreNoOps(string reservedTicker)
    {
        var repo = new ChartDrawingRepository();

        var data = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject>
            {
                new TrendLineObject(new ChartPoint(DateTime.UtcNow, 1m), new ChartPoint(DateTime.UtcNow.AddDays(1), 2m))
            }
        };

        await repo.SaveAsync(reservedTicker, TimeframeType.Daily, data);
        var result = repo.Load(reservedTicker, TimeframeType.Daily);

        Assert.Null(result);
    }
}
