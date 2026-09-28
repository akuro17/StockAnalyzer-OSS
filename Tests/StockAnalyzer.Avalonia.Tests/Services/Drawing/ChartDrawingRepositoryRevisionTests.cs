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
/// Revision guard of ChartDrawingRepository (stale-tab overwrite protection): accepted saves advance the
/// revision, a save based on an outdated revision leaves the file untouched and keeps a .conflict backup,
/// and Load waits for accepted-but-unwritten saves.
/// </summary>
public class ChartDrawingRepositoryRevisionTests : IDisposable
{
    private const string TestTicker = "ZZTEST-CHARTREVISION";
    private static readonly TimeframeType Timeframe = TimeframeType.Daily;

    private static string FilePath()
    {
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        return Path.Combine(dir, $"{TestTicker}.{Timeframe}.json");
    }

    public void Dispose()
    {
        foreach (var path in new[] { FilePath(), FilePath() + ChartDrawingRepository.ConflictFileSuffix, FilePath() + ".tmp", FilePath() + ".conflict.tmp" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ChartDrawingPayload PayloadWithLines(int count)
    {
        var lines = new List<IChartObject>();
        for (int i = 0; i < count; i++)
        {
            lines.Add(new TrendLineObject(
                new ChartPoint(new DateTime(2024, 1, 1), 10m + i),
                new ChartPoint(new DateTime(2024, 1, 2), 12m + i)));
        }
        return new ChartDrawingPayload
        {
            Objects = new Dictionary<ChartDrawingContextType, List<IChartObject>> { [ChartDrawingContextType.Standard] = lines }
        };
    }

    private static int PersistedCount(ChartDrawingRepository repo) =>
        repo.LoadPayload(TestTicker, Timeframe)?.Objects.GetValueOrDefault(ChartDrawingContextType.Standard)?.Count ?? 0;

    [Fact]
    public async Task SavePayloadIfCurrent_MatchingRevision_IsAcceptedAndAdvancesRevision()
    {
        var repo = new ChartDrawingRepository();
        var baseRevision = repo.GetRevision(TestTicker, Timeframe);

        var outcome = repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(1), baseRevision);
        await outcome.Completion;

        Assert.True(outcome.IsAccepted);
        Assert.Equal(baseRevision + 1, outcome.Revision);
        Assert.Equal(outcome.Revision, repo.GetRevision(TestTicker, Timeframe));
        Assert.Equal(1, PersistedCount(repo));
    }

    [Fact]
    public async Task SavePayloadIfCurrent_StaleRevision_KeepsFileAndPreservesRejectedPayloadAsConflict()
    {
        var repo = new ChartDrawingRepository();
        var baseRevision = repo.GetRevision(TestTicker, Timeframe);

        await repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(1), baseRevision).Completion; // tab A
        var stale = repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(2), baseRevision);      // tab B, same base
        await stale.Completion;

        Assert.False(stale.IsAccepted);
        Assert.Equal(baseRevision + 1, stale.Revision);
        Assert.Equal(baseRevision + 1, repo.GetRevision(TestTicker, Timeframe));
        Assert.Equal(1, PersistedCount(repo));
        Assert.True(File.Exists(FilePath() + ChartDrawingRepository.ConflictFileSuffix));
    }

    [Fact]
    public void LoadPayload_ReportsRevisionAndWaitsForAcceptedWrites()
    {
        var repo = new ChartDrawingRepository();
        var baseRevision = repo.GetRevision(TestTicker, Timeframe);

        var outcome = repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(3), baseRevision);
        // No await: the accepted write may still be pending; Load must not miss it.
        var payload = repo.LoadPayload(TestTicker, Timeframe, out var revision);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(outcome.Revision, revision);
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Objects[ChartDrawingContextType.Standard].Count);
    }

    [Fact]
    public async Task UnconditionalSave_AdvancesRevision_SoGuardedTabsSeeThemselvesStale()
    {
        var repo = new ChartDrawingRepository();
        var baseRevision = repo.GetRevision(TestTicker, Timeframe);

        await repo.SavePayloadAsync(TestTicker, Timeframe, PayloadWithLines(1));

        Assert.NotEqual(baseRevision, repo.GetRevision(TestTicker, Timeframe));
        Assert.False(repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(2), baseRevision).IsAccepted);
    }

    [Fact]
    public async Task QueuedSaves_ReachDiskInAcceptanceOrder()
    {
        var repo = new ChartDrawingRepository();
        var revision = repo.GetRevision(TestTicker, Timeframe);

        Task last = Task.CompletedTask;
        for (int count = 1; count <= 5; count++)
        {
            var outcome = repo.SavePayloadIfCurrent(TestTicker, Timeframe, PayloadWithLines(count), revision);
            Assert.True(outcome.IsAccepted);
            revision = outcome.Revision;
            last = outcome.Completion;
        }
        await last;

        Assert.Equal(5, PersistedCount(repo));
    }
}
