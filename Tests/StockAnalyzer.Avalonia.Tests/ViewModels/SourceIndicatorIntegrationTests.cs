using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class SourceIndicatorIntegrationTests
{
    [Fact]
    public void IndicatorReferenceHelper_GetChainingOptions_OrdersDefaultThenActiveThenRegistered()
    {
        // Arrange
        var activeChartIndicators = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Chart_EMA",
                DisplayName = "EMA (20) [Chart]",
                TypeEnum = IndicatorType.EMA,
                IsEnabled = true,
                IsOverlay = true
            }
        };

        var registeredSourceIndicators = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "Registered_RSI",
                DisplayName = "RSI (14) [Registered]",
                TypeEnum = IndicatorType.RSI,
                IsOverlay = false,
                OverlayPanelId = "panel_rsi"
            }
        };

        // Act
        var sources = new System.Collections.ObjectModel.ObservableCollection<IndicatorReferenceOption>();
        var drivers = new System.Collections.ObjectModel.ObservableCollection<IndicatorReferenceOption>();
        IndicatorReferenceHelper.PopulateReferenceOptions(
            sources,
            drivers,
            allIndicators: activeChartIndicators,
            currentIndicatorId: "SMA_Target",
            registeredSourceIndicators: registeredSourceIndicators);

        // Assert
        Assert.Equal(3, sources.Count);
        var options = sources;

        // 1st: Default (Price)
        Assert.Null(options[0].Id);
        Assert.True(options[0].IsOverlay);

        // 2nd: Active chart indicators
        Assert.Equal("Chart_EMA", options[1].Id);
        Assert.Equal("EMA (20) [Chart]", options[1].DisplayName);

        // 3rd: Registered source indicators
        Assert.Equal("Registered_RSI", options[2].Id);
        Assert.Equal("RSI (14) [Registered]", options[2].DisplayName);
        Assert.False(options[2].IsOverlay);
        Assert.Equal("panel_rsi", options[2].OverlayPanelId);
    }

    [Fact]
    public void IndicatorSettingsDialogViewModel_SelectingSubWindowSource_SyncsIsOverlayAndPanelId()
    {
        // Arrange
        var registeredRsi = new CoreIndicatorSettings
        {
            Id = "Reg_RSI_14",
            DisplayName = "Registered RSI",
            TypeEnum = IndicatorType.RSI,
            IsOverlay = false,
            OverlayPanelId = "rsi_panel_99"
        };

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicators())
            .Returns(new List<CoreIndicatorSettings> { registeredRsi });

        var mockDialogService = new Mock<IDialogService>();
        var mockToastService = new Mock<IToastNotificationService>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService.Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator))
            .ReturnsAsync(new List<IndicatorTemplate>());
        var mockUserDefaultService = new Mock<IIndicatorUserDefaultService>();

        var vm = new IndicatorSettingsDialogViewModel(
            mockDialogService.Object,
            IndicatorFactory.Default,
            mockToastService.Object,
            mockTemplateService.Object,
            mockUserDefaultService.Object,
            sourceIndicatorService: mockSourceService.Object);

        var sma = new CoreIndicatorSettings
        {
            Id = "SMA_Main",
            DisplayName = "SMA (20)",
            TypeEnum = IndicatorType.SMA,
            Category = CoreIndicatorCategory.Trend,
            IsEnabled = true,
            IsOverlay = true, // Initially main overlay
            OverlayPanelId = null,
            ParameterObject = new CoreSmaParameter { Period = 20 }
        };

        vm.Initialize(new[] { sma });
        vm.SelectedIndicator = sma;

        Assert.True(vm.SelectedIndicator.IsOverlay);
        Assert.Null(vm.SelectedIndicator.OverlayPanelId);

        // Act 1: Select registered RSI (which is a SubWindow indicator)
        var rsiOption = vm.AvailableSourceIndicators.FirstOrDefault(o => o.Id == "Reg_RSI_14");
        Assert.NotNull(rsiOption);

        vm.SelectedSourceIndicatorOption = rsiOption;

        // Assert 1: SMA should now be dynamically treated as sub-window in the same panel as RSI
        Assert.False(vm.SelectedIndicator.IsOverlay, "Selecting subwindow source indicator must set IsOverlay to false.");
        Assert.Equal("rsi_panel_99", vm.SelectedIndicator.OverlayPanelId);

        // Act 2: Revert to default price source
        var defaultOption = vm.AvailableSourceIndicators.First(o => o.Id == null);
        vm.SelectedSourceIndicatorOption = defaultOption;

        // Assert 2: SMA should revert to its default IsOverlay (true)
        Assert.True(vm.SelectedIndicator.IsOverlay, "Selecting default price source must restore default IsOverlay.");
    }

    [Fact]
    public void IndicatorPropertiesViewModel_SelectingSubWindowSource_SyncsIsOverlayAndPanelId()
    {
        // Arrange
        var registeredRsi = new CoreIndicatorSettings
        {
            Id = "Reg_RSI_14",
            DisplayName = "Registered RSI",
            TypeEnum = IndicatorType.RSI,
            IsOverlay = false,
            OverlayPanelId = "rsi_panel_42"
        };

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicators())
            .Returns(new List<CoreIndicatorSettings> { registeredRsi });

        var messenger = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();

        var sma = new CoreIndicatorSettings
        {
            Id = "SMA_Target",
            DisplayName = "SMA (25)",
            TypeEnum = IndicatorType.SMA,
            Category = CoreIndicatorCategory.Trend,
            IsEnabled = true,
            IsOverlay = true,
            OverlayPanelId = null,
            ParameterObject = new CoreSmaParameter { Period = 25 }
        };

        var vm = new IndicatorPropertiesViewModel(
            sma,
            messenger,
            dispatcher,
            allIndicators: Enumerable.Empty<CoreIndicatorSettings>(),
            dialogService: null,
            sourceIndicatorService: mockSourceService.Object);

        // Act 1: Select subwindow source
        var rsiOption = vm.AvailableSourceIndicators.FirstOrDefault(o => o.Id == "Reg_RSI_14");
        Assert.NotNull(rsiOption);

        vm.SelectedSourceIndicatorOption = rsiOption;

        // Assert 1
        Assert.False(vm.EditingSettings.IsOverlay);
        Assert.Equal("rsi_panel_42", vm.EditingSettings.OverlayPanelId);

        // Act 2: Revert to default
        var defaultOption = vm.AvailableSourceIndicators.First(o => o.Id == null);
        vm.SelectedSourceIndicatorOption = defaultOption;

        // Assert 2
        Assert.True(vm.EditingSettings.IsOverlay);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_RegisterAndDelete_WorksCorrectly()
    {
        // Arrange
        var savedIndicators = new List<CoreIndicatorSettings>();

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(() => savedIndicators.Select(i => i.Snapshot()).ToList());
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(i =>
            {
                var existing = savedIndicators.FindIndex(x => x.Id == i.Id);
                if (existing >= 0) savedIndicators[existing] = i.Snapshot();
                else savedIndicators.Add(i.Snapshot());
            })
            .Returns(Task.CompletedTask);
        mockSourceService.Setup(s => s.DeleteSourceIndicatorAsync(It.IsAny<string>()))
            .Returns<string>(id =>
            {
                int count = savedIndicators.RemoveAll(x => x.Id == id);
                return Task.FromResult(count > 0);
            });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Act 1: Initial state
        Assert.True(vm.IsSelectedMode);
        Assert.NotEmpty(vm.Categories);

        // Act 2: Pick Oscillator category and register RSI
        vm.SelectedCategory = CoreIndicatorCategory.Oscillator;
        Assert.False(vm.IsSelectedMode);

        var rsiCatalogItem = vm.FilteredCatalogItems.FirstOrDefault(t => t.Type == IndicatorType.RSI);
        Assert.NotNull(rsiCatalogItem);
        vm.SelectedCatalogItem = rsiCatalogItem;

        // Verify editing settings loaded
        Assert.NotNull(vm.EditingSettings);
        Assert.Equal(IndicatorType.RSI, vm.EditingSettings.TypeEnum);

        // Register it
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);

        // Assert 2: Saved to service and present in RegisteredIndicators
        Assert.Single(savedIndicators);
        Assert.Single(vm.RegisteredIndicators);
        Assert.Equal(IndicatorType.RSI, vm.RegisteredIndicators[0].TypeEnum);

        // Act 3: Delete the registered indicator
        vm.IsSelectedMode = true;
        var toDelete = vm.RegisteredIndicators[0];
        vm.SelectedRegisteredIndicator = toDelete;

        await vm.DeleteRegisteredIndicatorCommand.ExecuteAsync(toDelete);

        // Assert 3: Deleted from service and RegisteredIndicators
        Assert.Empty(savedIndicators);
        Assert.Empty(vm.RegisteredIndicators);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_Delete_BroadcastsSingleIndicatorSettingsChangedMessage()
    {
        // Arrange
        var registeredRsi = new CoreIndicatorSettings
        {
            Id = "Source_RSI_ToDelete",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI(14)",
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };
        var savedIndicators = new List<CoreIndicatorSettings> { registeredRsi };

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync()).ReturnsAsync(savedIndicators);
        mockSourceService.Setup(s => s.DeleteSourceIndicatorAsync(It.IsAny<string>()))
            .Returns<string>(id =>
            {
                int count = savedIndicators.RemoveAll(x => x.Id == id);
                return Task.FromResult(count > 0);
            });

        // Filter by Id: WeakReferenceMessenger.Default is a process-wide singleton also used by
        // other test classes that may run concurrently (different xUnit test classes default to
        // separate parallel collections), so an unfiltered handler can capture an unrelated
        // message broadcast by a concurrently-running test.
        SingleIndicatorSettingsChangedMessage? receivedMessage = null;
        WeakReferenceMessenger.Default.Register<SingleIndicatorSettingsChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value.Id == "Source_RSI_ToDelete") receivedMessage = msg;
        });

        try
        {
            var vm = new SourceIndicatorRegistrationViewModel(
                mockSourceService.Object,
                IndicatorFactory.Default);
            await vm.InitializationTask;

            vm.IsSelectedMode = true;
            var toDelete = vm.RegisteredIndicators.First();

            // Act
            await vm.DeleteRegisteredIndicatorCommand.ExecuteAsync(toDelete);

            // Assert: deletion broadcasts the same message type Save/Update use, so other open
            // dialogs (e.g. a second Indicator Manager window) refresh their reference dropdowns
            // and drop the deleted entry instead of continuing to show a now-dangling option.
            Assert.NotNull(receivedMessage);
            Assert.Equal("Source_RSI_ToDelete", receivedMessage.Value.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<SingleIndicatorSettingsChangedMessage>(this);
        }
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_ToggleUseShortName_UpdatesDisplayNameAndPreviewNameLive()
    {
        // Arrange
        var registeredSma = new CoreIndicatorSettings
        {
            Id = "Reg_SMA_20",
            DisplayName = "Simple Moving Average (20)",
            TypeEnum = IndicatorType.SMA,
            UseShortName = false,
            ParameterObject = new CoreSmaParameter { Period = 20 }
        };

        var savedIndicators = new List<CoreIndicatorSettings> { registeredSma };
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(savedIndicators);
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Returns<CoreIndicatorSettings>(s =>
            {
                var existing = savedIndicators.FirstOrDefault(x => x.Id == s.Id);
                if (existing != null)
                {
                    int idx = savedIndicators.IndexOf(existing);
                    savedIndicators[idx] = s;
                }
                else
                {
                    savedIndicators.Add(s);
                }
                return Task.FromResult(s);
            });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Select the indicator
        vm.IsSelectedMode = true;
        vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First();

        // Initially UseShortName is false -> PreviewName is "Simple Moving Average (20)"
        Assert.False(vm.UseShortName);
        Assert.Equal("Simple Moving Average (20)", vm.PreviewName);
        Assert.Equal("Simple Moving Average (20)", vm.SelectedRegisteredIndicator.DisplayName);

        // Act 1: Toggle UseShortName to true
        vm.UseShortName = true;

        // Assert 1: Live reflected in PreviewName and SelectedRegisteredIndicator.DisplayName
        Assert.Equal("SMA(20)", vm.PreviewName);
        Assert.Equal("SMA(20)", vm.SelectedRegisteredIndicator.DisplayName);
        Assert.Equal("SMA(20)", savedIndicators.First().DisplayName);
        Assert.True(savedIndicators.First().UseShortName);

        // Act 2: Toggle UseShortName back to false
        vm.UseShortName = false;

        // Assert 2: Live reflected back to long name with parameters
        Assert.Equal("Simple Moving Average (20)", vm.PreviewName);
        Assert.Equal("Simple Moving Average (20)", vm.SelectedRegisteredIndicator.DisplayName);
        Assert.Equal("Simple Moving Average (20)", savedIndicators.First().DisplayName);
        Assert.False(savedIndicators.First().UseShortName);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_ChangingPriceTypeOnSelectedIndicator_PersistsToRegisteredIndicator()
    {
        // Arrange: register a non-Price indicator (SMA), then select it from "Selected" mode and change
        // its Price Type (EditingSettings.PriceSource) via the newly-added Price Type selector. This
        // window has no visible "Add Indicator" button in Selected mode (Catalog-mode only), so unlike
        // Catalog mode, a change made here must persist through the same PropertyChanged-driven
        // auto-save path already used for Parameters/Output/UseShortName - not silently stay unsaved.
        var saveCalls = new List<(string Id, PriceType PriceSource)>();
        var saveCompleted = new TaskCompletionSource();
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                saveCalls.Add((s.Id, s.PriceSource));
                if (saveCalls.Count > 1) saveCompleted.TrySetResult();
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        vm.ClearCategoryFilterCommand.Execute(null);
        var smaItem = vm.FilteredCatalogItems.FirstOrDefault(i => i.Type == IndicatorType.SMA);
        Assert.NotNull(smaItem);
        vm.SelectedCatalogItem = smaItem;
        await vm.RegisterIndicatorCommand.ExecuteAsync(null);
        Assert.Single(saveCalls);
        Assert.Equal(PriceType.Close, saveCalls[0].PriceSource); // default before change

        // Registering leaves the window in Catalog mode (deliberately, per the reentrancy fix documented
        // in DynamicPeriodDriverRegistrationViewModel's step log) - navigate to "Selected" as a real user would.
        vm.SelectSelectedCommand.Execute(null);

        // Act: with the registered SMA now selected (Selected mode), change Price Type. The actual
        // persistence call (FireAndForgetSave) runs on a background Task.
        Assert.True(vm.IsSelectedMode);
        Assert.NotNull(vm.EditingSettings);
        vm.EditingSettings!.PriceSource = PriceType.High;

        // Assert: the change persisted onto the registered/saved indicator, not just EditingSettings, and
        // exactly one additional save happened (register + this one change = 2 total) - not a multiplied
        // burst, which would indicate the MemberwiseClone-inherited-subscription hazard resurfaced.
        Assert.Equal(PriceType.High, vm.SelectedRegisteredIndicator?.PriceSource);
        await saveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100); // settle window: prove no further (redundant/re-entrant) saves follow
        Assert.Equal(2, saveCalls.Count);
        Assert.Equal(PriceType.High, saveCalls[1].PriceSource);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_ChangingParameterOnSelectedIndicator_SavesExactlyOnce()
    {
        // Regression test for the MemberwiseClone()-inherited-subscription hazard: OnParameterChanged
        // assigns SelectedRegisteredIndicator.ParameterObject = EditingSettings.ParameterObject.Clone(),
        // and Clone() is MemberwiseClone()-based, so the clone can silently inherit this very handler's
        // subscription and re-enter it when later mutated. Without the _isSyncingParameterChange guard,
        // a single Period edit could multiply into several redundant saves.
        var saveCalls = new List<int>();
        var saveCompleted = new TaskCompletionSource();
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>());
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                saveCalls.Add((s.ParameterObject as CoreSmaParameter)?.Period ?? -1);
                if (saveCalls.Count > 1) saveCompleted.TrySetResult();
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
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
    public void IndicatorReferenceHelper_RegisteredIndicator_ReflectsLongAndShortNameInMenu()
    {
        // Case 1: Long Name (UseShortName = false)
        var longSma = new CoreIndicatorSettings
        {
            Id = "Source_Long_SMA",
            TypeEnum = IndicatorType.SMA,
            UseShortName = false,
            ParameterObject = new CoreSmaParameter { Period = 25 }
        };

        // Case 2: Short Name (UseShortName = true)
        var shortEma = new CoreIndicatorSettings
        {
            Id = "Source_Short_EMA",
            TypeEnum = IndicatorType.EMA,
            UseShortName = true,
            ParameterObject = new CoreEmaParameter { Period = 12 }
        };

        var sources = new System.Collections.ObjectModel.ObservableCollection<IndicatorReferenceOption>();
        var drivers = new System.Collections.ObjectModel.ObservableCollection<IndicatorReferenceOption>();

        IndicatorReferenceHelper.PopulateReferenceOptions(
            sources,
            drivers,
            allIndicators: null,
            currentIndicatorId: "Current_Target",
            registeredSourceIndicators: new List<CoreIndicatorSettings> { longSma, shortEma });

        // First item is Default
        Assert.Equal(3, sources.Count);
        // Second item is long SMA with parameters
        Assert.Equal("Simple Moving Average (25)", sources[1].DisplayName);
        // Third item is short EMA
        Assert.Equal("EMA(12)", sources[2].DisplayName);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_TemplateCrud_SavesAndLoadsTemplates()
    {
        var templates = new List<SourceIndicatorTemplate>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService.Setup(t => t.GetAllAsync<SourceIndicatorTemplate>(TemplateType.SourceIndicator))
            .ReturnsAsync(templates);
        mockTemplateService.Setup(t => t.ValidateAsync(It.IsAny<SourceIndicatorTemplate>()))
            .ReturnsAsync(TemplateValidationResult.Success());
        mockTemplateService.Setup(t => t.SaveAsync(It.IsAny<SourceIndicatorTemplate>()))
            .Returns<SourceIndicatorTemplate>(t =>
            {
                templates.Add(t);
                return Task.CompletedTask;
            });

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings
                {
                    Id = "Ind_1",
                    TypeEnum = IndicatorType.SMA,
                    DisplayName = "SMA(20)",
                    ParameterObject = new CoreSmaParameter { Period = 20 }
                }
            });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            templateService: mockTemplateService.Object,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Switch to templates
        vm.SelectTemplatesCommand.Execute(null);
        Assert.True(vm.IsTemplatesSelected);

        // Save a template
        vm.NewTemplateName = "Trend Following Sources";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        // Assert template was saved through ITemplateService with TemplateType.SourceIndicator
        Assert.Single(templates);
        Assert.Equal("Trend Following Sources", templates[0].Name);
        Assert.Single(templates[0].Indicators);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_AddButtonVisibility_HiddenInSelectedMode_VisibleInCatalogMode()
    {
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings
                {
                    Id = "Ind_1",
                    TypeEnum = IndicatorType.SMA,
                    DisplayName = "SMA(20)",
                    ParameterObject = new CoreSmaParameter { Period = 20 }
                }
            });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // 1. Initially in Selected mode: Add button must NOT be visible (IsCatalogMode == false)
        Assert.True(vm.IsSelectedMode);
        Assert.False(vm.IsCatalogMode);

        // 2. Switch to Catalog mode: Add button must be visible (IsCatalogMode == true)
        vm.ClearCategoryFilterCommand.Execute(null);
        Assert.False(vm.IsSelectedMode);
        Assert.True(vm.IsCatalogMode);

        // 3. Switch back to Selected mode: Add button must be hidden again
        vm.SelectSelectedCommand.Execute(null);
        Assert.True(vm.IsSelectedMode);
        Assert.False(vm.IsCatalogMode);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_SubWindowPanelSelection_SyncsOverlayPanelIdAndAutoSaves()
    {
        var rsi = new CoreIndicatorSettings
        {
            Id = "Source_RSI_14",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI(14)",
            IsOverlay = false,
            OverlayPanelId = null,
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };

        var savedIndicators = new List<CoreIndicatorSettings> { rsi };
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(savedIndicators);
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                var idx = savedIndicators.FindIndex(x => x.Id == s.Id);
                if (idx >= 0) savedIndicators[idx] = s.Snapshot();
                else savedIndicators.Add(s.Snapshot());
            })
            .Returns(Task.CompletedTask);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        // Select the RSI indicator in Selected mode
        vm.IsSelectedMode = true;
        vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First();

        // 1. ShowPanelSelector is true for Sub-Window indicators
        Assert.True(vm.ShowPanelSelector);
        Assert.True(vm.PanelOptions.Count >= 7);

        // Initially default panel (null)
        Assert.Equal(vm.PanelOptions[0], vm.SelectedOverlayPanelOption);
        Assert.Null(vm.EditingSettings?.OverlayPanelId);

        // 2. Select "Panel B"
        var panelBOption = vm.PanelOptions.FirstOrDefault(o => o.EndsWith("B"));
        Assert.NotNull(panelBOption);
        vm.SelectedOverlayPanelOption = panelBOption;

        // Verify synced to EditingSettings and SelectedRegisteredIndicator and saved
        Assert.Equal("B", vm.EditingSettings?.OverlayPanelId);
        Assert.Equal("B", vm.SelectedRegisteredIndicator?.OverlayPanelId);
        Assert.Equal("B", savedIndicators.First().OverlayPanelId);

        // 3. Select back to Default
        vm.SelectedOverlayPanelOption = vm.PanelOptions[0];
        Assert.Null(vm.EditingSettings?.OverlayPanelId);
        Assert.Null(vm.SelectedRegisteredIndicator?.OverlayPanelId);
        Assert.Null(savedIndicators.First().OverlayPanelId);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_ParameterChanged_BroadcastsMessageAndBumpsDependentChartIndicator()
    {
        var rsiParam = new CoreRsiParameter { Period = 14 };
        var registeredRsi = new CoreIndicatorSettings
        {
            Id = "Source_RSI_14",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI(14)",
            IsOverlay = false,
            ParameterObject = rsiParam
        };

        var savedIndicators = new List<CoreIndicatorSettings> { registeredRsi };
        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicators())
            .Returns(savedIndicators);
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync())
            .ReturnsAsync(savedIndicators);
        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                var idx = savedIndicators.FindIndex(x => x.Id == s.Id);
                if (idx >= 0) savedIndicators[idx] = s.Snapshot();
                else savedIndicators.Add(s.Snapshot());
            })
            .Returns(Task.CompletedTask);

        // Dependent chart indicator: SMA using Source_RSI_14 as input
        var chartSma = new CoreIndicatorSettings
        {
            Id = "Chart_SMA_Target",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(20)",
            SourceIndicatorId = "Source_RSI_14",
            MathematicalVersion = 1,
            ParameterObject = new CoreSmaParameter { Period = 20 }
        };

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();

        // Setup MainWindowViewModel with ChartViewModel containing the dependent indicator
        var mockDialogService = new Mock<IDialogService>();
        var mockWorkspaceFacade = new Mock<IWorkspaceViewModelFacade>();
        var mockThemeManager = new Mock<IThemeManager>();
        var mockLocalization = new Mock<ILocalizationService>();
        var mockCoreServices = new Mock<ICoreServicesFacade>();
        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        mockCoreServices.Setup(c => c.WatchlistManager).Returns(mockWatchlist.Object);
        mockCoreServices.Setup(c => c.Settings).Returns(new Mock<IStockAnalyzerSettings>().Object);
        mockCoreServices.Setup(c => c.MarketDataProvider).Returns(new Mock<IMarketDataProvider>().Object);
        mockCoreServices.Setup(c => c.PythonService).Returns(new Mock<IPythonService>().Object);

        var chartVm = new ChartViewModel();
        chartVm.Indicators.Add(chartSma);

        var tickerListVm = new TickerListViewModel(
            new Mock<IMarketDataProvider>().Object,
            new Mock<IPythonService>().Object,
            WeakReferenceMessenger.Default,
            dispatcher,
            mockWatchlist.Object,
            new PortfolioManager(),
            mockDialogService.Object,
            new TickerImportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerImportService>.Instance),
            new Mock<IChartSettingsManager>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerListViewModel>.Instance);

        mockWorkspaceFacade.Setup(w => w.TickerList).Returns(tickerListVm);
        mockWorkspaceFacade.Setup(w => w.DataWindow).Returns(new DataWindowViewModel(chartVm));
        mockWorkspaceFacade.Setup(w => w.Sidebar).Returns(new DrawingToolSidebarViewModel(chartVm));
        mockWorkspaceFacade.Setup(w => w.DrawingObjects).Returns(new DrawingObjectsViewModel(chartVm, dispatcher));

        var mockWindowManagement = new Mock<IWindowManagementService>();
        mockWindowManagement.Setup(w => w.BoundaryService).Returns(new Mock<IWindowBoundaryService>().Object);
        mockWindowManagement.Setup(w => w.TabFactory).Returns(new Mock<IPanelTabFactory>().Object);
        mockWindowManagement.Setup(w => w.TearOff).Returns(new Mock<ITearOffService>().Object);
        mockWindowManagement.Setup(w => w.WindowFactory).Returns(new Mock<IDetachedWindowFactory>().Object);

        var mockDetachedTabManager = new Mock<IDetachedTabManager>();
        var stateStore = new LayoutStateStore();
        var mockSaveScheduler = new Mock<ILayoutSaveScheduler>();
        var mockCoordinator = new Mock<IWorkspaceCoordinator>();
        var mockSerialization = new Mock<IWorkspaceSerializationService>();

        var mainVm = new MainWindowViewModel(
            mockDialogService.Object,
            mockWorkspaceFacade.Object,
            mockThemeManager.Object,
            dispatcher,
            mockLocalization.Object,
            mockCoreServices.Object,
            mockWindowManagement.Object,
            mockDetachedTabManager.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance,
            stateStore,
            mockSaveScheduler.Object,
            mockCoordinator.Object,
            mockSerialization.Object,
            () => chartVm,
            mockSourceService.Object);

        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default,
            toastService: null,
            dispatcherService: dispatcher);
        await vm.InitializationTask;

        vm.IsSelectedMode = true;
        vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First();

        long initialMathVersion = chartVm.Indicators[0].MathematicalVersion;

        // Act: Change period of the source indicator in Source Indicator window
        var editingParam = vm.EditingSettings?.ParameterObject as CoreRsiParameter;
        Assert.NotNull(editingParam);
        editingParam.Period = 21;

        // Assert:
        // 1. Saved to service with new period 21
        Assert.Equal(21, (savedIndicators[0].ParameterObject as CoreRsiParameter)?.Period);

        // 2. MainWindowViewModel received SingleIndicatorSettingsChangedMessage, detected dependent chart indicator,
        // and bumped MathematicalVersion, triggering live chart recalculation
        Assert.True(chartVm.Indicators[0].MathematicalVersion > initialMathVersion,
            $"Expected MathematicalVersion to increase from {initialMathVersion}, but got {chartVm.Indicators[0].MathematicalVersion}");

        // Cleanup: MainWindowViewModel registers itself with the process-wide WeakReferenceMessenger.Default
        // in its constructor; leaving it undisposed leaks a live recipient that can intercept unrelated
        // messages (e.g. CurrentTickerRequestMessage) sent by other tests later in the same test run.
        mainVm.Dispose();
    }

    [Fact]
    public void MainWindowViewModel_DriverEdit_PropagatesThroughRegisteredDriverChain()
    {
        // Arrange: registered Source Indicator "Source_A" and a registered Dynamic Period Driver
        // "Driver_B" that itself chains its input FROM Source_A (Driver_B.SourceIndicatorId = Source_A).
        // A chart indicator uses Driver_B (not Source_A directly) as its DynamicPeriodIndicatorId.
        // Editing Source_A must still invalidate the chart indicator, which requires walking the
        // Dynamic Period Driver registry (not just the Source Indicator registry) to discover that
        // Driver_B depends on Source_A. Regression coverage for MainWindowViewModel.cs's transitive
        // closure previously omitting IDynamicPeriodDriverService entirely.
        var sourceA = new CoreIndicatorSettings
        {
            Id = "Source_A_ForDriverChain",
            TypeEnum = IndicatorType.RSI,
            DisplayName = "RSI(14)",
            ParameterObject = new CoreRsiParameter { Period = 14 }
        };

        var driverB = new CoreIndicatorSettings
        {
            Id = "Driver_B_ForDriverChain",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(10) Driver",
            SourceIndicatorId = "Source_A_ForDriverChain",
            ParameterObject = new CoreSmaParameter { Period = 10 }
        };

        var chartIndicator = new CoreIndicatorSettings
        {
            Id = "Chart_UsesDriverB",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "SMA(20)",
            DynamicPeriodIndicatorId = "Driver_B_ForDriverChain",
            MathematicalVersion = 1,
            ParameterObject = new CoreSmaParameter { Period = 20 }
        };

        var mockSourceService = new Mock<ISourceIndicatorService>();
        mockSourceService.Setup(s => s.GetSourceIndicators()).Returns(new List<CoreIndicatorSettings> { sourceA });

        var mockDriverService = new Mock<IDynamicPeriodDriverService>();
        mockDriverService.Setup(s => s.GetDynamicPeriodDrivers()).Returns(new List<CoreIndicatorSettings> { driverB });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var mockDialogService = new Mock<IDialogService>();
        var mockWorkspaceFacade = new Mock<IWorkspaceViewModelFacade>();
        var mockThemeManager = new Mock<IThemeManager>();
        var mockLocalization = new Mock<ILocalizationService>();
        var mockCoreServices = new Mock<ICoreServicesFacade>();
        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        mockCoreServices.Setup(c => c.WatchlistManager).Returns(mockWatchlist.Object);
        mockCoreServices.Setup(c => c.Settings).Returns(new Mock<IStockAnalyzerSettings>().Object);
        mockCoreServices.Setup(c => c.MarketDataProvider).Returns(new Mock<IMarketDataProvider>().Object);
        mockCoreServices.Setup(c => c.PythonService).Returns(new Mock<IPythonService>().Object);

        var chartVm = new ChartViewModel();
        chartVm.Indicators.Add(chartIndicator);

        var tickerListVm = new TickerListViewModel(
            new Mock<IMarketDataProvider>().Object,
            new Mock<IPythonService>().Object,
            WeakReferenceMessenger.Default,
            dispatcher,
            mockWatchlist.Object,
            new PortfolioManager(),
            mockDialogService.Object,
            new TickerImportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerImportService>.Instance),
            new Mock<IChartSettingsManager>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerListViewModel>.Instance);

        mockWorkspaceFacade.Setup(w => w.TickerList).Returns(tickerListVm);
        mockWorkspaceFacade.Setup(w => w.DataWindow).Returns(new DataWindowViewModel(chartVm));
        mockWorkspaceFacade.Setup(w => w.Sidebar).Returns(new DrawingToolSidebarViewModel(chartVm));
        mockWorkspaceFacade.Setup(w => w.DrawingObjects).Returns(new DrawingObjectsViewModel(chartVm, dispatcher));

        var mockWindowManagement = new Mock<IWindowManagementService>();
        mockWindowManagement.Setup(w => w.BoundaryService).Returns(new Mock<IWindowBoundaryService>().Object);
        mockWindowManagement.Setup(w => w.TabFactory).Returns(new Mock<IPanelTabFactory>().Object);
        mockWindowManagement.Setup(w => w.TearOff).Returns(new Mock<ITearOffService>().Object);
        mockWindowManagement.Setup(w => w.WindowFactory).Returns(new Mock<IDetachedWindowFactory>().Object);

        var mockDetachedTabManager = new Mock<IDetachedTabManager>();
        var stateStore = new LayoutStateStore();
        var mockSaveScheduler = new Mock<ILayoutSaveScheduler>();
        var mockCoordinator = new Mock<IWorkspaceCoordinator>();
        var mockSerialization = new Mock<IWorkspaceSerializationService>();

        var mainVm = new MainWindowViewModel(
            mockDialogService.Object,
            mockWorkspaceFacade.Object,
            mockThemeManager.Object,
            dispatcher,
            mockLocalization.Object,
            mockCoreServices.Object,
            mockWindowManagement.Object,
            mockDetachedTabManager.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance,
            stateStore,
            mockSaveScheduler.Object,
            mockCoordinator.Object,
            mockSerialization.Object,
            () => chartVm,
            mockSourceService.Object,
            mockDriverService.Object);

        long initialMathVersion = chartVm.Indicators[0].MathematicalVersion;

        // Act: Source_A was edited elsewhere (e.g. its Registration window), broadcasting that its
        // own settings changed. Source_A is not itself on the chart and is not directly referenced
        // by the chart indicator (only Driver_B is) -- propagation must hop through the Driver registry.
        mainVm.Receive(new SingleIndicatorSettingsChangedMessage(sourceA));

        // Assert: the chart indicator (which only references Driver_B) was invalidated.
        Assert.True(chartVm.Indicators[0].MathematicalVersion > initialMathVersion,
            $"Expected MathematicalVersion to increase from {initialMathVersion}, but got {chartVm.Indicators[0].MathematicalVersion}");

        // Cleanup: unregister from the process-wide WeakReferenceMessenger.Default (see comment on
        // the equivalent cleanup above) so this instance cannot intercept other tests' messages.
        mainVm.Dispose();
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_WhenMultiOutputIndicatorSelected_PopulatesAvailableOutputs()
    {
        // Arrange
        var mockSourceService = new Mock<ISourceIndicatorService>();
        var registeredList = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "MACD_1",
                TypeEnum = IndicatorType.MACD,
                DisplayName = "MACD (12, 26, 9)",
                ParameterObject = new CoreMacdParameter { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 }
            },
            new CoreIndicatorSettings
            {
                Id = "SMA_1",
                TypeEnum = IndicatorType.SMA,
                DisplayName = "SMA (20)",
                ParameterObject = new CoreSmaParameter { Period = 20 }
            }
        };

        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync()).ReturnsAsync(registeredList);

        var vm = new SourceIndicatorRegistrationViewModel(
            mockSourceService.Object,
            IndicatorFactory.Default);
        await vm.InitializationTask;

        // Act 1: Select MACD (multi-output)
        vm.IsSelectedMode = true;
        vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First(i => i.TypeEnum == IndicatorType.MACD);

        // Assert 1: HasMultipleOutputs is true, AvailableOutputs contains MacdLine, Signal, Histogram
        Assert.True(vm.HasMultipleOutputs);
        Assert.Contains("Signal", vm.AvailableOutputs);
        Assert.Contains("Histogram", vm.AvailableOutputs);

        // Act 2: Select SMA (single output)
        vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First(i => i.TypeEnum == IndicatorType.SMA);

        // Assert 2: HasMultipleOutputs is false
        Assert.False(vm.HasMultipleOutputs);
    }

    [Fact]
    public async Task SourceIndicatorRegistrationViewModel_WhenOutputSeriesChangedInSelectedMode_SavesAndNotifiesMessage()
    {
        // Arrange
        var mockSourceService = new Mock<ISourceIndicatorService>();
        var savedIndicators = new List<CoreIndicatorSettings>();

        mockSourceService.Setup(s => s.SaveSourceIndicatorAsync(It.IsAny<CoreIndicatorSettings>()))
            .Callback<CoreIndicatorSettings>(s =>
            {
                var existing = savedIndicators.FirstOrDefault(i => i.Id == s.Id);
                if (existing != null) savedIndicators.Remove(existing);
                savedIndicators.Add(s);
            })
            .Returns(Task.CompletedTask);

        var registeredList = new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings
            {
                Id = "MACD_Source",
                TypeEnum = IndicatorType.MACD,
                DisplayName = "MACD (12, 26, 9)",
                ParameterObject = new CoreMacdParameter { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 }
            }
        };
        mockSourceService.Setup(s => s.GetSourceIndicatorsAsync()).ReturnsAsync(registeredList);

        SingleIndicatorSettingsChangedMessage? receivedMessage = null;
        WeakReferenceMessenger.Default.Register<SingleIndicatorSettingsChangedMessage>(this, (_, msg) =>
        {
            receivedMessage = msg;
        });

        try
        {
            var vm = new SourceIndicatorRegistrationViewModel(
                mockSourceService.Object,
                IndicatorFactory.Default);
            await vm.InitializationTask;

            vm.IsSelectedMode = true;
            vm.SelectedRegisteredIndicator = vm.RegisteredIndicators.First();

            // Act: Change SelectedOutput from default to "Signal"
            vm.SelectedOutput = "Signal";

            // Assert:
            // 1. OutputSeriesName updated in EditingSettings and SelectedRegisteredIndicator
            Assert.Equal("Signal", vm.EditingSettings?.OutputSeriesName);
            Assert.Equal("Signal", vm.SelectedRegisteredIndicator?.OutputSeriesName);

            // 2. PreviewName updated to reflect the selected output series
            Assert.Contains("Signal", vm.PreviewName);

            // 3. Saved to service with OutputSeriesName = "Signal"
            Assert.True(savedIndicators.Count > 0);
            Assert.Equal("Signal", savedIndicators.Last().OutputSeriesName);

            // 4. WeakReferenceMessenger notification received
            Assert.NotNull(receivedMessage);
            Assert.Equal("Signal", receivedMessage.Value.OutputSeriesName);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<SingleIndicatorSettingsChangedMessage>(this);
        }
    }
}
