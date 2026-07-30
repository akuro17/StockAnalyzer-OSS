using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia;

public static class ServiceCollectionExtensions
{
    public static void AddCommonServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
        services.AddOptions();
        
        // Register configuration itself
        services.AddSingleton<IConfiguration>(configuration);

        // Register configuration options with DI
        services.Configure<ChartOptions>(configuration.GetSection("ChartOptions"));
        services.Configure<ThemeSettings>(configuration.GetSection("ThemeSettings"));
        services.Configure<PythonSettings>(configuration.GetSection("Python"));
        services.Configure<ChartDefaultSettings>(configuration.GetSection("Chart"));
        services.Configure<PredictionSettings>(configuration.GetSection("Prediction"));
        services.Configure<MarketDataSettings>(configuration.GetSection("MarketDataSettings"));
        services.Configure<ScreenerSettings>(configuration.GetSection("Screener"));
        services.Configure<SmartScreenerSettings>(configuration.GetSection("SmartScreener"));
        services.Configure<InfrastructureSettings>(configuration.GetSection("Infrastructure"));
        services.Configure<MarketStructureSettings>(configuration.GetSection("MarketStructure"));
        services.Configure<PatternRecognitionSettings>(configuration.GetSection("PatternRecognition"));
        services.Configure<ResilienceSettings>(configuration.GetSection("Resilience"));
        services.Configure<LocalizationSettings>(configuration.GetSection("Localization"));

