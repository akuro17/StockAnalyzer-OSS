using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>
/// Indicator Timeframe, Sprint 4 / T6: the Timeframe row of the Indicator Manager and of the Indicator Properties dialog (visibility, enabled state, tooltip,
/// the reset when an indicator joins a chain, and the candidates of the chain dropdowns). View models only; no UI is created.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class IndicatorTimeframeRowViewModelTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public IndicatorTimeframeRowViewModelTests() => LocalizationManager.Instance.Initialize("en");

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        LocalizationManager.Instance.Initialize(_originalLanguage);
    }

    private IndicatorSettingsDialogViewModel CreateManager(params CoreIndicatorSettings[] indicators)
    {
        var templates = new Mock<ITemplateService>();
        templates.Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator)).ReturnsAsync(new List<IndicatorTemplate>());
        templates.Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>())).ReturnsAsync(TemplateValidationResult.Success());
        var vm = new IndicatorSettingsDialogViewModel(
            new Mock<IDialogService>().Object,
            IndicatorFactory.Default,
            new Mock<IToastNotificationService>().Object,
            templates.Object,
            new Mock<IIndicatorUserDefaultService>().Object);
        _disposables.Add(vm);
        vm.Initialize(indicators);
        return vm;
    }

    private IndicatorPropertiesViewModel CreateProperties(CoreIndicatorSettings editing, IEnumerable<CoreIndicatorSettings> all)
    {
        var vm = new IndicatorPropertiesViewModel(editing, new StrongReferenceMessenger(), new SynchronousDispatcherService(), all);
        _disposables.Add(vm);
        return vm;
    }

    private static CoreIndicatorSettings Sma(string id, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart, int period = 20)
    {
        var settings = new CoreIndicatorSettings
        {
            Id = id,
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            Category = CoreIndicatorCategory.Trend,
            ParameterObject = new CoreSmaParameter { Period = period },
        };
        settings.UpdateDisplayName();
        settings.Timeframe = timeframe;
        return settings;
    }

    private static CoreIndicatorSettings Of(string id, IndicatorType type, CoreIndicatorParameterBase? parameter = null)
    {
        var settings = new CoreIndicatorSettings { Id = id, TypeEnum = type, IsEnabled = true, ParameterObject = parameter };
        settings.UpdateDisplayName();
        return settings;
    }

    // ---- visibility matrix -----------------------------------------------------------------------------------------

    [Fact]
    public void ShowTimeframe_FollowsThePolicy_ForTheSelectedIndicator()
    {
        var matrix = new (CoreIndicatorSettings Settings, bool Expected)[]
        {
            (Sma("sma"), true),
            (Of("obv", IndicatorType.OBV), true), // no parameter object
            (Of("pivot", IndicatorType.ClassicPivotPoints, new CoreClassicPivotPointsParameter()), false),
            (Of("pivotAdv", IndicatorType.PivotPointsAdvanced, new CorePivotPointsAdvancedParameter()), false),
            (Of("corr", IndicatorType.Correlation), false),
            (Of("renko", IndicatorType.Renko), false),
            (Of("zigzag", IndicatorType.ZigZag), false),
            (Of("price", IndicatorType.Price), false),
        };
        var manager = CreateManager(matrix.Select(m => m.Settings).ToArray());

        foreach (var (settings, expected) in matrix)
        {
            manager.SelectedIndicator = settings;
            Assert.Equal(expected, manager.ShowTimeframe);
            Assert.Equal(expected, CreateProperties(settings, matrix.Select(m => m.Settings)).ShowTimeframe);
        }

        manager.SelectedIndicator = null;
        Assert.False(manager.ShowTimeframe);
        Assert.Equal(new[] { IndicatorTimeframe.SameAsChart, IndicatorTimeframe.Weekly, IndicatorTimeframe.Monthly }, manager.TimeframeOptions.ToArray());
    }

    [Fact]
    public void TimeframeOptions_AreInScaleOrder_AndHaveLocalizedTexts_InEnglishAndJapanese()
    {
        Assert.Equal(new[] { IndicatorTimeframe.SameAsChart, IndicatorTimeframe.Weekly, IndicatorTimeframe.Monthly }, IndicatorTimeframeUiRules.Options.ToArray());

        LocalizationManager.Instance.Initialize("en");
        Assert.Equal(new[] { "Same as Chart", "Weekly", "Monthly" }, IndicatorTimeframeUiRules.Options.Select(o => ParameterViewBuilder.ResolveEnumDisplayText(o)).ToArray());
        LocalizationManager.Instance.Initialize("ja");
        // ja: "Same as Chart", "Weekly", "Monthly" (written as \u escapes: code files stay English-only)
        Assert.Equal(new[] { "\u30C1\u30E3\u30FC\u30C8\u3068\u540C\u3058", "\u9031\u8DB3", "\u6708\u8DB3" }, IndicatorTimeframeUiRules.Options.Select(o => ParameterViewBuilder.ResolveEnumDisplayText(o)).ToArray());
        Assert.NotEqual("[Tooltip_TimeframeDisabledChained]", LocalizationManager.Instance["Tooltip_TimeframeDisabledChained"]);
        LocalizationManager.Instance.Initialize("en");
        Assert.NotEqual("[Tooltip_TimeframeDisabledChained]", LocalizationManager.Instance["Tooltip_TimeframeDisabledChained"]);
    }

    // ---- enabled state and tooltip ---------------------------------------------------------------------------------

    [Fact]
    public void AFreeIndicator_IsEnabled_WithoutATooltip()
    {
        var free = Sma("free");
        var manager = CreateManager(free, Sma("other"));
        manager.SelectedIndicator = free;

        Assert.True(manager.IsTimeframeEnabled);
        Assert.Null(manager.TimeframeToolTip);
    }

    [Fact]
    public void AnIndicatorThatReadsAnother_AndTheOneItReads_AreDisabledWithATooltip()
    {
        var up = Sma("up");
        var down = Sma("down");
        down.SourceIndicatorId = "up";
        var manager = CreateManager(up, down);

        manager.SelectedIndicator = down;
        Assert.True(manager.ShowTimeframe);
        Assert.False(manager.IsTimeframeEnabled);
        Assert.Equal(LocalizationManager.Instance["Tooltip_TimeframeDisabledChained"], manager.TimeframeToolTip);

        manager.SelectedIndicator = up; // referenced by "down"
        Assert.False(manager.IsTimeframeEnabled);
        Assert.NotNull(manager.TimeframeToolTip);

        var driver = Sma("driver");
        var driven = Sma("driven");
        driven.DynamicPeriodIndicatorId = "driver";
        var propertiesOfDriver = CreateProperties(driver, new[] { driver, driven });
        Assert.False(propertiesOfDriver.IsTimeframeEnabled);
        Assert.NotNull(propertiesOfDriver.TimeframeToolTip);
    }

    [Fact]
    public void AnIndicatorWhereTheSettingDoesNotApply_HasNoTooltip()
    {
        var pivot = Of("pivot", IndicatorType.ClassicPivotPoints, new CoreClassicPivotPointsParameter());
        var manager = CreateManager(pivot);
        manager.SelectedIndicator = pivot;

        Assert.False(manager.ShowTimeframe);
        Assert.False(manager.IsTimeframeEnabled);
        Assert.Null(manager.TimeframeToolTip);
    }

    // ---- reset when an indicator joins a chain ---------------------------------------------------------------------

    [Fact]
    public void ChoosingASourceIndicator_ResetsTheTimeframe_AndDisablesTheRow_BeforeAnyNotification()
    {
        var up = Sma("up");
        var down = Sma("down", IndicatorTimeframe.Weekly);
        var manager = CreateManager(up, down);
        manager.SelectedIndicator = down;
        Assert.True(manager.IsTimeframeEnabled);

        var seenTimeframeAtNotification = new List<IndicatorTimeframe>();
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(manager.IsTimeframeEnabled))
            {
                seenTimeframeAtNotification.Add(down.Timeframe);
            }
        };

        manager.SelectedSourceIndicatorOption = manager.AvailableSourceIndicators.First(o => o.Id == "up");

        Assert.Equal(IndicatorTimeframe.SameAsChart, down.Timeframe);
        Assert.False(manager.IsTimeframeEnabled);
        Assert.NotEmpty(seenTimeframeAtNotification);
        Assert.All(seenTimeframeAtNotification, tf => Assert.Equal(IndicatorTimeframe.SameAsChart, tf));
    }

    [Fact]
    public void TurningOnTheDynamicPeriodDriver_ResetsTheTimeframe_AndDisablesTheRow()
    {
        var driver = Sma("driver");
        var driven = Sma("driven", IndicatorTimeframe.Monthly);
        var manager = CreateManager(driver, driven);
        manager.SelectedIndicator = driven;
        Assert.True(manager.SupportsDynamicPeriod);

        manager.HasDynamicPeriodDriver = true;

        Assert.True(manager.HasDynamicPeriodDriver);
        Assert.Equal(IndicatorTimeframe.SameAsChart, driven.Timeframe);
        Assert.False(manager.IsTimeframeEnabled);
        Assert.NotNull(manager.TimeframeToolTip);
    }

    [Fact]
    public void ChoosingADriverFromTheDropdown_ResetsTheTimeframe()
    {
        var driver = Sma("driver");
        var driven = Sma("driven", IndicatorTimeframe.Weekly);
        var manager = CreateManager(driver, driven);
        manager.SelectedIndicator = driven;

        manager.SelectedDynamicPeriodDriverOption = manager.AvailableDynamicPeriodDrivers.First(o => o.Id == "driver");

        Assert.Equal(IndicatorTimeframe.SameAsChart, driven.Timeframe);
        Assert.False(manager.IsTimeframeEnabled);
    }

    [Fact]
    public void PropertiesDialog_ChoosingASourceOrADriver_ResetsTheTimeframeOfTheEditedCopyOnly()
    {
        var up = Sma("up");
        var original = Sma("down", IndicatorTimeframe.Weekly);
        var all = new[] { up, original };

        var withSource = CreateProperties(original, all);
        withSource.SelectedSourceIndicatorOption = withSource.AvailableSourceIndicators.First(o => o.Id == "up");
        Assert.Equal(IndicatorTimeframe.SameAsChart, withSource.EditingSettings.Timeframe);
        Assert.False(withSource.IsTimeframeEnabled);
        Assert.Equal(IndicatorTimeframe.Weekly, original.Timeframe); // nothing is applied before OK

        var withDriver = CreateProperties(original, all);
        Assert.True(withDriver.IsTimeframeEnabled);
        withDriver.HasDynamicPeriodDriver = true;
        Assert.Equal(IndicatorTimeframe.SameAsChart, withDriver.EditingSettings.Timeframe);
        Assert.False(withDriver.IsTimeframeEnabled);

        withDriver.ResetCommand.Execute(null); // back to the state the dialog opened with
        Assert.Equal(IndicatorTimeframe.Weekly, withDriver.EditingSettings.Timeframe);
        Assert.True(withDriver.IsTimeframeEnabled);
    }

    // ---- chain candidates ------------------------------------------------------------------------------------------

    [Fact]
    public void AnIndicatorOnAnotherTimeframe_IsNotOfferedAsSourceOrDriver_UnlessItIsAlreadyChosen()
    {
        var weekly = Sma("weekly", IndicatorTimeframe.Weekly);
        var same = Sma("same");
        var consumer = Sma("consumer");
        var manager = CreateManager(weekly, same, consumer);

        manager.SelectedIndicator = consumer;

        Assert.DoesNotContain(manager.AvailableSourceIndicators, o => o.Id == "weekly");
        Assert.DoesNotContain(manager.AvailableDynamicPeriodDrivers, o => o.Id == "weekly");
        Assert.Contains(manager.AvailableSourceIndicators, o => o.Id == "same");
        Assert.Contains(manager.AvailableDynamicPeriodDrivers, o => o.Id == "same");

        // A reference that is already in place stays selectable so the selection never disappears.
        consumer.SourceIndicatorId = "weekly";
        manager.UpdateReferenceOptions();
        Assert.Contains(manager.AvailableSourceIndicators, o => o.Id == "weekly");
    }

    [Fact]
    public void TheTimeframeRowNotifiesWhenTheSelectionChanges()
    {
        var a = Sma("a");
        var b = Of("pivot", IndicatorType.ClassicPivotPoints, new CoreClassicPivotPointsParameter());
        var manager = CreateManager(a, b);
        manager.SelectedIndicator = a;
        var changed = new List<string?>();
        manager.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        manager.SelectedIndicator = b;

        Assert.Contains(nameof(manager.ShowTimeframe), changed);
        Assert.Contains(nameof(manager.IsTimeframeEnabled), changed);
        Assert.Contains(nameof(manager.TimeframeToolTip), changed);
    }

    // ---- template application (U-8: an IndicatorTemplate keeps the Timeframe, applying it heals through AutoHeal) -----------

    private static IndicatorTemplate TemplateOf(params CoreIndicatorSettings[] indicators)
    {
        var template = new IndicatorTemplate { Id = Guid.NewGuid(), Name = "Tpl" };
        template.SetIndicators(indicators);
        return template;
    }

    [Fact]
    public async System.Threading.Tasks.Task LoadTemplate_KeepsAFreeIndicatorsTimeframe_AndResetsOneThatReadsAnotherIndicator()
    {
        var manager = CreateManager();
        var free = Sma("free", IndicatorTimeframe.Weekly);
        var chained = Sma("chained", IndicatorTimeframe.Monthly);
        chained.SourceIndicatorId = "free";
        var template = TemplateOf(free, chained);

        await manager.LoadTemplateCommand.ExecuteAsync(template);

        // Clone() gives every applied indicator a new Id, so the clones are told apart by what the template put into them.
        Assert.Equal(2, manager.Indicators.Count);
        var appliedFree = manager.Indicators.Single(i => string.IsNullOrEmpty(i.SourceIndicatorId));
        var appliedChained = manager.Indicators.Single(i => !string.IsNullOrEmpty(i.SourceIndicatorId));
        Assert.Equal(IndicatorTimeframe.Weekly, appliedFree.Timeframe);
        Assert.Equal(IndicatorTimeframe.SameAsChart, appliedChained.Timeframe);
        Assert.DoesNotContain("[MN1]", appliedChained.DisplayName);
        Assert.Equal(IndicatorTimeframe.Monthly, chained.Timeframe); // the template itself is not modified: only the applied clones are healed
    }

    [Fact]
    public async System.Threading.Tasks.Task AppendTemplate_ResetsTheTimeframeOfAnIndicatorWithADynamicPeriodDriver()
    {
        var manager = CreateManager();
        var driven = Sma("driven", IndicatorTimeframe.Weekly);
        driven.DynamicPeriodIndicatorId = "driver";

        await manager.AppendTemplateCommand.ExecuteAsync(TemplateOf(driven));

        var applied = Assert.Single(manager.Indicators);
        Assert.Equal(IndicatorTimeframe.SameAsChart, applied.Timeframe);
        Assert.Equal(IndicatorTimeframe.Weekly, driven.Timeframe);
    }
}
