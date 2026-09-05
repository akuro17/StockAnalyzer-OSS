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
    private static IndicatorSettingsDialogViewModel CreateViewModel(IIndicatorUserDefaultService? userDefaultService = null)
    {
        var mockDialogService = new Mock<IDialogService>();
        var mockToastService = new Mock<IToastNotificationService>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService
            .Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator))
            .ReturnsAsync(new List<IndicatorTemplate>());

        var mockUserDefaultService = userDefaultService ?? new Mock<IIndicatorUserDefaultService>().Object;

        return new IndicatorSettingsDialogViewModel(
            mockDialogService.Object,
            IndicatorFactory.Default,
            mockToastService.Object,
            mockTemplateService.Object,
            mockUserDefaultService);
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
    public void HiddenParameterTags_StaysEmpty_WhenDynamicPeriodDriverIsActive()
    {
        // Period remains editable even when a Dynamic Period Driver is assigned: the static value
        // is still used as AdaptiveSmoothingHelper.CalculateAdaptiveSma's defaultPeriod fallback for
        // bars where the driver has no value yet, so it must stay visible/editable, not hidden.
        // Also covers the Library tab, which binds the same VM-level HiddenParameterTags property.
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 14), CreateSmaSettings("b", 20) });

        vm.HasDynamicPeriodDriver = true;

        Assert.True(vm.HasDynamicPeriodDriver);
        Assert.Empty(vm.HiddenParameterTags);
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

    [Fact]
    public void SelectedCatalogItem_WhenSelected_ClonesDefaultSettingsAndUpdatesPreviewName()
    {
        var vm = CreateViewModel();
        Assert.NotEmpty(vm.FilteredCatalogItems);

        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        Assert.NotNull(vm.SelectedLibraryIndicatorSettings);
        Assert.Equal(IndicatorType.SMA, vm.SelectedLibraryIndicatorSettings.TypeEnum);
        Assert.False(vm.UseShortName);
        Assert.Equal("Simple Moving Average (20)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void UseShortName_Toggle_UpdatesPreviewNameBetweenLongAndShort()
    {
        var vm = CreateViewModel();
        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        Assert.Equal("Simple Moving Average (20)", vm.LibraryIndicatorPreviewName);

        vm.UseShortName = true;
        Assert.Equal("SMA(20)", vm.LibraryIndicatorPreviewName);

        vm.UseShortName = false;
        Assert.Equal("Simple Moving Average (20)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void ParameterChange_InLibraryMode_UpdatesPreviewNameDynamically()
    {
        var vm = CreateViewModel();
        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        var smaParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        smaParam.Period = 25;

        Assert.Equal("Simple Moving Average (25)", vm.LibraryIndicatorPreviewName);

        vm.UseShortName = true;
        Assert.Equal("SMA(25)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void AddSelectedLibraryIndicatorCommand_AddsConfiguredIndicatorToIndicatorsList()
    {
        var vm = CreateViewModel();
        vm.Initialize(new List<CoreIndicatorSettings>());

        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        var smaParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        smaParam.Period = 50;
        vm.UseShortName = true;

        vm.AddSelectedLibraryIndicatorCommand.Execute(null);

        Assert.Single(vm.Indicators);
        var added = vm.Indicators[0];
        Assert.Equal("SMA(50)", added.DisplayName);
        Assert.True(added.IsEnabled);
        var addedParam = Assert.IsType<CoreSmaParameter>(added.ParameterObject);
        Assert.Equal(50, addedParam.Period);
    }

    [Fact]
    public void ResetLibraryIndicatorSettingsCommand_RestoresFactoryDefaults()
    {
        var vm = CreateViewModel();
        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        var smaParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        smaParam.Period = 99;
        Assert.Equal("Simple Moving Average (99)", vm.LibraryIndicatorPreviewName);

        vm.ResetLibraryIndicatorSettingsCommand.Execute(null);

        var restoredParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings.ParameterObject);
        Assert.Equal(20, restoredParam.Period);
        Assert.Equal("Simple Moving Average (20)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public async Task SetAsDefaultCommand_PersistsUserDefaultAndIsLoadedAsBaseline()
    {
        var mockService = new Mock<IIndicatorUserDefaultService>();
        CoreIndicatorSettings? savedSettings = null;
        mockService
            .Setup(s => s.SaveUserDefaultAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s => savedSettings = s)
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel(mockService.Object);
        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        var smaParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        smaParam.Period = 35;
        vm.UseShortName = true;

        await vm.SetAsDefaultCommand.ExecuteAsync(null);

        mockService.Verify(s => s.SaveUserDefaultAsync(It.IsAny<CoreIndicatorSettings>()), Times.Once);
        Assert.NotNull(savedSettings);
        Assert.Equal(IndicatorType.SMA, savedSettings.TypeEnum);
        Assert.Equal("SMA(35)", savedSettings.DisplayName);
        var savedParam = Assert.IsType<CoreSmaParameter>(savedSettings.ParameterObject);
        Assert.Equal(35, savedParam.Period);
    }

    [Fact]
    public async Task ResetCommand_InActiveMode_RestoresSystemDefault()
    {
        var vm = CreateViewModel();
        vm.Initialize(new[] { CreateSmaSettings("a", 99) });

        Assert.Equal("SMA(99)", vm.Indicators[0].ShortDisplayName);
        vm.SelectedIndicator = vm.Indicators[0];

        await vm.ResetCommand.ExecuteAsync(null);

        var activeParam = Assert.IsType<CoreSmaParameter>(vm.Indicators[0].ParameterObject);
        Assert.Equal(20, activeParam.Period);
        Assert.Equal("SMA(20)", vm.Indicators[0].ShortDisplayName);
    }

    [Fact]
    public async Task ResetCommand_InLibraryMode_ClearsUserDefaultAndRestoresSystemDefault()
    {
        var mockService = new Mock<IIndicatorUserDefaultService>();
        var userDefaults = new Dictionary<IndicatorType, CoreIndicatorSettings>
        {
            [IndicatorType.SMA] = new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SMA,
                DisplayName = "SMA(42)",
                ParameterObject = new CoreSmaParameter { Period = 42 }
            }
        };
        mockService.Setup(s => s.LoadUserDefaults()).Returns(userDefaults);
        mockService.Setup(s => s.ResetToSystemDefaultAsync(IndicatorType.SMA)).Returns(Task.CompletedTask);

        var vm = CreateViewModel(mockService.Object);
        vm.IsLibraryMode = true;

        var smaItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCatalogItem = smaItem;

        var loadedParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        Assert.Equal(42, loadedParam.Period);

        await vm.ResetCommand.ExecuteAsync(null);

        mockService.Verify(s => s.ResetToSystemDefaultAsync(IndicatorType.SMA), Times.Once);
        var restoredParam = Assert.IsType<CoreSmaParameter>(vm.SelectedLibraryIndicatorSettings.ParameterObject);
        Assert.Equal(20, restoredParam.Period);
        Assert.Equal("Simple Moving Average (20)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void ClearCategoryFilterCommand_AfterSelectTemplates_RestoresAllCategoriesAndResetsTemplateCategory()
    {
        var vm = CreateViewModel();
        vm.IsLibraryMode = true;

        // Select templates
        vm.SelectTemplatesCommand.Execute(null);
        Assert.True(vm.IsTemplatesSelected);
        Assert.True(vm.IsTemplatesCategory);
        Assert.False(vm.IsNotTemplatesCategory);

        // Click "All Categories"
        vm.ClearCategoryFilterCommand.Execute(null);
        Assert.False(vm.IsTemplatesSelected);
        Assert.False(vm.IsTemplatesCategory);
        Assert.True(vm.IsNotTemplatesCategory);
        Assert.Null(vm.SelectedCategory);
        Assert.Equal(vm.AllCatalogItems.Count, vm.FilteredCatalogItems.Count);
    }

    [Fact]
    public void SelectedLibraryIndicatorSettings_StochasticParameterChanges_UpdatesPreviewNameDynamically()
    {
        var vm = CreateViewModel();
        vm.IsLibraryMode = true;

        var stochItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.Stoch);
        vm.SelectedCatalogItem = stochItem;

        var stochParam = Assert.IsType<CoreStochasticParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        Assert.Equal("Stochastic Oscillator (14, 3, 3)", vm.LibraryIndicatorPreviewName);

        stochParam.KPeriod = 21;
        Assert.Equal("Stochastic Oscillator (21, 3, 3)", vm.LibraryIndicatorPreviewName);

        stochParam.DPeriod = 5;
        Assert.Equal("Stochastic Oscillator (21, 5, 3)", vm.LibraryIndicatorPreviewName);

        stochParam.Smooth = 4;
        Assert.Equal("Stochastic Oscillator (21, 5, 4)", vm.LibraryIndicatorPreviewName);

        vm.UseShortName = true;
        Assert.Equal("Stoch(21, 5, 4)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void SelectedLibraryIndicatorSettings_IchimokuParameterChanges_UpdatesPreviewNameDynamically()
    {
        var vm = CreateViewModel();
        vm.IsLibraryMode = true;

        var ichimokuItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.Ichimoku);
        vm.SelectedCatalogItem = ichimokuItem;

        var ichimokuParam = Assert.IsType<CoreIchimokuParameter>(vm.SelectedLibraryIndicatorSettings!.ParameterObject);
        Assert.Equal("Ichimoku Kinko Hyo (9, 26)", vm.LibraryIndicatorPreviewName);

        ichimokuParam.TenkanSample = 7;
        Assert.Equal("Ichimoku Kinko Hyo (7, 26)", vm.LibraryIndicatorPreviewName);

        ichimokuParam.KijunSample = 22;
        Assert.Equal("Ichimoku Kinko Hyo (7, 22)", vm.LibraryIndicatorPreviewName);
    }

    [Fact]
    public void SelectPrice_Populates15PriceCatalogItems_InOrder()
    {
        var vm = CreateViewModel();
        vm.IsLibraryMode = true;

        vm.SelectPriceCommand.Execute(null);

        Assert.True(vm.IsPriceCategory);
        Assert.False(vm.IsTemplatesCategory);
        Assert.Null(vm.SelectedCategory);
        Assert.Equal(15, vm.FilteredCatalogItems.Count);

        for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            var expectedType = PriceDataHelper.PriceTypeOptions[i];
            Assert.Equal(expectedType, vm.FilteredCatalogItems[i].PriceType);
            Assert.Equal(IndicatorType.Price, vm.FilteredCatalogItems[i].Type);
            Assert.Equal(PriceDataHelper.FormatPriceTypeLabel(expectedType), vm.FilteredCatalogItems[i].DisplayName);
        }
    }

    [Fact]
    public void AddSelectedLibraryIndicator_WhenPriceSelected_AddsPriceOverlayToIndicators()
    {
        var vm = CreateViewModel();
        vm.IsLibraryMode = true;

        vm.SelectPriceCommand.Execute(null);

        // Select Median (H+L)/2
        var medianItem = vm.FilteredCatalogItems.First(i => i.PriceType == PriceType.Median);
        vm.SelectedCatalogItem = medianItem;

        Assert.Equal("Median (H+L)/2", vm.LibraryIndicatorPreviewName);
        Assert.NotNull(vm.SelectedLibraryIndicatorSettings);
        Assert.Equal(PriceType.Median, vm.SelectedLibraryIndicatorSettings.PriceSource);
        Assert.Equal(IndicatorType.Price, vm.SelectedLibraryIndicatorSettings.TypeEnum);

        vm.AddSelectedLibraryIndicatorCommand.Execute(null);

        Assert.Single(vm.Indicators);
        var added = vm.Indicators[0];
        Assert.Equal(IndicatorType.Price, added.TypeEnum);
        Assert.Equal(PriceType.Median, added.PriceSource);
        Assert.Equal("Median (H+L)/2", added.DisplayName);
        Assert.True(added.IsEnabled);
    }

    [Fact]
    public void Initialize_WithPriceIndicatorGenericName_HealsDisplayNameToPriceSourceLabel()
    {
        var vm = CreateViewModel();
        var existing = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.Price,
            PriceSource = PriceType.HeikinAshiClose,
            DisplayName = "Price" // Generic legacy name
        };

        vm.Initialize(new[] { existing });

        Assert.Single(vm.Indicators);
        Assert.Equal("Heikin-Ashi Close", vm.Indicators[0].DisplayName);
    }
}
