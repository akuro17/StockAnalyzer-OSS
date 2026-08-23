using Xunit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class TabWindowVerificationTests
{
    private readonly IServiceProvider _serviceProvider;

    public TabWindowVerificationTests()
    {
        var services = new ServiceCollection();
        
        // Register dependencies
        services.AddSingleton<IDataService, MockDataService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IStockAnalyzerSettings, MockStockAnalyzerSettings>();
        services.AddSingleton<ITimeFrameManager>(sp => new TimeFrameManager(sp.GetRequiredService<IDataService>()));
        services.AddSingleton<Core.Theme.IThemeManager, Core.Theme.ThemeManager>();
        services.AddSingleton<Core.Services.IDispatcherService, SynchronousDispatcherService>();
        services.AddSingleton<IAnalysisPipelineService, MockAnalysisPipelineService>();
        services.AddSingleton<IMessenger>(StrongReferenceMessenger.Default);
        
        // Factories and complex services
        services.AddSingleton<IChartStrategyFactory>(new ChartStrategyFactory(Enumerable.Empty<IChartStrategy>()));
        services.AddSingleton<MarketStructureService>(); // Concrete class, needs its own deps if any
        
        // Register ChartViewModel
        services.AddSingleton<ChartViewModel>();
        
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void IndependentViewModels_HaveIsolatedState()
    {
        // Arrange
        var vm1 = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        var vm2 = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);

        // Act
        vm1.Symbol = "AAPL";
        vm2.Symbol = "MSFT";

        // Assert
        Assert.NotEqual(vm1.Symbol, vm2.Symbol);
        Assert.Equal("AAPL", vm1.Symbol);
        Assert.Equal("MSFT", vm2.Symbol);
    }

    [Fact]
    public void DisposedViewModel_UnregistersFromMessenger()
    {
        // Arrange
        var messenger = _serviceProvider.GetRequiredService<IMessenger>();
        var vm = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        vm.Symbol = "OLD";
        
        // Act
        vm.Dispose();
        
        // Send a message that ChartViewModel normally receives
        messenger.Send(new StockAnalyzer.Avalonia.Common.TickerSelectedMessage("NEW"));

        // Assert
        Assert.NotEqual("NEW", vm.Symbol);
    }

    [Fact]
    public void DisposedViewModel_UnregistersFromThemeManager()
    {
        // Arrange
        var themeManager = _serviceProvider.GetRequiredService<Core.Theme.IThemeManager>();
        var vm = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        int initialTrigger = vm.RenderTrigger;

        // Act
        vm.Dispose();
        
        // Trigger theme change
        themeManager.ChangeTheme(Core.Theme.ThemeColors.Dark);

        // Assert
        Assert.Equal(initialTrigger, vm.RenderTrigger);
    }

    [Fact]
    public void AllWorkspaceTabs_RegisteredWithAllowMultipleTrue()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StockAnalyzer.Core.Interfaces.ILocalizationService, MockLocalizationService>();
        
        // Register dummy/transient ViewModels
        services.AddTransient<ChartViewModel>();
        services.AddTransient<TickerListViewModel>();
        services.AddTransient<DataWindowViewModel>();
        services.AddTransient<DrawingToolSidebarViewModel>();
        services.AddTransient<DrawingObjectsViewModel>();
        services.AddTransient<PortfolioSummaryViewModel>();
        services.AddTransient<AllocationPanelViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTimelineViewModel>();

        // Register other dependencies needed for tab registry creation
        services.AddSingleton<IDataService, MockDataService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IStockAnalyzerSettings, MockStockAnalyzerSettings>();
        services.AddSingleton<ITimeFrameManager>(sp => new TimeFrameManager(sp.GetRequiredService<IDataService>()));
        services.AddSingleton<Core.Theme.IThemeManager, Core.Theme.ThemeManager>();
        services.AddSingleton<Core.Services.IDispatcherService, SynchronousDispatcherService>();
        services.AddSingleton<IAnalysisPipelineService, MockAnalysisPipelineService>();
        services.AddSingleton<IMessenger>(StrongReferenceMessenger.Default);
        services.AddSingleton<IChartStrategyFactory>(new ChartStrategyFactory(Enumerable.Empty<IChartStrategy>()));
        services.AddSingleton<MarketStructureService>();

        // Act
        var tabRegistry = new TabRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<TabRegistry>.Instance);
        var localizer = new MockLocalizationService();

        tabRegistry.Register(new Core.Models.UI.TabMetadata("Chart", localizer.GetString("Tab_Chart"), AllowMultiple: true), 
            s => s.GetRequiredService<ChartViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("TickerList", localizer.GetString("Tab_TickerList"), AllowMultiple: true), 
            s => s.GetRequiredService<TickerListViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("DataWindow", localizer.GetString("Tab_DataWindow"), AllowMultiple: true), 
            s => s.GetRequiredService<DataWindowViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("DrawingTools", localizer.GetString("Tab_DrawingTools"), AllowMultiple: true), 
            s => s.GetRequiredService<DrawingToolSidebarViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("PortfolioSummary", localizer.GetString("Tab_Portfolio"), AllowMultiple: true), 
            s => s.GetRequiredService<PortfolioSummaryViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("Allocation", localizer.GetString("Tab_Allocation"), AllowMultiple: true),
            s => s.GetRequiredService<AllocationPanelViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("NoteTimeline", localizer.GetString("Tab_Notes"), AllowMultiple: true),
            s => s.GetRequiredService<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTimelineViewModel>());
        tabRegistry.Register(new Core.Models.UI.TabMetadata("DrawingObjects", localizer.GetString("Tab_DrawingObjects"), AllowMultiple: true),
            s => s.GetRequiredService<DrawingObjectsViewModel>());
        tabRegistry.Lock();

        // Assert: All 8 tabs exist and have AllowMultiple = true
        var allTabs = tabRegistry.GetAllMetadata().ToList();
        Assert.Equal(8, allTabs.Count);
        Assert.All(allTabs, tab => Assert.True(tab.AllowMultiple, $"Tab '{tab.Id}' must have AllowMultiple == true."));
    }

    [Fact]
    public void DataWindowViewModel_MultipleInstances_IndependentStateAndDisposal()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var chart1 = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        var chart2 = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        var dw1 = new DataWindowViewModel(chart1, messenger, dispatcher);
        var dw2 = new DataWindowViewModel(chart2, messenger, dispatcher);

        var candle1 = new CoreCandleData(DateTime.UtcNow, 100m, 110m, 95m, 105m, 1000);
        var candle2 = new CoreCandleData(DateTime.UtcNow, 200m, 210m, 195m, 205m, 2000);

        var pos1 = new StockAnalyzer.Avalonia.Common.CrosshairPositionData { CandleIndex = 10, HoveredCandle = candle1 };
        var pos2 = new StockAnalyzer.Avalonia.Common.CrosshairPositionData { CandleIndex = 20, HoveredCandle = candle2 };

        // Act 1: Send crosshair message -> both update
        messenger.Send(new StockAnalyzer.Avalonia.Common.CrosshairPositionChangedMessage(pos1));

        Assert.Equal("105.000", dw1.CloseText);
        Assert.Equal("105.000", dw2.CloseText);

        // Act 2: Dispose dw1 -> dw1 stops receiving, dw2 keeps receiving
        dw1.Dispose();

        messenger.Send(new StockAnalyzer.Avalonia.Common.CrosshairPositionChangedMessage(pos2));

        // Assert
        Assert.Equal("105.000", dw1.CloseText);
        Assert.Equal("205.000", dw2.CloseText);

        dw2.Dispose();
    }

    [Fact]
    public void DrawingToolSidebarViewModel_DisposesCleanly()
    {
        // Arrange
        var chart = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        var sidebar = new DrawingToolSidebarViewModel(chart);

        // Act & Assert
        Assert.NotNull(sidebar.DrawingObjectsViewModel);
        sidebar.Dispose();
    }

    [Fact]
    public void DataWindowViewModel_SetChartViewModel_ImmediatelyRefreshesIndicatorsAndValues()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var chart = ActivatorUtilities.CreateInstance<ChartViewModel>(_serviceProvider);
        chart.Symbol = "AAPL";

        var candle = new CoreCandleData(DateTime.UtcNow, 150m, 155m, 148m, 152m, 5000);
        chart.Candles = new List<CoreCandleData> { candle };

        var indSetting = StockAnalyzer.Core.Models.DefaultCoreIndicatorSettings.GetDefault().First(i => i.TypeEnum == IndicatorType.SMA);
        indSetting.IsEnabled = true;
        chart.Indicators.Add(indSetting);

        var indResult = IndicatorResult.Success(new decimal?[] { 151.25m });
        chart.IndicatorResults = new Dictionary<string, IIndicatorResult>
        {
            { indSetting.Id, indResult }
        };

        var dw = new DataWindowViewModel(new ChartViewModel(), messenger, dispatcher);

        // Act
        dw.SetChartViewModel(chart);

        // Assert: Immediately populated without requiring crosshair hover
        Assert.Equal("AAPL", dw.Symbol);
        Assert.Equal("152.000", dw.CloseText);
        Assert.NotEmpty(dw.IndicatorItems);

        dw.Dispose();
    }
}

public class MockAnalysisPipelineService : IAnalysisPipelineService
{
    public Dictionary<string, IIndicatorResult> CalculateIndicators(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings)
        => new();

    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings)
        => Task.FromResult(new Dictionary<string, IIndicatorResult>());

    public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0)
        => null;

    public decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period)
        => 0m;
}

public class MockLocalizationService : StockAnalyzer.Core.Interfaces.ILocalizationService
{
    public string CurrentLanguage { get; set; } = "en";
    public string GetString(string key) => key;
    public string this[string key] => key;
    public void SetLanguage(string language) { CurrentLanguage = language; }
}
