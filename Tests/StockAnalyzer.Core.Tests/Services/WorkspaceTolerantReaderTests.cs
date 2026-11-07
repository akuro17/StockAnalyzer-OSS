using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Stage 1 of the two-stage workspace restore (D-3', Run A): the tolerant reader.
/// Normalisation (Run B) is intentionally not asserted here.
/// </summary>
public sealed class WorkspaceTolerantReaderTests : IDisposable
{
    private const string BrokenEnumText = "NoSuchIndicatorType";

    private readonly string _directory;
    private readonly WorkspaceTolerantReader _reader = new();
    private readonly WorkspaceSerializationService _service = new();

    public WorkspaceTolerantReaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sa_tolerant_reader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private string NewPath() => Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");

    private static CoreIndicatorSettings Indicator(string id, int period = 14) => new()
    {
        Id = id,
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new CoreSmaParameter { Period = period }
    };

    /// <summary>Saves a valid workspace with the real serializer, then lets the test damage the JSON tree.</summary>
    private async Task<string> WriteDamagedAsync(WorkspaceSettings settings, Action<JsonObject> damage)
    {
        var path = NewPath();
        await _service.SaveAsync(settings, path);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        damage(root);
        await File.WriteAllTextAsync(path, root.ToJsonString());
        return path;
    }

    private string WriteText(string text)
    {
        var path = NewPath();
        File.WriteAllText(path, text);
        return path;
    }

    private static void AssertStatus(WorkspaceLoadResult result, WorkspaceReadStatus status, bool lockExpected, int lossy, int cosmetic = 0)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(lockExpected, result.RequiresAutoSaveLock);
        Assert.Equal(lossy, result.Warnings.Count(w => w.Severity == WorkspaceWarningSeverity.Lossy));
        Assert.Equal(cosmetic, result.Warnings.Count(w => w.Severity == WorkspaceWarningSeverity.Cosmetic));
    }

    private static WorkspaceSettings WithIndicators(params CoreIndicatorSettings[] indicators)
    {
        var settings = new WorkspaceSettings { Name = "Reader Test", SelectedTicker = "AAPL" };
        settings.Indicators.AddRange(indicators);
        return settings;
    }

    // ---- probe first: the defect this component removes (F1) ---------------------------------------------

    [Fact]
    public async Task Probe_CurrentLoadAsync_ThrowsOnBrokenFile_WhichTheCoordinatorTurnsIntoDefaultsAndOverwrite()
    {
        var path = WriteText("{ \"Name\": \"x\", ");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.LoadAsync(path));

        var result = await _reader.ReadAsync(path);
        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Probe_CurrentLoadAsync_LosesWholeWorkspaceForOneBrokenIndicator_ReaderKeepsTheRest()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b"), Indicator("c")),
            root => root["Indicators"]![1]!["TypeEnum"] = BrokenEnumText);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.LoadAsync(path));

        var result = await _reader.ReadAsync(path);
        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal(new[] { "a", "c" }, result.Settings!.Indicators.Select(i => i.Id));
        Assert.Equal("Reader Test", result.Settings.Name);
        Assert.Equal("AAPL", result.Settings.SelectedTicker);
    }

    // ---- file level -------------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_File_ReportsMissingWithoutLock()
    {
        var result = await _reader.ReadAsync(NewPath());

        AssertStatus(result, WorkspaceReadStatus.Missing, lockExpected: false, lossy: 0);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Missing_ParentDirectoryAbsent_ReportsMissing()
    {
        var result = await _reader.ReadAsync(Path.Combine(_directory, "no_such_dir", "workspace.json"));

        AssertStatus(result, WorkspaceReadStatus.Missing, lockExpected: false, lossy: 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\r\n ")]
    public async Task Empty_ZeroBytesOrWhitespace_ReportsEmptyWithoutLock(string content)
    {
        var result = await _reader.ReadAsync(WriteText(content));

        AssertStatus(result, WorkspaceReadStatus.Empty, lockExpected: false, lossy: 0);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Empty_BomFollowedByWhitespaceOnly_ReportsEmpty()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("  \n")).ToArray());

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Empty, lockExpected: false, lossy: 0);
    }

    [Fact]
    public async Task Ok_FileWithUtf8Bom_IsReadLikeAnyOtherFile()
    {
        var path = NewPath();
        await _service.SaveAsync(WithIndicators(Indicator("a")), path);
        var json = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble().Concat(json).ToArray());

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.Equal("a", Assert.Single(result.Settings!.Indicators).Id);
    }

    [Theory]
    [InlineData("{ \"Name\": ")]
    [InlineData("not json at all")]
    [InlineData("{ \"Name\": \"x\" } trailing")]
    public async Task Unreadable_SyntaxError_LocksAndReturnsNoSettings(string content)
    {
        var result = await _reader.ReadAsync(WriteText(content));

        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Unreadable_TruncatedValidFile_IsNotPartiallyRescued()
    {
        var path = NewPath();
        await _service.SaveAsync(WithIndicators(Indicator("a"), Indicator("b")), path);
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text[..(text.Length / 2)]);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Unreadable_RootIsNotAnObject(string content)
    {
        var result = await _reader.ReadAsync(WriteText(content));

        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Unreadable_FileOpenExclusivelyByAnotherWriter_ReportsIoFailureWithoutThrowing()
    {
        var path = NewPath();
        await _service.SaveAsync(WithIndicators(Indicator("a")), path);

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Ok_FileHeldOpenByWriterWithShareReadWrite_StillReadable()
    {
        var path = NewPath();
        await _service.SaveAsync(WithIndicators(Indicator("a")), path);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
    }

    // ---- schema version ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Unreadable_SchemaVersionBelowOne(int version)
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root["SchemaVersion"] = version;
            root["Version"] = version;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Ok_SupportedSchemaVersions_AreReturnedUnmigrated(int version)
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root["SchemaVersion"] = version;
            root["Version"] = version;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.Equal(version, result.Settings!.SchemaVersion);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public async Task NewerSchema_ReturnsNoSettingsAndLocks(int version)
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root["SchemaVersion"] = version;
            root["Version"] = version;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.NewerSchema, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
        Assert.Equal("SchemaVersion", Assert.Single(result.Warnings).Path);
    }

    [Fact]
    public async Task NewerSchema_IsDecidedBeforeTheBodyIsDeserialised()
    {
        // The body is garbage for the current schema; a newer file must still be classified by its version alone.
        var path = WriteText("{ \"SchemaVersion\": 99, \"Indicators\": 12345, \"FutureSection\": { \"x\": [1,2] } }");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.NewerSchema, lockExpected: true, lossy: 1);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Schema_LegacyVersionAliasOnly_IsHonoured()
    {
        var current = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root.Remove("SchemaVersion");
            root["Version"] = 1;
        });
        var below = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root.Remove("SchemaVersion");
            root["Version"] = 0;
        });
        var newer = await WriteDamagedAsync(WithIndicators(Indicator("a")), root =>
        {
            root.Remove("SchemaVersion");
            root["Version"] = 99;
        });

        AssertStatus(await _reader.ReadAsync(current), WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        AssertStatus(await _reader.ReadAsync(below), WorkspaceReadStatus.Unreadable, lockExpected: true, lossy: 1);
        AssertStatus(await _reader.ReadAsync(newer), WorkspaceReadStatus.NewerSchema, lockExpected: true, lossy: 1);
    }

    [Fact]
    public async Task Ok_NoSchemaVersionAtAll_DefaultsToCurrent()
    {
        var path = WriteText("{ \"Name\": \"bare\" }");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.Equal(WorkspaceSettings.CurrentSchemaVersion, result.Settings!.SchemaVersion);
        Assert.Equal("bare", result.Settings.Name);
    }

    [Fact]
    public async Task Recovered_SchemaVersionOfWrongType_IsAFailedSectionNotAFileFailure()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root => root["SchemaVersion"] = "two");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("SchemaVersion", Assert.Single(result.Warnings).Path);
        Assert.Equal(WorkspaceSettings.CurrentSchemaVersion, result.Settings!.SchemaVersion);
        Assert.Equal("a", Assert.Single(result.Settings.Indicators).Id);
    }

    // ---- section / element rescue -----------------------------------------------------------------------

    [Fact]
    public async Task Recovered_UnknownIndicatorTypeString_DropsOnlyThatIndicator()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b")),
            root => root["Indicators"]![0]!["TypeEnum"] = BrokenEnumText);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("Indicators[0]", Assert.Single(result.Warnings).Path);
        Assert.Equal("b", Assert.Single(result.Settings!.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_TwoBrokenElementsInOneList_ReportsEachWithItsIndex()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b"), Indicator("c"), Indicator("d")), root =>
        {
            root["Indicators"]![0]!["TypeEnum"] = BrokenEnumText;
            root["Indicators"]![2]!["TypeEnum"] = BrokenEnumText;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 2);
        Assert.Equal(new[] { "Indicators[0]", "Indicators[2]" }, result.Warnings.Select(w => w.Path));
        Assert.Equal(new[] { "b", "d" }, result.Settings!.Indicators.Select(i => i.Id));
    }

    [Fact]
    public async Task Recovered_WholeSectionOfWrongType_KeepsDefaultAndOtherSections()
    {
        var settings = WithIndicators(Indicator("a"));
        settings.TagFilters.Add(new FilterSettings());
        var path = await WriteDamagedAsync(settings, root => root["TagFilters"] = "this should be an array of filters");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("TagFilters", Assert.Single(result.Warnings).Path);
        Assert.Empty(result.Settings!.TagFilters);
        Assert.Equal("Reader Test", result.Settings.Name);
        Assert.Equal("a", Assert.Single(result.Settings.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_ListSectionThatIsAnObject_DropsTheWholeSection()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root => root["Indicators"] = new JsonObject { ["x"] = 1 });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("Indicators", Assert.Single(result.Warnings).Path);
        Assert.Empty(result.Settings!.Indicators);
    }

    [Fact]
    public async Task Recovered_ScalarOfWrongType_KeepsDefaultForThatProperty()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a")), root => root["MaxCandleCount"] = "many");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("MaxCandleCount", Assert.Single(result.Warnings).Path);
        Assert.Equal(new WorkspaceSettings().MaxCandleCount, result.Settings!.MaxCandleCount);
        Assert.Equal("AAPL", result.Settings.SelectedTicker);
    }

    [Fact]
    public async Task Recovered_PropertyNamesAreMatchedCaseInsensitively()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b")), root =>
        {
            root["Indicators"]![0]!["TypeEnum"] = BrokenEnumText;
            var indicators = root["Indicators"]!.DeepClone();
            root.Remove("Indicators");
            root["iNdIcAtOrS"] = indicators;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("b", Assert.Single(result.Settings!.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_UnknownTopLevelPropertiesAreIgnoredWithoutWarning()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b")), root =>
        {
            root["FromTheFuture"] = new JsonObject { ["x"] = 1 };
            root["Indicators"]![1]!["TypeEnum"] = BrokenEnumText;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
    }

    // ---- nested rescue (Q-3') ---------------------------------------------------------------------------

    private static PanelChartSettings Panel(string tabId, params CoreIndicatorSettings[] indicators)
    {
        var panel = new PanelChartSettings { TabId = tabId, Symbol = "MSFT" };
        panel.Indicators.AddRange(indicators);
        return panel;
    }

    [Fact]
    public async Task Recovered_NestedPanelChartIndicatorBroken_DropsOnlyTheChildAndKeepsTheParent()
    {
        var settings = WithIndicators(Indicator("main"));
        settings.PanelCharts.Add(Panel("tab-0", Indicator("p0a"), Indicator("p0b")));
        settings.PanelCharts.Add(Panel("tab-1", Indicator("p1a"), Indicator("p1b"), Indicator("p1c")));
        var path = await WriteDamagedAsync(settings, root =>
            root["PanelCharts"]![1]!["Indicators"]![1]!["TypeEnum"] = BrokenEnumText);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("PanelCharts[1].Indicators[1]", Assert.Single(result.Warnings).Path);
        var panels = result.Settings!.PanelCharts;
        Assert.Equal(new[] { "tab-0", "tab-1" }, panels.Select(p => p.TabId));
        Assert.Equal(new[] { "p0a", "p0b" }, panels[0].Indicators.Select(i => i.Id));
        Assert.Equal(new[] { "p1a", "p1c" }, panels[1].Indicators.Select(i => i.Id));
        Assert.Equal("MSFT", panels[1].Symbol);
        Assert.Equal("main", Assert.Single(result.Settings.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_NestedDetachedTabIndicatorBroken_DropsOnlyTheChildAndKeepsTheParent()
    {
        var settings = WithIndicators();
        var tab = new DetachedTabInfo { TabId = "detached-0", Symbol = "NVDA", Width = 640 };
        tab.Indicators.Add(Indicator("d0"));
        tab.Indicators.Add(Indicator("d1"));
        settings.DetachedTabs.Add(tab);
        var path = await WriteDamagedAsync(settings, root =>
            root["DetachedTabs"]![0]!["Indicators"]![0]!["TypeEnum"] = BrokenEnumText);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("DetachedTabs[0].Indicators[0]", Assert.Single(result.Warnings).Path);
        var restored = Assert.Single(result.Settings!.DetachedTabs);
        Assert.Equal("detached-0", restored.TabId);
        Assert.Equal("NVDA", restored.Symbol);
        Assert.Equal(640, restored.Width);
        Assert.Equal("d1", Assert.Single(restored.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_NestedParentWithBrokenScalar_KeepsParentAndDefaultsOnlyThatScalar()
    {
        var settings = WithIndicators();
        settings.PanelCharts.Add(Panel("tab-0", Indicator("p0a")));
        var path = await WriteDamagedAsync(settings, root => root["PanelCharts"]![0]!["MaxCandleCount"] = "lots");

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("PanelCharts[0].MaxCandleCount", Assert.Single(result.Warnings).Path);
        var panel = Assert.Single(result.Settings!.PanelCharts);
        Assert.Equal("tab-0", panel.TabId);
        Assert.Equal("p0a", Assert.Single(panel.Indicators).Id);
    }

    [Fact]
    public async Task Recovered_NestedParentTabIdOfWrongType_DropsTheParentWithOneWarning()
    {
        var settings = WithIndicators();
        settings.PanelCharts.Add(Panel("tab-0", Indicator("p0a")));
        settings.PanelCharts.Add(Panel("tab-1", Indicator("p1a")));
        var path = await WriteDamagedAsync(settings, root => root["PanelCharts"]![1]!["TabId"] = 5);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("PanelCharts[1]", Assert.Single(result.Warnings).Path);
        Assert.Equal("tab-0", Assert.Single(result.Settings!.PanelCharts).TabId);
    }

    [Fact]
    public async Task Recovered_NestedParentWithoutTabIdAndBrokenChild_DropsTheParentWithOneWarning()
    {
        var settings = WithIndicators();
        settings.DetachedTabs.Add(new DetachedTabInfo { TabId = "keep" });
        var broken = new DetachedTabInfo { TabId = "lose" };
        broken.Indicators.Add(Indicator("x"));
        settings.DetachedTabs.Add(broken);
        var path = await WriteDamagedAsync(settings, root =>
        {
            var tab = root["DetachedTabs"]![1]!.AsObject();
            tab.Remove("TabId");
            tab["Indicators"]![0]!["TypeEnum"] = BrokenEnumText;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("DetachedTabs[1]", Assert.Single(result.Warnings).Path);
        Assert.Equal("keep", Assert.Single(result.Settings!.DetachedTabs).TabId);
    }

    [Fact]
    public async Task Recovered_NestedParentWithEmptyTabIdAndBrokenChild_DropsTheParent()
    {
        var settings = WithIndicators();
        settings.PanelCharts.Add(Panel("will-be-emptied", Indicator("x")));
        var path = await WriteDamagedAsync(settings, root =>
        {
            root["PanelCharts"]![0]!["TabId"] = string.Empty;
            root["PanelCharts"]![0]!["Indicators"]![0]!["TypeEnum"] = BrokenEnumText;
        });

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("PanelCharts[0]", Assert.Single(result.Warnings).Path);
        Assert.Empty(result.Settings!.PanelCharts);
    }

    [Fact]
    public async Task Recovered_NestedElementThatIsNotAnObject_IsDropped()
    {
        var settings = WithIndicators();
        settings.PanelCharts.Add(Panel("tab-0"));
        var path = await WriteDamagedAsync(settings, root => root["PanelCharts"]!.AsArray().Add(JsonValue.Create("not an object")));

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Recovered, lockExpected: true, lossy: 1);
        Assert.Equal("PanelCharts[1]", Assert.Single(result.Warnings).Path);
        Assert.Equal("tab-0", Assert.Single(result.Settings!.PanelCharts).TabId);
    }

    [Fact]
    public async Task Ok_PanelChartWithoutTabIdButOtherwiseValid_IsNotJudgedByTheReader()
    {
        // Policy for structurally valid but semantically empty elements belongs to the Run B normaliser.
        var settings = WithIndicators();
        settings.PanelCharts.Add(Panel("tab-0"));
        var path = await WriteDamagedAsync(settings, root => root["PanelCharts"]![0]!.AsObject().Remove("TabId"));

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.Equal(string.Empty, Assert.Single(result.Settings!.PanelCharts).TabId);
    }

    // ---- healthy files ----------------------------------------------------------------------------------

    [Fact]
    public async Task Ok_RoundTrip_EqualsWhatTheSerializerWrote()
    {
        var original = WithIndicators(Indicator("a", 5), Indicator("b", 50));
        original.ThemeSettings["ChartBackground"] = "#1E1E1E";
        original.PanelCharts.Add(Panel("tab-0", Indicator("p0", 7)));
        original.DetachedTabs.Add(new DetachedTabInfo { TabId = "d0", Symbol = "SPY", X = 12.5, Y = 30, Width = 800, Height = 600 });
        original.SelectedWatchlistId = original.WatchlistProfiles[0].Id;
        var path = NewPath();
        await _service.SaveAsync(original, path);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.Equal(
            JsonSerializer.Serialize(original, WorkspaceSerializationService.SharedJsonOptions),
            JsonSerializer.Serialize(result.Settings, WorkspaceSerializationService.SharedJsonOptions));
    }

    [Fact]
    public async Task Ok_NonFiniteNumbersWrittenByTheSerializer_ReadBackWithoutWarnings()
    {
        var original = WithIndicators();
        original.LeftPanelWidth = double.NaN;
        original.RightPanelWidth = double.PositiveInfinity;
        var path = NewPath();
        await _service.SaveAsync(original, path);

        var result = await _reader.ReadAsync(path);

        AssertStatus(result, WorkspaceReadStatus.Ok, lockExpected: false, lossy: 0);
        Assert.True(double.IsNaN(result.Settings!.LeftPanelWidth));
        Assert.True(double.IsPositiveInfinity(result.Settings.RightPanelWidth));
    }

    // ---- contract ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Read_NeverModifiesTheFileOrCreatesSiblings()
    {
        var path = await WriteDamagedAsync(WithIndicators(Indicator("a"), Indicator("b")),
            root => root["Indicators"]![0]!["TypeEnum"] = BrokenEnumText);
        var bytesBefore = await File.ReadAllBytesAsync(path);
        var writeTimeBefore = File.GetLastWriteTimeUtc(path);
        var entriesBefore = Directory.GetFileSystemEntries(_directory).OrderBy(e => e).ToArray();

        await _reader.ReadAsync(path);

        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(path));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(_directory).OrderBy(e => e).ToArray());
    }

    [Fact]
    public async Task Read_NullOrBlankPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync(string.Empty));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.ReadAsync("   "));
    }

    [Fact]
    public async Task Read_CancelledToken_ThrowsInsteadOfReportingAStatus()
    {
        var path = WriteText("{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, cts.Token));
    }

    [Fact]
    public void RequiresAutoSaveLock_FollowsTheStatusTable()
    {
        static WorkspaceLoadResult Make(WorkspaceReadStatus status, params WorkspaceWarningSeverity[] severities) =>
            new(status, null, severities.Select(s => new WorkspaceLoadWarning(s, "p", "m")).ToArray());

        Assert.False(Make(WorkspaceReadStatus.Missing).RequiresAutoSaveLock);
        Assert.False(Make(WorkspaceReadStatus.Empty).RequiresAutoSaveLock);
        Assert.False(Make(WorkspaceReadStatus.Ok).RequiresAutoSaveLock);
        Assert.False(Make(WorkspaceReadStatus.Ok, WorkspaceWarningSeverity.Cosmetic).RequiresAutoSaveLock);
        Assert.True(Make(WorkspaceReadStatus.Ok, WorkspaceWarningSeverity.Lossy).RequiresAutoSaveLock);
        Assert.True(Make(WorkspaceReadStatus.Unreadable).RequiresAutoSaveLock);
        Assert.True(Make(WorkspaceReadStatus.Recovered).RequiresAutoSaveLock);
        Assert.True(Make(WorkspaceReadStatus.NewerSchema).RequiresAutoSaveLock);
    }
}
