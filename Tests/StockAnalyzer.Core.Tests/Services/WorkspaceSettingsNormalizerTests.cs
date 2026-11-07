using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Stage 1 normalisation (D-3', Run B / W2). Pure function over a settings instance.</summary>
public sealed class WorkspaceSettingsNormalizerTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value, WorkspaceSerializationService.SharedJsonOptions);

    private static CoreIndicatorSettings Indicator(string id) => new()
    {
        Id = id,
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new CoreSmaParameter { Period = 14 }
    };

    private static void AssertSingle(IReadOnlyList<WorkspaceLoadWarning> warnings, WorkspaceWarningSeverity severity, string path)
    {
        var warning = Assert.Single(warnings);
        Assert.Equal(severity, warning.Severity);
        Assert.Equal(path, warning.Path);
    }

    [Fact]
    public void Normalize_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WorkspaceSettingsNormalizer.Normalize(null!));
    }

    [Fact]
    public void Normalize_HealthyWorkspace_ReportsNothingAndChangesNothing()
    {
        var settings = new WorkspaceSettings { SelectedTimeframe = "Daily", ChartType = "Candlestick", SelectedTicker = "AAPL" };
        settings.Indicators.Add(Indicator("a"));
        settings.PanelCharts.Add(new PanelChartSettings { TabId = "t0", Indicators = { Indicator("p") } });
        settings.DetachedTabs.Add(new DetachedTabInfo { TabId = "d0", Indicators = { Indicator("d") } });
        settings.SelectedWatchlistId = settings.WatchlistProfiles[0].Id;
        var before = Json(settings);

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Empty(warnings);
        Assert.Equal(before, Json(settings));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var settings = new WorkspaceSettings { SelectedTimeframe = "nope", ChartType = "nope", LeftPanelWidth = double.NaN };
        settings.Indicators.Add(null!);
        settings.TagFilters = null!;

        Assert.NotEmpty(WorkspaceSettingsNormalizer.Normalize(settings));
        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(settings));
    }

    // ---- lists ----------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_NullTopLevelList_BecomesEmptyList()
    {
        var settings = new WorkspaceSettings { TagFilters = null! };

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.NotNull(settings.TagFilters);
        Assert.Empty(settings.TagFilters);
        AssertSingle(warnings, WorkspaceWarningSeverity.Cosmetic, "TagFilters");
    }

    [Fact]
    public void Normalize_NullWatchlistProfiles_BecomesEmptyListAndClearsTheSelection()
    {
        var settings = new WorkspaceSettings { WatchlistProfiles = null!, SelectedWatchlistId = Guid.NewGuid() };

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Empty(settings.WatchlistProfiles);
        Assert.Null(settings.SelectedWatchlistId);
        Assert.Equal(new[] { "WatchlistProfiles", "SelectedWatchlistId" }, warnings.Select(w => w.Path));
        Assert.All(warnings, w => Assert.Equal(WorkspaceWarningSeverity.Cosmetic, w.Severity));
    }

    [Fact]
    public void Normalize_NullEntriesInIndicators_AreRemovedWithOneCosmeticWarning()
    {
        var settings = new WorkspaceSettings();
        settings.Indicators.AddRange(new[] { null!, Indicator("a"), null!, Indicator("b") });

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Equal(new[] { "a", "b" }, settings.Indicators.Select(i => i.Id));
        AssertSingle(warnings, WorkspaceWarningSeverity.Cosmetic, "Indicators");
    }

    [Fact]
    public void Normalize_NullEntriesInOtherLists_AreRemoved()
    {
        var settings = new WorkspaceSettings();
        settings.PanelCharts.Add(null!);
        settings.PanelCharts.Add(new PanelChartSettings { TabId = "t0" });
        settings.DetachedTabs.Add(null!);
        settings.TickerListVisibleColumns.AddRange(new[] { "A", null!, "B" });

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Equal("t0", Assert.Single(settings.PanelCharts).TabId);
        Assert.Empty(settings.DetachedTabs);
        Assert.Equal(new[] { "A", "B" }, settings.TickerListVisibleColumns);
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(WorkspaceWarningSeverity.Cosmetic, w.Severity));
    }

    [Fact]
    public void Normalize_NestedIndicatorLists_NullListNullEntryAndHealing()
    {
        var settings = new WorkspaceSettings();
        settings.PanelCharts.Add(new PanelChartSettings { TabId = "t0", Indicators = null! });
        settings.PanelCharts.Add(new PanelChartSettings { TabId = "t1", Indicators = { null!, Indicator("p") } });
        settings.DetachedTabs.Add(new DetachedTabInfo { TabId = "d0", Indicators = null! });
        settings.DetachedTabs.Add(new DetachedTabInfo { TabId = "d1", Indicators = { Indicator("d"), null! } });

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Empty(settings.PanelCharts[0].Indicators);
        Assert.Equal("p", Assert.Single(settings.PanelCharts[1].Indicators).Id);
        Assert.Empty(settings.DetachedTabs[0].Indicators);
        Assert.Equal("d", Assert.Single(settings.DetachedTabs[1].Indicators).Id);
        Assert.Equal(
            new[] { "PanelCharts[0].Indicators", "PanelCharts[1].Indicators", "DetachedTabs[0].Indicators", "DetachedTabs[1].Indicators" },
            warnings.Select(w => w.Path));
        Assert.All(warnings, w => Assert.Equal(WorkspaceWarningSeverity.Cosmetic, w.Severity));
    }

    // ---- indicator healing -----------------------------------------------------------------------------------

    [Fact]
    public void Normalize_HealsLegacyIndicatorsInPlaceWithoutWarnings()
    {
        var legacy = new CoreIndicatorSettings { Id = "cmo", TypeEnum = IndicatorType.CMO, ParameterObject = null };
        var settings = new WorkspaceSettings();
        settings.Indicators.Add(legacy);

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Empty(warnings);
        Assert.NotNull(Assert.Single(settings.Indicators).ParameterObject);
    }

    [Fact]
    public void Normalize_IndicatorThatCannotBeHealed_IsRemovedAsLossyAndTheRestSurvive()
    {
        var settings = new WorkspaceSettings();
        settings.Indicators.Add(Indicator("a"));
        settings.Indicators.Add(UnhealableIndicator());
        settings.Indicators.Add(Indicator("c"));
        settings.PanelCharts.Add(new PanelChartSettings { TabId = "t0", Indicators = { UnhealableIndicator(), Indicator("p") } });

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Equal(new[] { "a", "c" }, settings.Indicators.Select(i => i.Id));
        Assert.Equal("p", Assert.Single(settings.PanelCharts[0].Indicators).Id);
        Assert.Equal(new[] { "Indicators[1]", "PanelCharts[0].Indicators[0]" }, warnings.Select(w => w.Path));
        Assert.All(warnings, w => Assert.Equal(WorkspaceWarningSeverity.Lossy, w.Severity));
    }

    /// <summary>
    /// An indicator whose healing really throws: a persisted series-colour list that contains a null entry makes
    /// AutoHeal dereference it (found by probing; an undefined TypeEnum value does NOT throw).
    /// </summary>
    private static CoreIndicatorSettings UnhealableIndicator()
    {
        var typeWithSeriesColors = DefaultCoreIndicatorSettings.GetDefault().First(d => d.SeriesColors is { Count: > 0 }).TypeEnum;
        return new CoreIndicatorSettings
        {
            Id = "bad",
            TypeEnum = typeWithSeriesColors,
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> { null! }
        };
    }

    [Fact]
    public void AutoHeal_IsIdempotent_ForEveryDefaultIndicatorAndItsLegacyShape()
    {
        var mismatches = new List<string>();
        foreach (var legacyShape in new[] { false, true })
        {
            foreach (var indicator in DefaultCoreIndicatorSettings.GetDefault())
            {
                if (legacyShape)
                {
                    indicator.ParameterObject = null;
                }

                DefaultCoreIndicatorSettings.AutoHeal(indicator);
                var once = Json(indicator);
                DefaultCoreIndicatorSettings.AutoHeal(indicator);
                var twice = Json(indicator);
                if (once != twice)
                {
                    mismatches.Add($"{indicator.TypeEnum} legacyShape={legacyShape}");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void AutoHeal_IsIdempotent_ForLegacyFixedRanges()
    {
        var legacy = new[]
        {
            new CoreIndicatorSettings { Id = "c", TypeEnum = IndicatorType.ClothoidOscillator, MinValue = -100m, MaxValue = 100m },
            new CoreIndicatorSettings { Id = "r", TypeEnum = IndicatorType.RollingBeta, MinValue = -3m, MaxValue = 3m }
        };
        foreach (var indicator in legacy)
        {
            DefaultCoreIndicatorSettings.AutoHeal(indicator);
            var once = Json(indicator);
            DefaultCoreIndicatorSettings.AutoHeal(indicator);
            Assert.Equal(once, Json(indicator));
        }
    }

    // ---- enum text --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Daily")]
    [InlineData("daily")]
    public void Normalize_KnownTimeframe_IsKept(string text)
    {
        var settings = new WorkspaceSettings { SelectedTimeframe = text };

        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(settings));
        Assert.Equal(text, settings.SelectedTimeframe);
    }

    [Theory]
    [InlineData("Fortnightly")]
    [InlineData("")]
    [InlineData("987654")]
    public void Normalize_UnknownTimeframe_BecomesNull(string text)
    {
        var settings = new WorkspaceSettings { SelectedTimeframe = text };

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Null(settings.SelectedTimeframe);
        AssertSingle(warnings, WorkspaceWarningSeverity.Cosmetic, "SelectedTimeframe");
    }

    [Fact]
    public void Normalize_ChartType_FollowsTheCaseSensitiveApplySite()
    {
        var known = new WorkspaceSettings { ChartType = nameof(ChartType.HeikinAshi) };
        var wrongCase = new WorkspaceSettings { ChartType = "heikinashi" };
        var unknown = new WorkspaceSettings { ChartType = "Pyramid" };

        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(known));
        Assert.Equal(nameof(ChartType.HeikinAshi), known.ChartType);
        AssertSingle(WorkspaceSettingsNormalizer.Normalize(wrongCase), WorkspaceWarningSeverity.Cosmetic, "ChartType");
        Assert.Null(wrongCase.ChartType);
        AssertSingle(WorkspaceSettingsNormalizer.Normalize(unknown), WorkspaceWarningSeverity.Cosmetic, "ChartType");
        Assert.Null(unknown.ChartType);
    }

    [Fact]
    public void Normalize_NullEnumText_StaysNullWithoutWarning()
    {
        var settings = new WorkspaceSettings { SelectedTimeframe = null, ChartType = null };

        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(settings));
    }

    // ---- geometry ---------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_NonFinitePanelSizes_BecomeLayoutDefaults()
    {
        var settings = new WorkspaceSettings
        {
            LeftPanelWidth = double.NaN,
            RightPanelWidth = double.PositiveInfinity,
            TopPanelHeight = double.NegativeInfinity,
            BottomPanelHeight = double.NaN
        };

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Equal(LayoutConstants.DefaultLeftWidth, settings.LeftPanelWidth);
        Assert.Equal(LayoutConstants.DefaultRightWidth, settings.RightPanelWidth);
        Assert.Equal(LayoutConstants.DefaultTopHeight, settings.TopPanelHeight);
        Assert.Equal(LayoutConstants.DefaultBottomHeight, settings.BottomPanelHeight);
        Assert.Equal(4, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(WorkspaceWarningSeverity.Cosmetic, w.Severity));
    }

    [Fact]
    public void Normalize_FiniteButOutOfRangeSizes_AreLeftToTheApplyCodeClamp()
    {
        var settings = new WorkspaceSettings { LeftPanelWidth = -5, RightPanelWidth = 1e9, TopPanelHeight = 0, BottomPanelHeight = 5000 };

        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(settings));
        Assert.Equal(-5, settings.LeftPanelWidth);
        Assert.Equal(1e9, settings.RightPanelWidth);
    }

    [Fact]
    public void Normalize_NonFiniteDetachedWindowGeometry_BecomesDefaults()
    {
        var settings = new WorkspaceSettings();
        settings.DetachedTabs.Add(new DetachedTabInfo { TabId = "d0", X = double.NaN, Y = double.PositiveInfinity, Width = double.NaN, Height = 300 });

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        var tab = Assert.Single(settings.DetachedTabs);
        Assert.Equal(LayoutConstants.DefaultDetachedWindowX, tab.X);
        Assert.Equal(LayoutConstants.DefaultDetachedWindowY, tab.Y);
        Assert.Equal(LayoutConstants.DefaultDetachedWindowWidth, tab.Width);
        Assert.Equal(300, tab.Height);
        Assert.Equal(new[] { "DetachedTabs[0].X", "DetachedTabs[0].Y", "DetachedTabs[0].Width" }, warnings.Select(w => w.Path));
    }

    // ---- watchlist selection ----------------------------------------------------------------------------------

    [Fact]
    public void Normalize_SelectedWatchlistMissingFromProfiles_IsCleared()
    {
        var settings = new WorkspaceSettings { SelectedWatchlistId = Guid.NewGuid() };

        var warnings = WorkspaceSettingsNormalizer.Normalize(settings);

        Assert.Null(settings.SelectedWatchlistId);
        AssertSingle(warnings, WorkspaceWarningSeverity.Cosmetic, "SelectedWatchlistId");
    }

    [Fact]
    public void Normalize_SelectedWatchlistPresent_IsKept()
    {
        var settings = new WorkspaceSettings();
        settings.SelectedWatchlistId = settings.WatchlistProfiles[0].Id;

        Assert.Empty(WorkspaceSettingsNormalizer.Normalize(settings));
        Assert.NotNull(settings.SelectedWatchlistId);
    }

    // ---- together with the reader ----------------------------------------------------------------------------

    [Fact]
    public async Task ReaderThenNormalizer_FreshlySavedWorkspace_NeedsNoLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_normalizer_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var service = new WorkspaceSerializationService();
            var original = new WorkspaceSettings { SelectedTimeframe = "Daily", ChartType = "Candlestick" };
            original.Indicators.Add(Indicator("a"));
            await service.SaveAsync(original, path);

            var result = await new WorkspaceTolerantReader().ReadAsync(path);
            var all = result.Warnings.Concat(WorkspaceSettingsNormalizer.Normalize(result.Settings!)).ToArray();

            Assert.Equal(WorkspaceReadStatus.Ok, result.Status);
            Assert.Empty(all);
            Assert.False(new WorkspaceLoadResult(result.Status, result.Settings, all).RequiresAutoSaveLock);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReaderThenNormalizer_JsonNullWatchlistList_IsNormalisedWithoutALock()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_normalizer_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{ \"WatchlistProfiles\": null, \"SelectedTimeframe\": \"Daily\" }");

            var result = await new WorkspaceTolerantReader().ReadAsync(path);
            var warnings = WorkspaceSettingsNormalizer.Normalize(result.Settings!);

            Assert.Equal(WorkspaceReadStatus.Ok, result.Status);
            AssertSingle(warnings, WorkspaceWarningSeverity.Cosmetic, "WatchlistProfiles");
            Assert.False(new WorkspaceLoadResult(result.Status, result.Settings, warnings).RequiresAutoSaveLock);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReaderThenNormalizer_RecoveredFileKeepsItsLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_normalizer_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{ \"Indicators\": 5, \"SelectedTimeframe\": \"bogus\" }");

            var result = await new WorkspaceTolerantReader().ReadAsync(path);
            var all = result.Warnings.Concat(WorkspaceSettingsNormalizer.Normalize(result.Settings!)).ToArray();

            Assert.Equal(WorkspaceReadStatus.Recovered, result.Status);
            Assert.Contains(all, w => w.Severity == WorkspaceWarningSeverity.Lossy && w.Path == "Indicators");
            Assert.Contains(all, w => w.Severity == WorkspaceWarningSeverity.Cosmetic && w.Path == "SelectedTimeframe");
            Assert.True(new WorkspaceLoadResult(result.Status, result.Settings, all).RequiresAutoSaveLock);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
