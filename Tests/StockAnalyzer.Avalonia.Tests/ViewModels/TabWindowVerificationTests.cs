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
}

public class MockAnalysisPipelineService : IAnalysisPipelineService
{
    public Dictionary<string, IIndicatorResult> CalculateIndicators(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings)
        => new();

    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings)
        => Task.FromResult(new Dictionary<string, IIndicatorResult>());

    public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false)
        => null;

    public decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period)
        => 0m;
}
