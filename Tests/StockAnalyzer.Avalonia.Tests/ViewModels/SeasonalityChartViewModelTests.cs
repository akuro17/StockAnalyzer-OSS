using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class SeasonalityChartViewModelTests
{
    [Fact]
    public void Constructor_NullDataSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SeasonalityChartViewModel(null!));
    }

    [AvaloniaFact]
    public void DefaultYearsToOverlay_MatchesTheSettingsDefault()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearsToOverlay, viewModel.YearsToOverlay);
    }

    [AvaloniaFact]
    public void Constructor_WithActiveSymbolButNoResult_PullsTheFirstOverlayWithoutAReadyCaption()
    {
        // Startup workspace restore sets the symbol (and raises the data source's one-shot Changed)
        // before this view model exists, so the panel must analyse the already-active symbol on
        // construction rather than sitting at a "Ready for ..." caption until the user re-picks it.
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };

        // Zero debounce via the constructor argument so the constructor's own first schedule runs
        // deterministically on the pump below, with no wall-clock wait.
        using var viewModel = new SeasonalityChartViewModel(source, reanalyzeDebounce: TimeSpan.Zero);

        Assert.DoesNotContain("Ready", viewModel.StatusText);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.True(viewModel.HasDrawnResult);
        Assert.False(viewModel.HasStatusText);
    }

    [AvaloniaFact]
    public void Constructor_WithoutActiveSymbol_StaysIdleAndDoesNotAnalyze()
    {
        var source = new FakeSeasonalityDataSource();

        using var viewModel = new SeasonalityChartViewModel(source);

        Assert.Equal(0, source.AnalyzeCalls);
        Assert.Equal(LocalizationManager.Instance["Seasonality_Status_Idle"], viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Analyze_WithoutActiveSymbol_SetsStatusAndDoesNotCallDataSource()
    {
        var source = new FakeSeasonalityDataSource();
        using var viewModel = new SeasonalityChartViewModel(source);

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, source.AnalyzeCalls);
        Assert.Equal(LocalizationManager.Instance["Seasonality_Status_NoSymbol"], viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Analyze_InvalidYears_SurfacesValidationErrorWithoutCallingDataSource()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 0 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, source.AnalyzeCalls);
        Assert.False(viewModel.IsAnalyzing);
        Assert.Contains("YearsToOverlay", viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Analyze_Success_CallsDataSourceAndClearsAnalyzingFlag()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 4 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.Equal(4u, source.LastParameters!.Value.YearsToOverlay);
        Assert.False(viewModel.IsAnalyzing);
    }

    [AvaloniaFact]
    public async Task Analyze_PropagatesResultPublishedByDataSource()
    {
        SeasonalityChartResult result = BuildResult();
        var source = new FakeSeasonalityDataSource
        {
            ActiveSymbol = "MSFT",
            OnAnalyze = _ => Task.CompletedTask,
        };
        source.ResultToPublish = result;
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(result, viewModel.Result);
        Assert.Equal(
            string.Format(
                CultureInfo.InvariantCulture,
                LocalizationManager.Instance["Seasonality_Status_Overlaid"],
                result.CalendarYears.Count,
                "MSFT"),
            viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Analyze_DataSourceThrowsInvalidOperation_ShowsMessage()
    {
        var source = new FakeSeasonalityDataSource
        {
            ActiveSymbol = "MSFT",
            OnAnalyze = _ => throw new InvalidOperationException("boom"),
        };
        using var viewModel = new SeasonalityChartViewModel(source);

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("boom", viewModel.StatusText);
        Assert.False(viewModel.IsAnalyzing);
    }

    [AvaloniaFact]
    public void Dispose_UnsubscribesFromChanged()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        var viewModel = new SeasonalityChartViewModel(source);

        viewModel.Dispose();
        source.ActiveSymbol = "OTHER";
        source.Current = BuildResult();
        source.RaiseChanged();

        // The handler was detached at Dispose, so the freshly published result never reaches the view model.
        Assert.Null(viewModel.Result);
        Assert.False(viewModel.HasDrawnResult);
    }

    [AvaloniaFact]
    public void MeanPathAndGridOptions_HaveExpectedDefaults()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        Assert.False(viewModel.ShowMeanPath);
        Assert.Equal(60u, viewModel.GridOpacityPercent);
        Assert.False(viewModel.GridDashed);
        Assert.False(viewModel.UseVisibleRangeOrigin);
    }

    [AvaloniaFact]
    public void TogglingShowMeanPath_ReanalyzesWithIncludeMeanPathParameter()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };

        viewModel.ShowMeanPath = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.True(source.LastParameters!.Value.IncludeMeanPath);
    }

    [AvaloniaFact]
    public void ChangingYearsToOverlay_AutomaticallyReanalyzes()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source) { ReanalyzeDebounceInterval = TimeSpan.Zero };

        viewModel.YearsToOverlay = 7;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.Equal(7u, source.LastParameters!.Value.YearsToOverlay);
    }

    [AvaloniaFact]
    public void ConstructedWithSettingsManager_SeedsYearsToOverlayFromSettings()
    {
        var manager = new MockChartSettingsManager();
        manager.UpdatePreview(manager.Current with { SeasonalityYearsToOverlay = 23 });

        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource(), manager);

        Assert.Equal(23u, viewModel.YearsToOverlay);
    }

    [AvaloniaFact]
    public void SettingsYearsToOverlayChange_SyncsTheChartInputAndReanalyzes()
    {
        var manager = new MockChartSettingsManager();
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source, manager, reanalyzeDebounce: TimeSpan.Zero);
        Dispatcher.UIThread.RunJobs();
        int callsBefore = source.AnalyzeCalls;

        manager.UpdatePreview(manager.Current with { SeasonalityYearsToOverlay = 30 });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(30u, viewModel.YearsToOverlay);
        Assert.True(source.AnalyzeCalls > callsBefore);
        Assert.Equal(30u, source.LastParameters!.Value.YearsToOverlay);
    }

    [AvaloniaFact]
    public void ChangingTheChartInput_WritesTheOverlayDepthThroughToSettings()
    {
        var manager = new MockChartSettingsManager();
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source, manager, reanalyzeDebounce: TimeSpan.Zero);

        viewModel.YearsToOverlay = 17;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(17u, manager.Current.SeasonalityYearsToOverlay);
    }

    [AvaloniaFact]
    public void ChangingManualScaleFactor_AutomaticallyReanalyzes()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source) { ReanalyzeDebounceInterval = TimeSpan.Zero };

        viewModel.ManualScaleFactor = 250m;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.Equal(250m, source.LastParameters!.Value.ManualScaleFactor);
    }

    [AvaloniaFact]
    public void DataSourceClearingItsResult_AutomaticallyReanalyzes()
    {
        var source = new FakeSeasonalityDataSource { ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { ReanalyzeDebounceInterval = TimeSpan.Zero };

        // The chart bridge sets a symbol and clears any stale result -> the panel pulls a fresh overlay.
        source.SetActiveSymbol("MSFT");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
    }

    [AvaloniaFact]
    public async Task RapidParameterChanges_DebounceIntoASingleReanalyze()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            ReanalyzeDebounceInterval = TimeSpan.FromMilliseconds(50),
        };

        viewModel.YearsToOverlay = 4;
        viewModel.YearsToOverlay = 5;
        viewModel.YearsToOverlay = 6;
        Assert.Equal(0, source.AnalyzeCalls); // nothing fired synchronously

        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.Equal(6u, source.LastParameters!.Value.YearsToOverlay);
    }

    [AvaloniaFact]
    public void ChangingAGridRenderOption_DoesNotReanalyze()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };
        using var viewModel = new SeasonalityChartViewModel(source);

        viewModel.GridDashed = true;
        viewModel.UseVisibleRangeOrigin = true;
        viewModel.GridOpacityPercent = 25u;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, source.AnalyzeCalls);
    }

    [AvaloniaFact]
    public void SettingsFlyout_StartsCollapsed()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        Assert.Null(viewModel.OpenSettingsGroup);
        Assert.False(viewModel.IsSettingsFlyoutOpen);
        Assert.False(viewModel.IsBasicGroupOpen);
        Assert.False(viewModel.IsDisplayGroupOpen);
        Assert.False(viewModel.IsSeriesGroupOpen);
    }

    [AvaloniaFact]
    public void ToggleSettingsGroup_OpensSwitchesAndCollapsesTheFlyout()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        viewModel.ToggleSettingsGroupCommand.Execute(SeasonalityChartViewModel.SettingsGroupDisplay);
        Assert.True(viewModel.IsSettingsFlyoutOpen);
        Assert.True(viewModel.IsDisplayGroupOpen);
        Assert.False(viewModel.IsBasicGroupOpen);

        viewModel.ToggleSettingsGroupCommand.Execute(SeasonalityChartViewModel.SettingsGroupBasic);
        Assert.True(viewModel.IsBasicGroupOpen);
        Assert.False(viewModel.IsDisplayGroupOpen);

        viewModel.ToggleSettingsGroupCommand.Execute(SeasonalityChartViewModel.SettingsGroupBasic);
        Assert.Null(viewModel.OpenSettingsGroup);
        Assert.False(viewModel.IsSettingsFlyoutOpen);
    }

    [AvaloniaFact]
    public void CollapseSettingsFlyout_ClosesTheOpenGroup()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());
        viewModel.ToggleSettingsGroupCommand.Execute(SeasonalityChartViewModel.SettingsGroupDisplay);
        Assert.True(viewModel.IsSettingsFlyoutOpen);

        viewModel.CollapseSettingsFlyout();

        Assert.Null(viewModel.OpenSettingsGroup);
        Assert.False(viewModel.IsSettingsFlyoutOpen);
        Assert.False(viewModel.IsDisplayGroupOpen);
    }

    [AvaloniaFact]
    public void CollapseSettingsFlyout_WhenAlreadyCollapsed_IsANoOp()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());
        Assert.Null(viewModel.OpenSettingsGroup);

        viewModel.CollapseSettingsFlyout();

        Assert.Null(viewModel.OpenSettingsGroup);
        Assert.False(viewModel.IsSettingsFlyoutOpen);
    }

    [AvaloniaFact]
    public void RadiusModes_ExposesPercentAndZScoreOnly_NotSignedUnitFromBounds()
    {
        Assert.Equal(2, SeasonalityChartViewModel.RadiusModes.Count);
        Assert.Contains(SeasonalityRadiusMode.PercentVsYearStart, SeasonalityChartViewModel.RadiusModes);
        Assert.Contains(SeasonalityRadiusMode.ZScoreWithinYear, SeasonalityChartViewModel.RadiusModes);
        Assert.DoesNotContain(SeasonalityRadiusMode.SignedUnitFromBounds, SeasonalityChartViewModel.RadiusModes);
    }

    [AvaloniaFact]
    public void Constructor_WithoutResult_HasNoSeriesRows()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT" };

        using var viewModel = new SeasonalityChartViewModel(source);

        Assert.Empty(viewModel.SeriesRows);
        Assert.False(viewModel.HasSeriesRows);
    }

    [AvaloniaFact]
    public async Task Analyze_Success_PopulatesOneSeriesRowForTheBaseSymbol()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.HasSeriesRows);
        Assert.Equal(new[] { 0 }, viewModel.SeriesRows.Select(r => r.SeriesId).ToArray());
        Assert.Equal("MSFT", viewModel.SeriesRows[0].Label);
        Assert.Equal(SeasonalityRadiusMode.PercentVsYearStart, viewModel.SeriesRows[0].RadiusMode);
    }

    [AvaloniaFact]
    public async Task ChangingSeriesRowRadiusMode_ReanalyzesAndPushesTheOverrideMap()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, source.AnalyzeCalls);

        viewModel.SeriesRows[0].RadiusMode = SeasonalityRadiusMode.ZScoreWithinYear;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, source.AnalyzeCalls);
        Assert.Equal(SeasonalityRadiusMode.ZScoreWithinYear, source.RadiusModeOverrides[0]);
    }

    [AvaloniaFact]
    public async Task RepublishingTheSameSeriesLayout_DoesNotReanalyze()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
        Assert.Single(viewModel.SeriesRows);
    }

    [AvaloniaFact]
    public async Task ResultClearedToNull_EmptiesSeriesRows()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(viewModel.SeriesRows);

        source.Current = null;
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewModel.SeriesRows);
        Assert.False(viewModel.HasSeriesRows);
    }

    [AvaloniaFact]
    public void DisplayMode_DefaultsToPolarClock_WithDerivedFlags()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        Assert.Equal(SeasonalityDisplayMode.PolarClock, viewModel.DisplayMode);
        Assert.True(viewModel.IsPolarClock);
        Assert.False(viewModel.IsLinearOverlay);
        Assert.False(viewModel.IsMonthlyReturnsTable);
        Assert.True(viewModel.IsMeanPathSettingEnabled);
        Assert.Equal(
            new[]
            {
                SeasonalityDisplayMode.PolarClock,
                SeasonalityDisplayMode.LinearYearOverlay,
                SeasonalityDisplayMode.MonthlyReturnsTable,
            },
            viewModel.DisplayModes.ToArray());
    }

    [AvaloniaFact]
    public void SettingLinearOverlay_FlipsDerivedFlagsAndDisablesTheMeanPathSetting()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        viewModel.DisplayMode = SeasonalityDisplayMode.LinearYearOverlay;

        Assert.False(viewModel.IsPolarClock);
        Assert.True(viewModel.IsLinearOverlay);
        Assert.False(viewModel.IsMonthlyReturnsTable);
        Assert.False(viewModel.IsMeanPathSettingEnabled);
    }

    [AvaloniaFact]
    public void SettingMonthlyReturnsTable_FlipsDerivedFlagsAndDisablesTheMeanPathSetting()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        viewModel.DisplayMode = SeasonalityDisplayMode.MonthlyReturnsTable;

        Assert.False(viewModel.IsPolarClock);
        Assert.False(viewModel.IsLinearOverlay);
        Assert.True(viewModel.IsMonthlyReturnsTable);
        Assert.False(viewModel.IsMeanPathSettingEnabled);
    }

    [AvaloniaFact]
    public async Task HasDrawnResult_IsTrueOnlyAfterAnOverlayIsDrawn()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };
        Assert.False(viewModel.HasDrawnResult);

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.HasDrawnResult);

        source.Current = null;
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.HasDrawnResult);
    }

    [AvaloniaFact]
    public async Task ChangingDisplayMode_DoesNotReanalyze()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, source.AnalyzeCalls);

        viewModel.DisplayMode = SeasonalityDisplayMode.LinearYearOverlay;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.AnalyzeCalls);
    }

    [AvaloniaFact]
    public async Task MonthlyReturns_IsBuiltFromTheResult_OnAnalysis()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewModel.MonthlyReturns);
        Assert.True(viewModel.MonthlyReturns!.HasData);
        Assert.True(viewModel.HasMonthlyReturns);
        Assert.Equal(SeasonalityMonthlyReturnsTable.RowCount, viewModel.MonthlyReturns.Rows.Count);
    }

    [AvaloniaFact]
    public async Task MonthlyReturns_ClearedWhenResultGoesNull()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.HasMonthlyReturns);

        source.Current = null;
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.MonthlyReturns);
        Assert.False(viewModel.HasMonthlyReturns);
    }

    [AvaloniaFact]
    public void SeasonalitySettings_DefaultsExposeTenColourPaletteAndDefaultFonts()
    {
        using var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource());

        Assert.Equal(10, viewModel.YearPalette.Count);
        Assert.Equal((double)ChartSettingsConstants.DefaultSeasonalityFontSize, viewModel.LegendFontSize);
        Assert.Equal((double)ChartSettingsConstants.DefaultSeasonalityFontSize, viewModel.AxisFontSize);
        Assert.Equal((double)ChartSettingsConstants.DefaultSeasonalityFontSize, viewModel.MonthlyTableFontSize);
        Assert.NotNull(viewModel.MonthlyReturnsStyle.PositiveBackground);
        Assert.NotNull(viewModel.MonthlyReturnsStyle.NeutralBackground);
    }

    [AvaloniaFact]
    public async Task SeasonalitySettingsChange_RefreshesPaletteFontsAndMonthlyReturns()
    {
        var manager = new MockChartSettingsManager();
        var source = new FakeSeasonalityDataSource { Current = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source, manager);
        Assert.NotNull(viewModel.MonthlyReturns);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await manager.UpdateAsync(manager.Current with
        {
            SeasonalityLegendFontSize = 20f,
            SeasonalityAxisFontSize = 22f,
            SeasonalityMonthlyTableFontSize = 18f,
            SeasonalityYearColor1 = "#FF102030",
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(20d, viewModel.LegendFontSize);
        Assert.Equal(22d, viewModel.AxisFontSize);
        Assert.Equal(18d, viewModel.MonthlyTableFontSize);
        Assert.Equal(
            HsvData.FromHtmlSafe("#FF102030").ToIndicatorColor().ToAvaloniaColor(),
            viewModel.YearPalette[0]);
        Assert.Equal(18d, viewModel.MonthlyReturns!.FontSize);
        Assert.Contains(nameof(SeasonalityChartViewModel.YearPalette), changed);
    }

    [AvaloniaFact]
    public async Task Dispose_UnsubscribesFromSettingsChanges()
    {
        var manager = new MockChartSettingsManager();
        var viewModel = new SeasonalityChartViewModel(new FakeSeasonalityDataSource(), manager);

        viewModel.Dispose();
        await manager.UpdateAsync(manager.Current with { SeasonalityLegendFontSize = 30f });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((double)ChartSettingsConstants.DefaultSeasonalityFontSize, viewModel.LegendFontSize);
    }

    [AvaloniaFact]
    public async Task Analyze_WhenDataSourceThrowsUnexpectedError_ClearsAnalyzingFlagAndReportsIt()
    {
        var source = new FakeSeasonalityDataSource
        {
            ActiveSymbol = "MSFT",
            OnAnalyze = _ => throw new System.IO.DirectoryNotFoundException("Data/Daily missing"),
        };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 4 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        // The panel must not be left wedged at "Analyzing ..." when the analysis fails with an
        // exception type the old catch filter (InvalidOperationException / ArgumentException) missed.
        Assert.False(viewModel.IsAnalyzing);
        Assert.DoesNotContain("Analyzing", viewModel.StatusText);
        Assert.Equal(
            string.Format(
                CultureInfo.InvariantCulture,
                LocalizationManager.Instance["Seasonality_Status_AnalyzeFailed"],
                "MSFT",
                nameof(System.IO.DirectoryNotFoundException)),
            viewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Analyze_PopulatesOneYearRowPerOverlaidYear_NewestFirstAllVisibleInheritingTheCommonThickness()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildMultiYearResult() };
        using var viewModel = new SeasonalityChartViewModel(source) { YearsToOverlay = 3 };

        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(viewModel.YearRows);
        Assert.True(viewModel.HasYearRows);
        Assert.Equal(
            viewModel.Result!.CalendarYears.OrderByDescending(y => y).ToArray(),
            viewModel.YearRows.Select(r => r.CalendarYear).ToArray());
        Assert.All(viewModel.YearRows, row =>
        {
            Assert.True(row.IsVisible);
            Assert.Null(row.LineThicknessOverride);
            Assert.Equal(ChartSettingsConstants.DefaultSeasonalityLineThickness, row.EffectiveLineThickness);
        });
        Assert.Equal(
            viewModel.YearRows.Select(r => r.CalendarYear).ToArray(),
            viewModel.YearDrawStates.Select(s => s.CalendarYear).ToArray());
    }

    [AvaloniaFact]
    public async Task TogglingAYearRowOff_UpdatesTheDrawStateWithoutReanalyzing()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        int callsBefore = source.AnalyzeCalls;
        int year = viewModel.YearRows[0].CalendarYear;

        viewModel.YearRows[0].IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callsBefore, source.AnalyzeCalls);
        Assert.False(viewModel.YearDrawStates.Single(s => s.CalendarYear == year).IsVisible);
    }

    [AvaloniaFact]
    public async Task SettingAPerYearThicknessOverride_ChangesTheEffectiveWidthAndDrawStateWithoutReanalyzing()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        int callsBefore = source.AnalyzeCalls;
        int year = viewModel.YearRows[0].CalendarYear;

        viewModel.YearRows[0].LineThicknessOverride = 4.5f;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callsBefore, source.AnalyzeCalls);
        Assert.Equal(4.5f, viewModel.YearRows[0].EffectiveLineThickness);
        Assert.Equal(4.5f, viewModel.YearDrawStates.Single(s => s.CalendarYear == year).EffectiveLineThickness);
    }

    [AvaloniaFact]
    public async Task CommonLineThicknessChange_FollowsUneditedRowsButLeavesAnOverriddenRow()
    {
        var manager = new MockChartSettingsManager();
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildMultiYearResult() };
        using var viewModel = new SeasonalityChartViewModel(source, manager, reanalyzeDebounce: TimeSpan.Zero);
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.YearRows.Count >= 2);
        int editedYear = viewModel.YearRows[0].CalendarYear;
        int uneditedYear = viewModel.YearRows[1].CalendarYear;

        viewModel.YearRows[0].LineThicknessOverride = 4.5f;
        Dispatcher.UIThread.RunJobs();

        manager.UpdatePreview(manager.Current with { SeasonalityLineThickness = 3.0f });
        Dispatcher.UIThread.RunJobs();

        // Unedited row follows the new common value; the overridden row keeps its own width (D3).
        Assert.Equal(3.0f, viewModel.YearRows.Single(r => r.CalendarYear == uneditedYear).EffectiveLineThickness);
        Assert.Equal(4.5f, viewModel.YearRows.Single(r => r.CalendarYear == editedYear).EffectiveLineThickness);
        Assert.Equal(3.0f, viewModel.YearDrawStates.Single(s => s.CalendarYear == uneditedYear).EffectiveLineThickness);
        Assert.Equal(4.5f, viewModel.YearDrawStates.Single(s => s.CalendarYear == editedYear).EffectiveLineThickness);

        // Clearing the override lets the edited row inherit the common value too.
        viewModel.YearRows.Single(r => r.CalendarYear == editedYear).LineThicknessOverride = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3.0f, viewModel.YearDrawStates.Single(s => s.CalendarYear == editedYear).EffectiveLineThickness);
    }

    [AvaloniaFact]
    public async Task PerYearState_SurvivesASameSymbolReanalysis_ButResetsOnASymbolChange()
    {
        var source = new FakeSeasonalityDataSource { ActiveSymbol = "MSFT", ResultToPublish = BuildResult() };
        using var viewModel = new SeasonalityChartViewModel(source)
        {
            YearsToOverlay = 3,
            ReanalyzeDebounceInterval = TimeSpan.Zero,
        };
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        int year = viewModel.YearRows[0].CalendarYear;

        viewModel.YearRows[0].IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        // Same symbol: a fresh analysis keeps the per-year visibility (decision D3).
        await viewModel.RunAnalysisAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.YearRows.Single(r => r.CalendarYear == year).IsVisible);

        // Symbol change: the session state is cleared, so the year is visible again.
        source.ActiveSymbol = "OTHER";
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.YearRows.Single(r => r.CalendarYear == year).IsVisible);
    }

    private static SeasonalityChartResult BuildResult()
    {
        var series = new SeasonalitySeriesInput(
            0,
            "MSFT",
            SeasonalityRadiusMode.PercentVsYearStart,
            new[]
            {
                new SeasonalityPoint(new DateTime(2023, 1, 2), 100m),
                new SeasonalityPoint(new DateTime(2023, 6, 1), 110m),
            });

        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3));
    }

    private static SeasonalityChartResult BuildMultiYearResult()
    {
        var points = new List<SeasonalityPoint>();
        decimal value = 100m;
        for (int year = 2021; year <= 2023; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
                value += month % 2 == 0 ? 3m : -1m;
            }
        }

        var series = new SeasonalitySeriesInput(0, "MSFT", SeasonalityRadiusMode.PercentVsYearStart, points);
        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3));
    }

    private sealed class FakeSeasonalityDataSource : ISeasonalityChartDataSource
    {
        public SeasonalityChartResult? Current { get; set; }

        public SeasonalityChartResult? ResultToPublish { get; set; }

        public string? ActiveSymbol { get; set; }

        public bool HasActiveSymbol => !string.IsNullOrWhiteSpace(ActiveSymbol);

        public System.Collections.Generic.IReadOnlyDictionary<int, SeasonalityRadiusMode> RadiusModeOverrides { get; private set; }
            = new System.Collections.Generic.Dictionary<int, SeasonalityRadiusMode>();

        public int AnalyzeCalls { get; private set; }

        public SeasonalityChartParameters? LastParameters { get; private set; }

        public Func<SeasonalityChartParameters, Task>? OnAnalyze { get; set; }

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public void SetActiveSymbol(string? symbol)
        {
            ActiveSymbol = symbol;
            RaiseChanged();
        }

        public void SetRadiusModeOverrides(System.Collections.Generic.IReadOnlyDictionary<int, SeasonalityRadiusMode>? overridesBySeriesId)
        {
            RadiusModeOverrides = overridesBySeriesId is null
                ? new System.Collections.Generic.Dictionary<int, SeasonalityRadiusMode>()
                : new System.Collections.Generic.Dictionary<int, SeasonalityRadiusMode>(overridesBySeriesId);
        }

        public async Task AnalyzeAsync(SeasonalityChartParameters parameters, CancellationToken cancellationToken = default)
        {
            AnalyzeCalls++;
            LastParameters = parameters;
            if (OnAnalyze is not null)
            {
                await OnAnalyze(parameters).ConfigureAwait(false);
            }

            Current = ResultToPublish;
            RaiseChanged();
        }
    }
}
