using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DynamicPeriodDriverIntegrationTests
{
    [Fact]
    public void PopulateReferenceOptions_OrdersDefaultThenActiveThenRegisteredDrivers()
    {
        // Arrange
        var activeChartIndicators = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Chart_SMA",
                DisplayName = "SMA (20)",
                TypeEnum = IndicatorType.SMA,
                IsEnabled = true,
                IsOverlay = true
            }
        };

        var registeredDrivers = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Driver_HT",
                DisplayName = "Dominant Cycle",
                TypeEnum = IndicatorType.HilbertTransform,
                IsOverlay = false
            }
        };

        var sources = new ObservableCollection<IndicatorReferenceOption>();
        var drivers = new ObservableCollection<IndicatorReferenceOption>();

        // Act
        IndicatorReferenceHelper.PopulateReferenceOptions(
            sources,
            drivers,
            allIndicators: activeChartIndicators,
            currentIndicatorId: "Target_EMA",
            selectedSourceId: null,
            selectedDriverId: null,
            registeredSourceIndicators: null,
            registeredDrivers: registeredDrivers);

        // Assert: Dynamic period drivers collection structure
        Assert.Equal(3, drivers.Count);
        // [0] None/Static
        Assert.Null(drivers[0].Id);
        // [1] Upper tier: Active chart indicator
        Assert.Equal("Chart_SMA", drivers[1].Id);
        Assert.StartsWith("SMA (20)", drivers[1].DisplayName);
        // [2] Lower tier: Registered dynamic period driver
        Assert.Equal("Driver_HT", drivers[2].Id);
        Assert.StartsWith("Dominant Cycle", drivers[2].DisplayName);
    }

    [Fact]
    public void PopulateReferenceOptions_WithNullRegisteredDrivers_MaintainsBackwardsCompatibility()
    {
        // Arrange
        var registeredSources = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Source_RSI",
                DisplayName = "RSI (14)",
                TypeEnum = IndicatorType.RSI
            }
        };

        var sources = new ObservableCollection<IndicatorReferenceOption>();
        var drivers = new ObservableCollection<IndicatorReferenceOption>();

        // Act: Call without registeredDrivers argument
        IndicatorReferenceHelper.PopulateReferenceOptions(
            sources,
            drivers,
            allIndicators: null,
            currentIndicatorId: "Target_EMA",
            selectedSourceId: null,
            selectedDriverId: null,
            registeredSourceIndicators: registeredSources);

        // Assert: Backwards compatibility populates registeredSources into drivers
        Assert.Equal(2, drivers.Count);
        Assert.Equal("Source_RSI", drivers[1].Id);
    }

    [Fact]
    public void AnalysisPipelineService_WithRegisteredDriver_ModulatesIndicatorOutput()
    {
        // Arrange
        var candles = new List<CoreCandleData>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 60; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(i * 0.2) * 20m;
            candles.Add(new CoreCandleData(
                now.AddDays(i),
                price,
                price + 2m,
                price - 2m,
                price,
                1000L));
        }

        var driverSetting = new CoreIndicatorSettings
        {
            Id = "Registered_Driver_HT",
            DisplayName = "HT Dominant Cycle",
            TypeEnum = IndicatorType.HilbertTransform,
            ParameterObject = new CoreHilbertTransformParameter()
        };

        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriver("Registered_Driver_HT"))
            .Returns(driverSetting);

        var pipeline = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default,
            sourceIndicatorService: null,
            logger: null,
            dynamicPeriodDriverService: mockDriverService.Object);

        // Indicator without dynamic driver (Static 20)
        var staticEma = new CoreIndicatorSettings
        {
            Id = "EMA_Static",
            DisplayName = "Static EMA",
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreEmaParameter { Period = 20 }
        };

        // Indicator with registered dynamic driver
        var dynamicEma = new CoreIndicatorSettings
        {
            Id = "EMA_Dynamic",
            DisplayName = "Dynamic EMA",
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreEmaParameter { Period = 20 },
            DynamicPeriodIndicatorId = "Registered_Driver_HT"
        };

        // Act
        var results = pipeline.CalculateIndicators(candles, new[] { staticEma, dynamicEma });

        // Assert
        Assert.True(results["EMA_Static"].IsSuccessful);
        Assert.True(results["EMA_Dynamic"].IsSuccessful);

        var staticValues = results["EMA_Static"].MainValues;
        var dynamicValues = results["EMA_Dynamic"].MainValues;

        // Verify that dynamic driver actually modulated the values (not identical to static)
        bool valuesDiffer = false;
        for (int i = 20; i < candles.Count; i++)
        {
            if (staticValues[i].HasValue && dynamicValues[i].HasValue &&
                Math.Abs(staticValues[i]!.Value - dynamicValues[i]!.Value) > 0.001m)
            {
                valuesDiffer = true;
                break;
            }
        }
        Assert.True(valuesDiffer, "Dynamic period driver must modulate calculation output values differently from static period.");
    }

    [Fact]
    public void AnalysisPipelineService_IchimokuChikouSpanDriver_AppliesCausalityGuardFallback()
    {
        // Arrange
        var candles = new List<CoreCandleData>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 50; i++)
        {
            candles.Add(new CoreCandleData(
                now.AddDays(i),
                100m + i,
                105m + i,
                95m + i,
                100m + i,
                1000L));
        }

        var ichimokuSetting = new CoreIndicatorSettings
        {
            Id = "Driver_Ichimoku",
            DisplayName = "Ichimoku",
            TypeEnum = IndicatorType.Ichimoku,
            OutputSeriesName = "Chikou", // Non-causal future series
            ParameterObject = new CoreIchimokuParameter()
        };

        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriver("Driver_Ichimoku"))
            .Returns(ichimokuSetting);

        var pipeline = new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: IndicatorFactory.Default,
            sourceIndicatorService: null,
            logger: null,
            dynamicPeriodDriverService: mockDriverService.Object);

        var drivenEma = new CoreIndicatorSettings
        {
            Id = "EMA_DrivenByChikou",
            DisplayName = "Driven EMA",
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreEmaParameter { Period = 10 },
            DynamicPeriodIndicatorId = "Driver_Ichimoku"
        };

        // Act
        var results = pipeline.CalculateIndicators(candles, new[] { drivenEma });

        // Assert: Calculations should succeed without null crash on recent 25 bars
        Assert.True(results["EMA_DrivenByChikou"].IsSuccessful);
        var drivenValues = results["EMA_DrivenByChikou"].MainValues;
        // Last bar should be valid (not null caused by lookahead failure)
        Assert.NotNull(drivenValues[^1]);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_DisplayNameFormatting_IncludesPeriodInLongName()
    {
        // Arrange
        var savedDrivers = new List<CoreIndicatorSettings>();
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(savedDrivers);
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s => savedDrivers.Add(s.Snapshot()))
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Switch to Catalog mode and select SMA
        vm.ClearCategoryFilterCommand.Execute(null);
        var smaItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.SMA);
        Assert.NotNull(smaItem);
        vm.SelectedCatalogItem = smaItem;

        Assert.NotNull(vm.EditingSettings);
        Assert.Equal(IndicatorType.SMA, vm.EditingSettings.TypeEnum);

        var smaParam = vm.EditingSettings.ParameterObject as CoreSmaParameter;
        Assert.NotNull(smaParam);
        smaParam.Period = 25;

        // Act 1: UseShortName is false (Long name)
        vm.UseShortName = false;

        // Assert 1: PreviewName and EditingSettings.DisplayName have period
        Assert.Equal("Simple Moving Average (25)", vm.PreviewName);
        Assert.Equal("Simple Moving Average (25)", vm.EditingSettings.DisplayName);

        // Register it
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);

        // Assert registered item in service and list has period in DisplayName
        Assert.Single(savedDrivers);
        Assert.Equal("Simple Moving Average (25)", savedDrivers[0].DisplayName);
        Assert.Single(vm.RegisteredIndicators);
        Assert.Equal("Simple Moving Average (25)", vm.RegisteredIndicators[0].DisplayName);

        // Act 2: UseShortName is true (Short name)
        vm.UseShortName = true;
        Assert.Equal("SMA(25)", vm.PreviewName);
        Assert.Equal("SMA(25)", vm.EditingSettings.DisplayName);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_CatalogSelect_IndicatorWithoutStaticDefault_StillSeedsParameterObject()
    {
        // Arrange: CMO has no DefaultCoreIndicatorSettings entry (see IndicatorDefaultParameterFallbackTests),
        // so OnSelectedCatalogItemChanged must fall back to GetDefaultSettings() the same way
        // SourceIndicatorRegistrationViewModel does, instead of leaving ParameterObject null.
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Act: select CMO from the catalog
        vm.ClearCategoryFilterCommand.Execute(null);
        var cmoItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.CMO);
        Assert.NotNull(cmoItem);
        vm.SelectedCatalogItem = cmoItem;

        // Assert: ParameterObject is seeded (Period editable), matching Source Indicator/Screener behavior
        Assert.NotNull(vm.EditingSettings);
        var cmoParam = Assert.IsType<CoreSmaParameter>(vm.EditingSettings!.ParameterObject);
        Assert.Equal(14, cmoParam.Period);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_RegisterThenReselectIndicatorWithoutStaticDefault_KeepsParameterObject()
    {
        // Arrange: end-to-end round trip for an indicator type without a DefaultCoreIndicatorSettings
        // entry (CMO) - register it as a driver, then reload the window (as a fresh VM, like reopening
        // the dialog) and re-select it from the persisted RegisteredIndicators list. Verifies the
        // catalog-select fix (OnSelectedCatalogItemChanged) actually produces a ParameterObject that
        // survives persistence/round-trip, not just the in-memory EditingSettings at selection time.
        var savedDrivers = new List<CoreIndicatorSettings>();
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(() => savedDrivers.Select(s => s.Snapshot()).ToList());
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s => savedDrivers.Add(s.Snapshot()))
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        vm.ClearCategoryFilterCommand.Execute(null);
        var cmoItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.CMO);
        Assert.NotNull(cmoItem);
        vm.SelectedCatalogItem = cmoItem;
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);
        Assert.Single(savedDrivers);
        Assert.NotNull(savedDrivers[0].ParameterObject);

        // Act: simulate reopening the dialog (fresh VM reloads from the persisted service) and
        // re-selecting the already-registered CMO driver.
        var vm2 = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm2.InitializationTask;

        var registeredCmo = vm2.RegisteredIndicators.FirstOrDefault(i => i.TypeEnum == IndicatorType.CMO);
        Assert.NotNull(registeredCmo);
        vm2.SelectedRegisteredIndicator = registeredCmo;

        // Assert: Period is still editable after the full register -> persist -> reload -> reselect cycle.
        Assert.NotNull(vm2.EditingSettings);
        var cmoParam = Assert.IsType<CoreSmaParameter>(vm2.EditingSettings!.ParameterObject);
        Assert.Equal(14, cmoParam.Period);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_ChangingPriceTypeOnSelectedIndicator_PersistsToRegisteredIndicator()
    {
        // Arrange: register a non-Price indicator (SMA), then select it from "Selected" mode and
        // change its Price Type (EditingSettings.PriceSource) via the newly-added Price Type selector.
        // This window has no visible "Add Indicator" button in Selected mode (Catalog-mode only), so
        // unlike Catalog mode, a change made here must persist through the same PropertyChanged-driven
        // auto-save path already used for Parameters/Output/UseShortName - not silently stay unsaved.
        var saveCalls = new List<(string Id, PriceType PriceSource)>();
        var saveCompleted = new TaskCompletionSource();
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                saveCalls.Add((s.Id, s.PriceSource));
                if (saveCalls.Count > 1) saveCompleted.TrySetResult();
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        vm.ClearCategoryFilterCommand.Execute(null);
        var smaItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.SMA);
        Assert.NotNull(smaItem);
        vm.SelectedCatalogItem = smaItem;
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);
        Assert.Single(saveCalls);
        Assert.Equal(PriceType.Close, saveCalls[0].PriceSource); // default before change

        // Registering leaves the window in Catalog mode (deliberately, per the reentrancy fix
        // documented in this feature's step log Lesson Learned) - navigate to "Selected" like a
        // real user would to edit it.
        vm.SelectSelectedCommand.Execute(null);

        // Act: with the registered SMA now selected (Selected mode), change Price Type. The actual
        // persistence call (FireAndForgetSave) runs on a background Task.
        Assert.True(vm.IsSelectedMode);
        Assert.NotNull(vm.EditingSettings);
        vm.EditingSettings!.PriceSource = PriceType.High;

        // Assert: the change persisted onto the registered/saved indicator, not just EditingSettings.
        Assert.Equal(PriceType.High, vm.SelectedRegisteredIndicator?.PriceSource);
        await saveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100); // settle window: prove no further (redundant/re-entrant) saves follow

        // Exactly one additional save (register + this one change = 2 total), not a multiplied burst.
        // CoreIndicatorSettings.Snapshot()/Clone() are MemberwiseClone()-based and shallow-copy the
        // PropertyChanged delegate field, so a naive resubscribe can silently double-subscribe and/or
        // re-enter via SelectedRegisteredIndicator's own inherited subscription - both are guarded
        // against in OnEditingSettingsPropertyChanged/LoadEditingSettings.
        Assert.Equal(2, saveCalls.Count);
        Assert.Equal(PriceType.High, saveCalls[1].PriceSource);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_ChangingParameterOnSelectedIndicator_SavesExactlyOnce()
    {
        // Regression test for the MemberwiseClone()-inherited-subscription hazard: OnParameterChanged
        // assigns SelectedRegisteredIndicator.ParameterObject = EditingSettings.ParameterObject.Clone(),
        // and Clone() is MemberwiseClone()-based, so the clone can silently inherit this very handler's
        // subscription and re-enter it when later mutated. Without the _isSyncingParameterChange guard,
        // a single Period edit could multiply into several redundant saves.
        var saveCalls = new List<int>();
        var saveCompleted = new TaskCompletionSource();
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                saveCalls.Add((s.ParameterObject as CoreSmaParameter)?.Period ?? -1);
                if (saveCalls.Count > 1) saveCompleted.TrySetResult();
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        vm.ClearCategoryFilterCommand.Execute(null);
        var smaItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.SMA);
        Assert.NotNull(smaItem);
        vm.SelectedCatalogItem = smaItem;
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);
        Assert.Single(saveCalls);

        vm.SelectSelectedCommand.Execute(null);

        // Act: edit the Period parameter of the now-selected registered indicator.
        var param = Assert.IsType<CoreSmaParameter>(vm.EditingSettings?.ParameterObject);
        param.Period = 42;

        // Assert: exactly one additional save (register + this one edit = 2 total).
        await saveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(2, saveCalls.Count);
        Assert.Equal(42, saveCalls[1]);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_RegisterIndicator_PreventsReentrancyAndPreservesCatalogMode()
    {
        // Arrange
        var savedDrivers = new List<CoreIndicatorSettings>();
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(savedDrivers);
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s => savedDrivers.Add(s.Snapshot()))
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Switch to Catalog mode
        vm.ClearCategoryFilterCommand.Execute(null);
        Assert.True(vm.IsCatalogMode);
        Assert.False(vm.IsSelectedMode);

        var catalogItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.RSI);
        Assert.NotNull(catalogItem);
        vm.SelectedCatalogItem = catalogItem;

        // Act: Register indicator
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);

        // Assert: Registration succeeds, user remains in Catalog mode (no UI flip re-entrancy)
        Assert.True(vm.IsCatalogMode);
        Assert.False(vm.IsSelectedMode);
        Assert.Single(savedDrivers);
        Assert.Single(vm.RegisteredIndicators);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_TemplateCrud_SavesLoadsAndDeletesTemplates()
    {
        // Arrange
        var templates = new List<DynamicPeriodDriverTemplate>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService.Setup(t => t.GetAllAsync<DynamicPeriodDriverTemplate>(TemplateType.DynamicPeriodDriver))
            .ReturnsAsync(templates);
        mockTemplateService.Setup(t => t.ValidateAsync(It.IsAny<DynamicPeriodDriverTemplate>()))
            .ReturnsAsync(StockAnalyzer.Core.Models.Templates.TemplateValidationResult.Success());
        mockTemplateService.Setup(t => t.SaveAsync(It.IsAny<DynamicPeriodDriverTemplate>()))
            .Returns<DynamicPeriodDriverTemplate>(t =>
            {
                templates.Add(t);
                return Task.CompletedTask;
            });
        mockTemplateService.Setup(t => t.DeleteAsync(TemplateType.DynamicPeriodDriver, It.IsAny<Guid>()))
            .Returns<TemplateType, Guid>((_, id) =>
            {
                int count = templates.RemoveAll(x => x.Id == id);
                return Task.FromResult(count > 0);
            });

        var registered = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Driver_1",
                TypeEnum = IndicatorType.SMA,
                DisplayName = "Simple Moving Average (25)",
                ParameterObject = new CoreSmaParameter { Period = 25 }
            }
        };

        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(registered);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: mockTemplateService.Object,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Act 1: Switch to Templates mode
        vm.SelectTemplatesCommand.Execute(null);
        Assert.True(vm.IsTemplatesSelected);
        Assert.False(vm.IsNotTemplatesSelected);
        Assert.False(vm.IsCatalogMode);
        Assert.False(vm.IsSelectedMode);

        // Act 2: Save template
        vm.NewTemplateName = "Trend Drivers Template";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        // Assert 2: Saved via ITemplateService with TemplateType.DynamicPeriodDriver
        Assert.Single(templates);
        Assert.Equal("Trend Drivers Template", templates[0].Name);
        Assert.Single(templates[0].Indicators);

        // Act 3: Preview template indicators
        vm.SelectedTemplate = vm.Templates.First();
        Assert.Single(vm.SelectedTemplateIndicatorNames);
        Assert.Equal("Simple Moving Average (25)", vm.SelectedTemplateIndicatorNames[0]);

        // Act 4: Delete template
        await vm.DeleteTemplateCommand.ExecuteAsync(vm.SelectedTemplate);
        Assert.Empty(templates);
        Assert.Empty(vm.Templates);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_RegisterIndicator_ShowsIndicatorManagerStyleToastNotification()
    {
        // Arrange
        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockDriverService.Setup(s => s.SaveDynamicPeriodDriverAsync(It.IsAny<CoreIndicatorSettings>()))
            .Returns(Task.CompletedTask);

        string? shownToastMessage = null;
        var mockToastService = new Mock<IToastNotificationService>();
        mockToastService.Setup(t => t.ShowNotification(It.IsAny<string>()))
            .Callback<string>(msg => shownToastMessage = msg);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.DynamicPeriodDriverRegistrationViewModel(
            mockDriverService.Object,
            IndicatorFactory.Default,
            toastService: mockToastService.Object,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Verify ToastService property is exposed
        Assert.NotNull(vm.ToastService);
        Assert.Same(mockToastService.Object, vm.ToastService);

        // Select an indicator from catalog
        vm.ClearCategoryFilterCommand.Execute(null);
        var rsiItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.RSI);
        Assert.NotNull(rsiItem);
        vm.SelectedCatalogItem = rsiItem;

        // Act: Register indicator
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);

        // Assert: Toast notification was displayed matching Indicator Manager style
        Assert.NotNull(shownToastMessage);
        Assert.Contains("Relative Strength Index", shownToastMessage);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_RegisterIndicator_ShowsIndicatorManagerStyleToastNotification()
    {
        // Arrange
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Returns(Task.CompletedTask);

        string? shownToastMessage = null;
        var mockToastService = new Mock<IToastNotificationService>();
        mockToastService.Setup(t => t.ShowNotification(It.IsAny<string>()))
            .Callback<string>(msg => shownToastMessage = msg);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: mockToastService.Object,
            templateService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Verify ToastService property is exposed
        Assert.NotNull(vm.ToastService);
        Assert.Same(mockToastService.Object, vm.ToastService);

        // Select an indicator from catalog
        vm.ClearCategoryFilterCommand.Execute(null);
        var smaItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.SMA);
        Assert.NotNull(smaItem);
        vm.SelectedCatalogItem = smaItem;

        // Act: Register indicator
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);

        // Assert: Toast notification was displayed matching Indicator Manager style
        Assert.NotNull(shownToastMessage);
        Assert.Contains("Simple Moving Average", shownToastMessage);
    }

    [Fact]
    public async Task DynamicPeriodDriverRegistrationViewModel_Delete_BroadcastsSingleIndicatorSettingsChangedMessage()
    {
        // Arrange
        var registeredDriver = new CoreIndicatorSettings
        {
            Id = "Driver_ToDelete",
            TypeEnum = IndicatorType.HilbertTransform,
            DisplayName = "Dominant Cycle"
        };
        var savedDrivers = new List<CoreIndicatorSettings> { registeredDriver };

        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDriversAsync()).ReturnsAsync(savedDrivers);
        mockDriverService.Setup(s => s.DeleteDynamicPeriodDriverAsync(It.IsAny<string>()))
            .Returns<string>(id =>
            {
                int count = savedDrivers.RemoveAll(x => x.Id == id);
                return Task.FromResult(count > 0);
            });

        // Filter by Id: WeakReferenceMessenger.Default is a process-wide singleton also used by
        // other test classes that may run concurrently (different xUnit test classes default to
        // separate parallel collections), so an unfiltered handler can capture an unrelated
        // message broadcast by a concurrently-running test.
        SingleIndicatorSettingsChangedMessage? receivedMessage = null;
        WeakReferenceMessenger.Default.Register<SingleIndicatorSettingsChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value.Id == "Driver_ToDelete") receivedMessage = msg;
        });

        try
        {
            var vm = new DynamicPeriodDriverRegistrationViewModel(
                mockDriverService.Object,
                IndicatorFactory.Default);
            await vm.InitializationTask;

            vm.IsSelectedMode = true;
            var toDelete = vm.RegisteredIndicators.First();

            // Act
            await vm.DeleteRegisteredIndicatorCommand.ExecuteAsync(toDelete);

            // Assert: deletion broadcasts the same message type Save/Update use, so other open
            // dialogs refresh their Dynamic Period Driver reference dropdowns and drop the
            // deleted entry instead of continuing to show a now-dangling option.
            Assert.NotNull(receivedMessage);
            Assert.Equal("Driver_ToDelete", receivedMessage.Value.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<SingleIndicatorSettingsChangedMessage>(this);
        }
    }

    [Fact]
    public void Localization_Btn_AddIndicator_IsDefined()
    {
        var text = StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["Btn_AddIndicator"];
        Assert.False(string.IsNullOrEmpty(text), "Btn_AddIndicator must be defined in localization resources.");
    }
}


