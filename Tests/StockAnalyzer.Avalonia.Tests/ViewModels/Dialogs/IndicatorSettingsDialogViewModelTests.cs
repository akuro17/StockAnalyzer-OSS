using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>
/// Coverage for HasDynamicPeriodDriver (DynamicPeriodParameterVisibility feature): its ON/OFF
/// round-trip sync with CoreIndicatorSettings.DynamicPeriodIndicatorId, and the regression fixed
/// this session where switching SelectedIndicator away and back reset the driver to OFF because
/// UpdateReferenceOptions() clearing AvailableDynamicPeriodDrivers transiently nulled the bound
/// ComboBox selection back into the model.
/// </summary>
public class IndicatorSettingsDialogViewModelTests
{
    private static IndicatorSettingsDialogViewModel CreateViewModel()
    {
        var mockDialogService = new Mock<IDialogService>();
        var mockToastService = new Mock<IToastNotificationService>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService
            .Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator))
            .ReturnsAsync(new List<IndicatorTemplate>());

        return new IndicatorSettingsDialogViewModel(
            mockDialogService.Object,
            IndicatorFactory.Default,
            mockToastService.Object,
            mockTemplateService.Object);
    }

    private static CoreIndicatorSettings CreateSmaSettings(string id, int period)
    {
        return new CoreIndicatorSettings
        {
            Id = id,
            DisplayName = $"SMA({period})",
            TypeEnum = IndicatorType.SMA,
            Category = CoreIndicatorCategory.Trend,
            IsEnabled = true,
            ParameterObject = new CoreSmaParameter { Period = period }
        };
    }

    [Fact]
    public void HasDynamicPeriodDriver_TurningOn_SelectsFirstAvailableDriver_AndSyncsBackingId()
    {
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 14), CreateSmaSettings("b", 20) });

        Assert.False(vm.HasDynamicPeriodDriver);
        Assert.True(string.IsNullOrEmpty(vm.SelectedIndicator!.DynamicPeriodIndicatorId));

        vm.HasDynamicPeriodDriver = true;

        Assert.True(vm.HasDynamicPeriodDriver);
        Assert.Equal("b", vm.SelectedIndicator!.DynamicPeriodIndicatorId);
    }

    [Fact]
    public void HasDynamicPeriodDriver_TurningOff_ClearsBackingId()
    {
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 14), CreateSmaSettings("b", 20) });
        vm.HasDynamicPeriodDriver = true;

        vm.HasDynamicPeriodDriver = false;

        Assert.False(vm.HasDynamicPeriodDriver);
        Assert.True(string.IsNullOrEmpty(vm.SelectedIndicator!.DynamicPeriodIndicatorId));
    }

    [Fact]
    public void SwitchingSelectedIndicatorAwayAndBack_PreservesDynamicPeriodDriver()
    {
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 14), CreateSmaSettings("b", 20) });

        var indicatorA = vm.Indicators[0];
        var indicatorB = vm.Indicators[1];

        vm.SelectedIndicator = indicatorA;
        vm.HasDynamicPeriodDriver = true;
        var driverIdAfterEnable = indicatorA.DynamicPeriodIndicatorId;
        Assert.False(string.IsNullOrEmpty(driverIdAfterEnable));

        // Regression for the "switch away and back resets to OFF" bug: UpdateReferenceOptions()
        // clears/rebuilds AvailableDynamicPeriodDrivers on every SelectedIndicator change, which
        // used to transiently null-out the currently selected indicator's DynamicPeriodIndicatorId
        // via the TwoWay-bound SelectedDynamicPeriodDriverOption setter.
        vm.SelectedIndicator = indicatorB;
        vm.SelectedIndicator = indicatorA;

        Assert.True(vm.HasDynamicPeriodDriver);
        Assert.Equal(driverIdAfterEnable, indicatorA.DynamicPeriodIndicatorId);
    }

    [Fact]
    public void HasDynamicPeriodDriver_TurningOnWithNoAvailableDrivers_StaysOff_AndNotifiesPropertyChanged()
    {
        // Regression for the constraint-check finding M-1a: with a single indicator loaded,
        // AvailableDynamicPeriodDrivers contains only the "(None)" sentinel, so there is no
        // candidate driver. The setter's early-return branch must still raise PropertyChanged
        // so a bound CheckBox re-reads the getter (false) instead of staying visually checked.
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 14) });

        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.HasDynamicPeriodDriver = true;

        Assert.False(vm.HasDynamicPeriodDriver);
        Assert.True(string.IsNullOrEmpty(vm.SelectedIndicator!.DynamicPeriodIndicatorId));
        Assert.Contains(nameof(IndicatorSettingsDialogViewModel.HasDynamicPeriodDriver), raisedProperties);
    }

    [Fact]
    public void SupportsDynamicPeriod_SinglePeriodIndicator_ReturnsTrue()
    {
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("sma", 20) });

        Assert.True(vm.SupportsDynamicPeriod);
    }

    [Fact]
    public void SupportsDynamicPeriod_MultiPeriodIndicator_ReturnsTrue()
    {
        var vm = CreateViewModel();
        var ichimokuSettings = new CoreIndicatorSettings
        {
            Id = "ichi",
            DisplayName = "Ichimoku",
            TypeEnum = IndicatorType.Ichimoku,
            Category = CoreIndicatorCategory.Trend,
            IsEnabled = true,
            ParameterObject = new CoreIchimokuParameter()
        };
        var macdSettings = new CoreIndicatorSettings
        {
            Id = "macd",
            DisplayName = "MACD",
            TypeEnum = IndicatorType.MACD,
            Category = CoreIndicatorCategory.Oscillator,
            IsEnabled = true,
            ParameterObject = new CoreMacdParameter()
        };

        vm.Initialize(new[] { ichimokuSettings, macdSettings });

        vm.SelectedIndicator = ichimokuSettings;
        Assert.True(vm.SupportsDynamicPeriod);

        vm.SelectedIndicator = macdSettings;
        Assert.True(vm.SupportsDynamicPeriod);
    }

    [Fact]
    public void SupportsDynamicPeriod_NonPeriodIndicatorOrNull_ReturnsFalse()
    {
        var vm = CreateViewModel();
        var avwapSettings = new CoreIndicatorSettings
        {
            Id = "avwap",
            DisplayName = "Anchored VWAP",
            TypeEnum = IndicatorType.AnchoredVWAP,
            Category = CoreIndicatorCategory.Other,
            IsEnabled = true,
            ParameterObject = new CoreAnchoredVwapParameter()
        };

        vm.Initialize(new[] { avwapSettings });
        vm.SelectedIndicator = avwapSettings;
        Assert.False(vm.SupportsDynamicPeriod);

        vm.SelectedIndicator = null;
        Assert.False(vm.SupportsDynamicPeriod);
    }

    [Fact]
    public void PriceTypeOptions_MatchesPriceDataHelper_WithCloseDefault()
    {
        var vm = CreateViewModel();
        Assert.Equal(PriceDataHelper.PriceTypeOptions, vm.PriceTypeOptions);
        Assert.Equal(PriceType.Close, vm.PriceTypeOptions[3]); // Close below Low
    }
}
