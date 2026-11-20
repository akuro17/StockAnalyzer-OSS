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
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>
/// The Dot Size row (right of Line Thickness in the Indicator Manager, a row of the Indicator Properties dialog): shown for the four block chart overlays only, and a value edited there reaches the setting
/// the chart draws from. View models only; no UI is created.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class BlockChartOverlayDotSizeRowViewModelTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public BlockChartOverlayDotSizeRowViewModelTests() => LocalizationManager.Instance.Initialize("en");

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
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

    private IndicatorPropertiesViewModel CreateProperties(CoreIndicatorSettings editing)
    {
        var vm = new IndicatorPropertiesViewModel(editing, new StrongReferenceMessenger(), new SynchronousDispatcherService(), new[] { editing });
        _disposables.Add(vm);
        return vm;
    }

    private static CoreIndicatorSettings Default(IndicatorType type) => DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type);

    public static IEnumerable<object[]> OverlayTypes() => BlockChartOverlayTypes.All.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(OverlayTypes))]
    public void TheRowIsShownForTheFourOverlays_InBothDialogs(IndicatorType type)
    {
        var setting = Default(type);

        var manager = CreateManager(setting);
        manager.SelectedIndicator = setting;
        Assert.True(manager.ShowDotSize);

        Assert.True(CreateProperties(Default(type)).ShowDotSize);
    }

    [Fact]
    public void TheRowIsHiddenForEveryOtherIndicator_AndWhenNothingIsSelected()
    {
        var sma = Default(IndicatorType.SMA);
        var psar = Default(IndicatorType.ParabolicSAR);

        var manager = CreateManager(sma, psar);
        manager.SelectedIndicator = null;
        Assert.False(manager.ShowDotSize);
        manager.SelectedIndicator = sma;
        Assert.False(manager.ShowDotSize);
        manager.SelectedIndicator = psar;   // draws dots too, but from its own thickness rule: not this setting
        Assert.False(manager.ShowDotSize);

        Assert.False(CreateProperties(Default(IndicatorType.SMA)).ShowDotSize);
        Assert.False(CreateProperties(Default(IndicatorType.ParabolicSAR)).ShowDotSize);
    }

    [Fact]
    public void TheManagerNotifiesTheRow_WhenTheSelectionMovesBetweenAnOverlayAndAnotherIndicator()
    {
        var overlay = Default(IndicatorType.Renko);
        var sma = Default(IndicatorType.SMA);
        var manager = CreateManager(overlay, sma);
        var notified = new List<string?>();
        manager.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        manager.SelectedIndicator = overlay;
        manager.SelectedIndicator = sma;

        Assert.Contains(nameof(IndicatorSettingsDialogViewModel.ShowDotSize), notified);
    }

    [Fact]
    public void AValueEditedInThePropertiesDialog_IsTheValueOfTheEditedSetting_AndAClonedSettingKeepsIt()
    {
        var setting = Default(IndicatorType.Kagi);
        var vm = CreateProperties(setting);

        vm.EditingSettings.DotSize = 11.5;

        Assert.Equal(11.5, vm.EditingSettings.DotSize);
        Assert.Equal(11.5, vm.EditingSettings.Clone().DotSize);
        Assert.Equal(11.5, vm.EditingSettings.Snapshot().DotSize);
    }
}
