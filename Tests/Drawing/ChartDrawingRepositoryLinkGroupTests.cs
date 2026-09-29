using System;
using System.IO;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// End-to-end persistence of link groups through <see cref="ChartDrawingRepository"/>, the store actually used by the app
/// (legacy payload, positional records) and the V2 document format it can also read.
/// </summary>
public class ChartDrawingRepositoryLinkGroupTests
{
    private const string TestTicker = "ZZLINKGROUPTEST";
    private const TimeframeType Timeframe = TimeframeType.Daily;

    private static TrendLineObject NewLine(int day, decimal price)
        => new(new ChartPoint(new DateTime(2025, 1, day), price), new ChartPoint(new DateTime(2025, 1, day + 3), price + 10m));

    private static ChartObjectManager LinkedManager()
    {
        var manager = new ChartObjectManager();
        var lines = new[] { NewLine(2, 100m), NewLine(4, 120m), NewLine(6, 140m) };
        foreach (var line in lines) manager.AddObject(line);
        manager.LinkObjects(lines[2].Id, new[] { lines[0].Id, lines[1].Id });
        return manager;
    }

    private static string DrawingFilePath(ChartDrawingRepository repository)
    {
        Assert.True(repository.TryResolveDocumentPath(TestTicker, Timeframe, out var path));
        return path;
    }

    private static void AssertRestored(ChartDrawingPayload? loaded)
    {
        Assert.NotNull(loaded);
        Assert.NotEmpty(loaded!.LinkGroups);

        var restored = new ChartObjectManager();
        restored.LoadSnapshot(loaded.Objects);
        foreach (var (context, records) in loaded.LinkGroups) restored.LoadLinkGroupIndexRecords(context, records);

        var objects = restored.Objects;
        Assert.Equal(3, objects.Count);
        Assert.Equal(LinkRole.Child, restored.GetLinkRole(objects[0].Id));
        Assert.Equal(LinkRole.Child, restored.GetLinkRole(objects[1].Id));
        Assert.Equal(LinkRole.Parent, restored.GetLinkRole(objects[2].Id));
    }

    [Fact]
    public void LegacyPayload_SaveThenLoad_RestoresLinkGroupsOntoFreshObjects()
    {
        var repository = new ChartDrawingRepository();
        var path = DrawingFilePath(repository);
        try
        {
            var manager = LinkedManager();
            var payload = new ChartDrawingPayload
            {
                Objects = manager.GetSnapshot(),
                LinkGroups = manager.GetLinkGroupIndexRecords()
            };

            repository.SavePayloadAsync(TestTicker, Timeframe, payload).GetAwaiter().GetResult();

            AssertRestored(repository.LoadPayload(TestTicker, Timeframe));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void V2Document_LoadedThroughTheRepository_KeepsLinkGroups()
    {
        var repository = new ChartDrawingRepository();
        var path = DrawingFilePath(repository);
        try
        {
            var manager = LinkedManager();
            var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey(TestTicker, Timeframe), revision: 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DrawingDocumentCodec.SerializeToJson(state));

            AssertRestored(repository.LoadPayload(TestTicker, Timeframe));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
