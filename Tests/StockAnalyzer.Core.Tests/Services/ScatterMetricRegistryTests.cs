#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class ScatterMetricRegistryTests : IDisposable
{
    private readonly string _path = ScatterMetricTestKit.NewTempFilePath();
    private readonly ScatterMetricTestSettings _settings = new();

    public void Dispose()
    {
        foreach (string file in new[] { _path, _path + ".tmp", _path + ".bak" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    private ScatterMetricRegistry NewRegistry(string? path = null) => new(_settings, null, path ?? _path);

    private static ScatterMetricDefinition Rsi(int period = 14, int offset = 0)
        => ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset, null, ("Period", period));

    [Fact]
    public async Task ANewRegistryWithoutAFile_LoadsEmpty()
    {
        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Empty(registry.Snapshot());
        Assert.Equal(0, registry.TakeLoadDroppedCount());
    }

    [Fact]
    public async Task ReplaceAll_StoresTheListAndANewInstanceReadsItBack_InOrder()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var list = new List<ScatterMetricDefinition> { Rsi(14), Rsi(21, 2), ScatterMetricTestKit.Indicator(IndicatorType.SMA, 0, null, ("Period", 20)) };

        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(list));

        var reloaded = NewRegistry();
        await reloaded.Loaded;
        Assert.Equal(list.Select(m => m.Id), reloaded.Snapshot().Select(m => m.Id));
        Assert.Equal(list.Select(m => m.ContentKey), reloaded.Snapshot().Select(m => m.ContentKey));
        Assert.Equal(list.Select(m => m.Label), reloaded.Snapshot().Select(m => m.Label));
    }

    [Fact]
    public async Task Snapshot_ReturnsIndependentCopies()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { Rsi() });

        registry.Snapshot()[0].Source.Offset = 99;

        Assert.Equal(0, registry.Snapshot()[0].Source.Offset);
    }

    [Fact]
    public async Task ReplaceAll_StoresACopy_SoLaterEditsOfTheCallersListDoNotLeakIn()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var metric = Rsi();
        await registry.ReplaceAllAsync(new[] { metric });

        metric.Source.Offset = 42;

        Assert.Equal(0, registry.Snapshot()[0].Source.Offset);
    }

    [Fact]
    public async Task ReplaceAll_MoreThanTheLimit_IsLimitReachedAndChangesNothing()
    {
        _settings.ScatterMaxRegisteredMetrics = 3;
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { Rsi(5) });
        int events = 0;
        registry.Changed += (_, _) => events++;

        var result = await registry.ReplaceAllAsync(new[] { Rsi(1), Rsi(2), Rsi(3), Rsi(4) });

        Assert.Equal(ScatterMetricRegistryResult.LimitReached, result);
        Assert.Single(registry.Snapshot());
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task ReplaceAll_ExactlyTheLimit_IsAccepted()
    {
        _settings.ScatterMaxRegisteredMetrics = 3;
        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(new[] { Rsi(1), Rsi(2), Rsi(3) }));
        Assert.Equal(3, registry.Snapshot().Count);
    }

    [Fact]
    public async Task ReplaceAll_LimitIsCheckedBeforeValidity()
    {
        _settings.ScatterMaxRegisteredMetrics = 1;
        var registry = NewRegistry();
        await registry.Loaded;
        var invalid = Rsi();
        invalid.Source.Offset = -1;

        Assert.Equal(ScatterMetricRegistryResult.LimitReached, await registry.ReplaceAllAsync(new[] { invalid, Rsi(2) }));
    }

    [Fact]
    public async Task ReplaceAll_AnInvalidEntryOrARepeatedId_IsInvalid_AndChangesNothing()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { Rsi(5) });

        var criteria = Rsi();
        criteria.Source.CategoryType = ScreenerItemCategoryType.Criteria;
        var tooFar = Rsi(offset: _settings.ScatterMaxMetricOffset + 1);
        var first = Rsi(10);
        var sameId = Rsi(11);
        sameId.Id = first.Id;

        Assert.Equal(ScatterMetricRegistryResult.Invalid, await registry.ReplaceAllAsync(new[] { criteria }));
        Assert.Equal(ScatterMetricRegistryResult.Invalid, await registry.ReplaceAllAsync(new[] { tooFar }));
        Assert.Equal(ScatterMetricRegistryResult.Invalid, await registry.ReplaceAllAsync(new[] { first, sameId }));
        Assert.Equal("RSI", registry.Snapshot().Single().Source.IndicatorType.ToString());
        Assert.Equal(5, Convert.ToInt32(registry.Snapshot().Single().Source.Parameters["Period"]));
    }

    [Fact]
    public async Task ReplaceAll_TwoEntriesWithTheSameContentButOtherIds_AreAccepted()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var a = Rsi(14);
        var b = Rsi(14);

        Assert.Equal(a.ContentKey, b.ContentKey);
        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(new[] { a, b }));
        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public async Task ReplaceAll_FailedWrite_IsPersistFailed_KeepsMemory_AndRaisesNoEvent()
    {
        string blocker = ScatterMetricTestKit.NewTempFilePath();
        await File.WriteAllTextAsync(blocker, "a file where a folder would have to be");
        try
        {
            // The parent "folder" of the target is a file, so creating the directory (and so the write) fails.
            var registry = NewRegistry(Path.Combine(blocker, "scatter_metrics.json"));
            await registry.Loaded;
            int events = 0;
            registry.Changed += (_, _) => events++;

            var result = await registry.ReplaceAllAsync(new[] { Rsi() });

            Assert.Equal(ScatterMetricRegistryResult.PersistFailed, result);
            Assert.Empty(registry.Snapshot());
            Assert.Equal(0, events);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Changed_FiresOncePerRealChange_AndNotForAnIdenticalListOrAFailedOne()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        int events = 0;
        registry.Changed += (_, _) => events++;
        var a = Rsi(14);
        var b = Rsi(21);

        await registry.ReplaceAllAsync(new[] { a, b });
        Assert.Equal(1, events);

        await registry.ReplaceAllAsync(new[] { a.Clone(), b.Clone() });
        Assert.Equal(1, events);

        await registry.ReplaceAllAsync(new[] { b, a });
        Assert.Equal(2, events);

        var edited = b.Clone();
        edited.Source.Offset = 3;
        await registry.ReplaceAllAsync(new[] { edited, a });
        Assert.Equal(3, events);

        await registry.ReplaceAllAsync(Array.Empty<ScatterMetricDefinition>());
        Assert.Equal(4, events);
    }

    [Fact]
    public async Task AHandlerThatThrows_DoesNotStopTheOthers_NorFailTheChange()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        int reached = 0;
        registry.Changed += (_, _) => throw new InvalidOperationException("handler failure");
        registry.Changed += (_, _) => reached++;

        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(new[] { Rsi() }));
        Assert.Equal(1, reached);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public async Task ACorruptFile_StartsEmpty_AndIsLeftUntouched()
    {
        const string garbage = "{ this is not json";
        await File.WriteAllTextAsync(_path, garbage);

        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Empty(registry.Snapshot());
        Assert.Equal(garbage, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task AFileOfANewerFormat_IsIgnored_AndLeftUntouched()
    {
        string json = "{\"FormatVersion\": 2, \"Metrics\": []}";
        await File.WriteAllTextAsync(_path, json);

        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Empty(registry.Snapshot());
        Assert.Equal(json, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task InvalidSavedEntries_AreDropped_AndCountedOnce()
    {
        var seed = NewRegistry();
        await seed.Loaded;
        var good = Rsi(14);
        await seed.ReplaceAllAsync(new[] { good });

        // Add an invalid entry (Criteria) and a repeated Id by editing the file the way an old build or a hand edit could.
        string json = await File.ReadAllTextAsync(_path);
        var file = System.Text.Json.JsonSerializer.Deserialize<ScatterMetricFile>(json)!;
        var criteria = Rsi(30);
        criteria.Source.CategoryType = ScreenerItemCategoryType.Criteria;
        var repeated = Rsi(40);
        repeated.Id = good.Id;
        file.Metrics.Add(criteria);
        file.Metrics.Add(repeated);
        await File.WriteAllTextAsync(_path, System.Text.Json.JsonSerializer.Serialize(file));

        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Equal(good.Id, Assert.Single(registry.Snapshot()).Id);
        Assert.Equal(2, registry.TakeLoadDroppedCount());
        Assert.Equal(0, registry.TakeLoadDroppedCount());
    }

    [Fact]
    public async Task MoreSavedEntriesThanTheLimit_KeepTheFirstOnes_AndCountTheRest()
    {
        var seed = NewRegistry();
        await seed.Loaded;
        var five = Enumerable.Range(1, 5).Select(i => Rsi(i + 1)).ToList();
        await seed.ReplaceAllAsync(five);

        _settings.ScatterMaxRegisteredMetrics = 3;
        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Equal(five.Take(3).Select(m => m.Id), registry.Snapshot().Select(m => m.Id));
        Assert.Equal(2, registry.TakeLoadDroppedCount());
    }

    [Fact]
    public async Task ReplaceAllCalledRightAfterConstruction_IsNotOverwrittenByTheFirstRead()
    {
        var seed = NewRegistry();
        await seed.Loaded;
        await seed.ReplaceAllAsync(new[] { Rsi(5), Rsi(6) });

        // The first read of a new instance runs on another thread; a change started at once must wait for it and win.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            var registry = NewRegistry();
            var mine = Rsi(100 + attempt);

            Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(new[] { mine }));
            await registry.Loaded;

            Assert.Equal(mine.Id, Assert.Single(registry.Snapshot()).Id);
            await seed.ReplaceAllAsync(new[] { Rsi(5), Rsi(6) });
        }
    }

    [Fact]
    public async Task ConcurrentReplaceAll_EndsInOneOfTheInputs_ThatIsAlsoWhatIsOnDisk()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var inputs = Enumerable.Range(1, 40).Select(i => Rsi(i + 1)).ToList();

        var results = await Task.WhenAll(inputs.Select(m => Task.Run(() => registry.ReplaceAllAsync(new[] { m }))));

        Assert.All(results, r => Assert.Equal(ScatterMetricRegistryResult.Ok, r));
        Guid last = Assert.Single(registry.Snapshot()).Id;
        Assert.Contains(inputs, m => m.Id == last);

        var reloaded = NewRegistry();
        await reloaded.Loaded;
        Assert.Equal(last, Assert.Single(reloaded.Snapshot()).Id);
    }

    [Fact]
    public async Task AFileWrittenByThisBuild_UsesTheCurrentFormatVersionAndIndentedJson()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { Rsi() });

        string json = await File.ReadAllTextAsync(_path, Encoding.UTF8);

        Assert.Contains($"\"FormatVersion\": {ScatterMetricFile.CurrentFormatVersion}", json);
        Assert.DoesNotContain("\"Label\"", json);
        Assert.DoesNotContain("\"ContentKey\"", json);
    }

    [Fact]
    public async Task DayWeekAndMonthMetrics_AreStoredAndReadBackInOrder_WithTheirTimeFrames()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var list = new List<ScatterMetricDefinition>
        {
            Rsi(14),
            ScatterMetricTestKit.OnFrame(Rsi(14, 1), TimeFrame.W1),
            ScatterMetricTestKit.OnFrame(ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"), 2), TimeFrame.MN1),
        };

        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(list));

        var reloaded = NewRegistry();
        await reloaded.Loaded;
        Assert.Equal(list.Select(m => m.Id), reloaded.Snapshot().Select(m => m.Id));
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }, reloaded.Snapshot().Select(m => m.Source.TimeFrame).ToArray());
        Assert.Equal(list.Select(m => m.ContentKey), reloaded.Snapshot().Select(m => m.ContentKey));
        Assert.Equal(list.Select(m => m.Label), reloaded.Snapshot().Select(m => m.Label));
    }

    [Fact]
    public async Task ASavedEntryOnAnUnsupportedTimeFrame_IsDroppedAlone_AndDayAndWeekStay()
    {
        var seed = NewRegistry();
        await seed.Loaded;
        var day = Rsi(14);
        var week = ScatterMetricTestKit.OnFrame(Rsi(21), TimeFrame.W1);
        await seed.ReplaceAllAsync(new[] { day, week });

        // An entry on an hourly time frame, as a hand edit or a later build could leave in the file.
        string json = await File.ReadAllTextAsync(_path);
        var file = System.Text.Json.JsonSerializer.Deserialize<ScatterMetricFile>(json)!;
        file.Metrics.Add(ScatterMetricTestKit.OnFrame(Rsi(30), TimeFrame.H1));
        await File.WriteAllTextAsync(_path, System.Text.Json.JsonSerializer.Serialize(file));

        var registry = NewRegistry();
        await registry.Loaded;

        Assert.Equal(new[] { day.Id, week.Id }, registry.Snapshot().Select(m => m.Id).ToArray());
        Assert.Equal(TimeFrame.W1, registry.Snapshot()[1].Source.TimeFrame);
        Assert.Equal(1, registry.TakeLoadDroppedCount());
    }

    [Fact]
    public async Task ReplaceAll_RefusesAnUnsupportedTimeFrame_AndAFundamentalColumnOnWeek()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        var fundamental = ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("MarketCap"));

        Assert.Equal(ScatterMetricRegistryResult.Invalid, await registry.ReplaceAllAsync(new[] { ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.H1) }));
        Assert.Equal(ScatterMetricRegistryResult.Ok, await registry.ReplaceAllAsync(new[] { fundamental }));
        Assert.Equal(ScatterMetricRegistryResult.Invalid, await registry.ReplaceAllAsync(new[] { ScatterMetricTestKit.OnFrame(fundamental.Clone(), TimeFrame.W1) }));
        Assert.Equal(TimeFrame.D1, registry.Snapshot().Single().Source.TimeFrame);
    }

    [Fact]
    public async Task TheSecondSave_LeavesTheContentOfTheFirstAsTheBackup()
    {
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { Rsi(14), ScatterMetricTestKit.OnFrame(Rsi(21), TimeFrame.W1) });
        string firstSave = await File.ReadAllTextAsync(_path);

        await registry.ReplaceAllAsync(new[] { ScatterMetricTestKit.OnFrame(Rsi(28), TimeFrame.MN1) });

        Assert.Equal(firstSave, await File.ReadAllTextAsync(_path + ".bak"));
        Assert.NotEqual(firstSave, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task ARegisteredMetric_GivesTheSameValueBeforeAndAfterTheSaveAndReload()
    {
        // The reloaded parameters come back as JSON values; the Screener extractor must read them like the typed ones.
        var original = Rsi(period: 7);
        var registry = NewRegistry();
        await registry.Loaded;
        await registry.ReplaceAllAsync(new[] { original });
        var reloaded = NewRegistry();
        await reloaded.Loaded;
        var bars = ScatterMetricTestKit.Bars(300);

        decimal? before = ScreenerValueExtractor.Default.ExtractValueNullable(original.Source, bars);
        decimal? after = ScreenerValueExtractor.Default.ExtractValueNullable(reloaded.Snapshot().Single().Source, bars);
        decimal? defaultPeriod = ScreenerValueExtractor.Default.ExtractValueNullable(Rsi(14).Source, bars);

        Assert.NotNull(before);
        Assert.Equal(before, after);
        Assert.NotEqual(defaultPeriod, after);
    }
}