        // Register custom settings
        services.AddSingleton<Core.Services.IStockAnalyzerSettings, Services.StockAnalyzerSettings>();
        services.AddSingleton<Core.Interfaces.IWorkspaceSerializationService, Core.Services.WorkspaceSerializationService>();
        services.AddSingleton<Core.Theme.IThemeManager, Core.Theme.ThemeManager>();
        services.AddSingleton<IFontSettingsManager, FontSettingsManager>();
        services.AddSingleton<Core.Services.IChartSettingsManager, Core.Services.ChartSettingsManager>();
        services.AddSingleton<Core.Services.IDispatcherService, Services.DispatcherService>();
        services.AddSingleton<Core.Services.IClipboardService, Services.ClipboardService>();
        services.AddSingleton<Core.Interfaces.ILocalizationService, Services.LocalizationService>();
        services.AddSingleton<Core.Interfaces.IDesignTimeDetector, Services.DesignTimeDetector>();
        services.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);

        // Register Layout State Store and Scheduler
        services.AddSingleton<Core.Models.UI.LayoutStateStore>();
        services.AddSingleton<ILayoutSaveScheduler, LayoutSaveScheduler>();
        
        // Register Workspace Coordinator and Options
        services.Configure<WorkspaceCoordinatorOptions>(configuration.GetSection("Workspace"));
        services.AddSingleton<IWorkspaceCoordinator, WorkspaceCoordinator>();


        // Register Core Indicator Pipeline Dependencies
        services.AddSingleton<Core.Models.Indicators.IIndicatorFactory, Core.Models.Indicators.IndicatorFactory>();

        services.AddSingleton<IDialogService>(sp => new Services.DialogService(sp));
        services.AddSingleton<Core.Services.IDataService, Core.Services.ParquetDataService>();
        services.AddSingleton<Core.Services.ITimeFrameManager, Services.TimeFrameManager>();
        services.AddSingleton<Services.Drawing.IMagnetSnapService, Services.Drawing.MagnetSnapService>();
        services.AddTransient<Services.IToastNotificationService, Services.ToastNotificationService>();
        services.AddSingleton<IDetachedWindowFactory, DetachedWindowFactory>();
        services.AddSingleton<ITearOffService, TearOffService>();
        services.AddSingleton<IContainerRegistry, ContainerRegistry>();
        services.AddSingleton<IWindowBoundaryService, WindowBoundaryService>();
        services.AddSingleton<IWindowManagementService, WindowManagementService>();
        services.AddSingleton<IDetachedTabManager, DetachedTabManager>();
        
        // Screener
        services.AddSingleton<Core.Services.IPythonService, Core.Services.PythonService>(); // SmartScreener dependency
        services.AddSingleton<Core.Services.PatternRecognitionService>();
        services.AddSingleton<Core.Services.SmartScreenerService>();
        services.AddSingleton<Core.Services.ScreenerService>();
        services.AddSingleton<Core.Services.MarketStructureService>();
        services.AddSingleton<Core.Services.IPredictionService, Core.Services.PredictionService>();
        services.AddSingleton<Core.Services.IMLDataProcessor, Core.Services.MLDataProcessor>();
        services.AddSingleton<Core.Services.IComparisonDataAligner, Core.Services.ComparisonDataAligner>();
        services.AddSingleton<Core.Services.Analysis.IAnalysisPipelineService, Core.Services.Analysis.AnalysisPipelineService>();
        services.AddSingleton<Core.Services.SignalOrchestrator>();
        services.AddSingleton<Core.Interfaces.IUserPortfolioRepository, Core.Services.UserPortfolioRepository>();
        services.AddSingleton<Core.Interfaces.IPortfolioManager, Core.Services.PortfolioManager>();
        services.AddSingleton<Core.Interfaces.IWatchlistManager, Core.Services.WatchlistManager>();
        
        // Fast Screening (DuckDB/Parquet)
        services.AddSingleton<Core.Services.DuckDBConnectionManager>();
        services.AddSingleton<Core.Services.IMarketDataProvider, Core.Services.ParquetMarketDataProvider>();
        services.AddSingleton<Core.Interfaces.ITickerImportService, Core.Services.TickerImportService>();
        
        // Log Viewer
        services.AddSingleton<ILogService, LogService>();
        services.AddTransient<LogViewerViewModel>();

        services.AddSingleton<PortfolioSummaryViewModel>();
        services.AddSingleton<AllocationPanelViewModel>();
        services.AddSingleton<HeatmapPanelViewModel>();
        services.AddTransient<ScreenerViewModel>();
        services.AddTransient<IndicatorRegistrationViewModel>();
        services.AddSingleton<Core.Interfaces.IScreenerCatalogProvider, Core.Services.ScreenerCatalogProvider>();

        // Chart Strategies
        services.AddSingleton<Core.Strategies.IChartStrategy, Core.Strategies.KagiChartStrategy>();
        services.AddSingleton<Core.Strategies.IChartStrategy, Core.Strategies.RenkoChartStrategy>();
        services.AddSingleton<Core.Strategies.IChartStrategy, Core.Strategies.PointAndFigureChartStrategy>();
        services.AddSingleton<Core.Strategies.IChartStrategyFactory, Core.Strategies.ChartStrategyFactory>();

        services.AddSingleton<Views.Chart.Renderers.IndicatorRenderer>();

        // Facades
        services.AddSingleton<Core.Interfaces.ICoreServicesFacade, Core.Services.CoreServicesFacade>();

        // Registries
        services.AddSingleton<Core.Models.Watchlist.IWatchlistColumnRegistry, Core.Models.Watchlist.WatchlistColumnRegistryWrapper>();
    }

    public static void AddViewModels(this IServiceCollection services)
    {
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<ChartViewModel>();
        services.AddSingleton<Func<ChartViewModel>>(sp => () => sp.GetRequiredService<ChartViewModel>());
        services.AddSingleton<ConfluenceDashboardViewModel>();
        services.AddSingleton<TickerListViewModel>();
        services.AddSingleton<ITickerStateStore>(sp => sp.GetRequiredService<TickerListViewModel>());
        services.AddSingleton<DataWindowViewModel>();
        services.AddSingleton<DrawingToolSidebarViewModel>();

        services.AddSingleton<IWorkspaceViewModelFacade, WorkspaceViewModelFacade>();

        services.AddTransient<MultiSyncProgressViewModel>();
        
        services.AddWorkspaceTabs();

        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ThemeSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.FontsSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ChartGeneralSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.SettingsViewModel>();
        services.AddSingleton<StockAnalyzer.Avalonia.ViewModels.Dialogs.PlaceholderSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.AddTickerViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.CandlestickSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.OhlcBarSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.RenkoSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.KagiSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.PnfSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.LineSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.AreaSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.HeikinAshiSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ThreeLineBreakSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.RelativePerformanceSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ReverseWatchSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTransactionDialogViewModel>();
        services.AddSingleton<Func<StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTransactionDialogViewModel>>(sp => () => sp.GetRequiredService<StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTransactionDialogViewModel>());
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.FilterSettingsViewModel>();
    }

    private static void AddWorkspaceTabs(this IServiceCollection services)
    {
        // Register ITabRegistry as a singleton resolved with its dependencies
        services.AddSingleton<ITabRegistry>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TabRegistry>>();
            var registry = new TabRegistry(logger);

            var localizer = sp.GetRequiredService<StockAnalyzer.Core.Interfaces.ILocalizationService>();

            // Register default tab types using standard localization keys
            registry.Register(new Core.Models.UI.TabMetadata("Chart", localizer.GetString("Tab_Chart"), AllowMultiple: true), 
                s => s.GetRequiredService<ChartViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("TickerList", localizer.GetString("Tab_TickerList")), 
                s => s.GetRequiredService<TickerListViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("DataWindow", localizer.GetString("Tab_DataWindow")), 
                s => s.GetRequiredService<DataWindowViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("DrawingTools", localizer.GetString("Tab_DrawingTools")), 
                s => s.GetRequiredService<DrawingToolSidebarViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("PortfolioSummary", localizer.GetString("Tab_Portfolio")), 
                s => s.GetRequiredService<PortfolioSummaryViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("Allocation", localizer.GetString("Tab_Allocation")), 
                s => s.GetRequiredService<AllocationPanelViewModel>());

            // Lock the registry to prevent runtime modifications
            registry.Lock();

            return registry;
        });

        // Register PanelTabFactory (DI will automatically inject ITabRegistry and ILogger<PanelTabFactory>)
        services.AddSingleton<IPanelTabFactory, PanelTabFactory>();
    }
}
