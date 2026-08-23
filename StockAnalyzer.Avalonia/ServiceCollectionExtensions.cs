using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia;

public static class ServiceCollectionExtensions
{
    public static void AddCommonServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bridges ILogger<T> (used throughout ViewModels/Services) to the Serilog sinks configured
        // in Program.cs. Without this, ILogger<T>.LogXxx() calls are silently dropped (no provider
        // attached), while only direct Serilog.Log.* calls reach the log files.
        services.AddLogging(builder => builder.AddSerilog());
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
        services.AddSingleton<Core.Services.Notes.INotesSettingsManager, NotesSettingsManager>();
        services.AddSingleton<Core.Services.IChartSettingsManager, Core.Services.ChartSettingsManager>();
        services.AddSingleton<Core.Interfaces.ITemplateService, Core.Services.TemplateService>();
        services.AddSingleton<Services.Drawing.IChartDrawingRepository, Services.Drawing.ChartDrawingRepository>();
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
        services.AddSingleton<Services.Export.IChartImageExportService, Services.Export.ChartImageExportService>();
        services.AddSingleton<ITickerSyncService, Services.TickerSyncService>();
        services.AddSingleton<Core.Services.IDataService, Core.Services.ParquetDataService>();
        services.AddSingleton<Core.Services.ITimeFrameManager, Services.TimeFrameManager>();
        services.AddSingleton<Services.Drawing.IMagnetSnapService, Services.Drawing.MagnetSnapService>();
        services.AddSingleton<Services.Drawing.ISmartGuideService, Services.Drawing.SmartGuideService>();

        // Registry-driven DrawingSettingsDialog panels: new drawing tools plug in by implementing
        // IDrawingSettingsPanelDefinition and registering an instance here, instead of adding a
        // branch inside DrawingSettingsDialog itself.
        services.AddSingleton<IDrawingSettingsPanelRegistry>(sp =>
        {
            var registry = new DrawingSettingsPanelRegistry();
            registry.Register(new LineTextSettingsPanelDefinition());
            registry.Register(new TextSettingsPanelDefinition());
            registry.Register(new PriceLabelSettingsPanelDefinition());
            registry.Register(new HorizontalLineSettingsPanelDefinition());
            registry.Register(new TrendLineSettingsPanelDefinition());
            registry.Register(new NurbsTrendCurveSettingsPanelDefinition());
            registry.Register(new EllipseSettingsPanelDefinition());
            registry.Register(new EllipseAnnulusSettingsPanelDefinition());
            registry.Register(new NurbsConicShapeSettingsPanelDefinition());
            registry.Register(new NurbsWeightedCurveSettingsPanelDefinition());
            registry.Register(new FixedRangeVolumeProfileSettingsPanelDefinition());
            registry.Register(new BarPatternSettingsPanelDefinition());
            registry.Register(new GeometricPatternSettingsPanelDefinition());
            registry.Register(new HarmonicPatternSettingsPanelDefinition());
            registry.Register(new AutoElliottWaveSettingsPanelDefinition());
            registry.Register(new LongShortPositionSettingsPanelDefinition());
            registry.Register(new PolylineSettingsPanelDefinition());
            registry.Register(new RangeSplineSettingsPanelDefinition());
            registry.Register(new DtwProjectionSettingsPanelDefinition());
            registry.Register(new KalmanFilterProjectionSettingsPanelDefinition());
            registry.Register(new CatenaryCurveSettingsPanelDefinition());
            return registry;
        });
        services.AddTransient<Services.IToastNotificationService, Services.ToastNotificationService>();
        services.AddSingleton<IDetachedWindowFactory, DetachedWindowFactory>();
        services.AddSingleton<ITearOffService, TearOffService>();
        services.AddSingleton<IContainerRegistry, ContainerRegistry>();
        services.AddSingleton<IWindowBoundaryService, WindowBoundaryService>();
        services.AddSingleton<IWindowManagementService, WindowManagementService>();
        services.AddSingleton<IDetachedTabManager, DetachedTabManager>();
        
        // Screener
        services.AddSingleton<Core.Services.IPythonService, Core.Services.PythonService>(); // SmartScreener dependency
        services.AddSingleton<Core.Services.IPatternRecognitionService, Core.Services.PatternRecognitionService>();
        services.AddSingleton<Core.Services.PatternRecognitionService>();
        services.AddSingleton<Core.Services.SmartScreenerService>();
        services.AddSingleton<Core.Services.IScreenerService, Core.Services.ScreenerService>();
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

        // Ticker Notes (SQLite)
        services.AddSingleton<Core.Services.Notes.NoteDatabaseConnectionManager>();
        services.AddSingleton<Core.Services.Notes.NoteSchemaInitializer>();
        services.AddSingleton<Core.Services.Notes.NoteRepository>();
        services.AddSingleton<Core.Services.Notes.AttachmentRepository>();
        services.AddSingleton<Core.Services.UserStrategyMetadataRepository>(_ => Core.Services.UserStrategyMetadataRepository.Instance);
        services.AddSingleton<Core.Services.Notes.TickerMetadataNotesCacheSynchronizer>();
        services.AddSingleton<Core.Services.Notes.OrphanedAttachmentCleanupService>();
        services.AddSingleton<Core.Services.Notes.OrphanedAttachmentScanResultHolder>();

        // Log Viewer
        services.AddSingleton<ILogService, LogService>();
        services.AddTransient<LogViewerViewModel>();

        services.AddTransient<PortfolioSummaryViewModel>();
        services.AddTransient<AllocationPanelViewModel>();
        services.AddSingleton<HeatmapPanelViewModel>();
        services.AddTransient<ScreenerViewModel>();
        services.AddTransient<IndicatorRegistrationViewModel>();
        services.AddSingleton<Core.Interfaces.IScreenerCatalogProvider, Core.Services.ScreenerCatalogProvider>();
        services.AddSingleton<Core.Interfaces.IScreenerValueExtractor, Core.Services.ScreenerValueExtractor>();

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
        services.AddTransient<TickerListViewModel>();
        // Resolved through the shared facade rather than a fresh GetRequiredService<TickerListViewModel>():
        // TickerListViewModel is AddTransient, so an independent resolution here created a second,
        // orphaned instance disconnected from the one MainWindowViewModel/WorkspaceCoordinator and the
        // "TickerList" tab factory operate on (the same class of bug fixed in sa_minimal_fix /
        // FilterRulePersistenceBug for the tab factory below).
        services.AddSingleton<ITickerStateStore>(sp => sp.GetRequiredService<IWorkspaceViewModelFacade>().TickerList);
        services.AddTransient<DataWindowViewModel>();
        services.AddTransient<DrawingToolSidebarViewModel>();
        services.AddTransient<DrawingObjectsViewModel>();

        services.AddSingleton<IWorkspaceViewModelFacade, WorkspaceViewModelFacade>();

        // Resolved through the shared facade rather than the default constructor-injected
        // GetRequiredService<TickerListViewModel>(): MultiSyncProgressViewModel's constructor takes
        // TickerListViewModel directly, and since that type is AddTransient, letting the container
        // auto-resolve it here created a third orphaned instance (same class of bug as the two above).
        // Both call sites (TickerListViewModel.RunSyncSessionAsync, TickerSyncService.SyncSingleTickerAsync)
        // read/write sync settings (IsMetadataSyncEnabled, delay seconds, etc.) through the returned
        // MultiSyncProgressViewModel, so an orphaned TickerListViewModel silently detached those settings
        // from the one the user actually configures and MainWindowViewModel persists.
        services.AddTransient<MultiSyncProgressViewModel>(sp => new MultiSyncProgressViewModel(sp.GetRequiredService<IWorkspaceViewModelFacade>().TickerList));
        
        services.AddWorkspaceTabs();

        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ThemeSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.FontsSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.NotesSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.ChartGeneralSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.SettingsViewModel>();
        services.AddSingleton<StockAnalyzer.Avalonia.ViewModels.Dialogs.PlaceholderSettingsViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.AddTickerViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.DrawingSettingsViewModel>();
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
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.FilterTemplatePickerDialogViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Dialogs.IndicatorSettingsDialogViewModel>();

        // Ticker Notes
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Notes.NoteEditorViewModel>();
        services.AddSingleton<Func<StockAnalyzer.Avalonia.ViewModels.Notes.NoteEditorViewModel>>(sp => () => sp.GetRequiredService<StockAnalyzer.Avalonia.ViewModels.Notes.NoteEditorViewModel>());
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTimelineViewModel>();
        services.AddTransient<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashViewModel>();
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

            // Resolved through the shared facade (not a fresh GetRequiredService<TickerListViewModel>()
            // like the other transient-backed tab factories below use for their own view models):
            // watchlists/filters are global state with exactly one live tree, not per-tab data, so every
            // "TickerList" tab must reflect the same instance MainWindowViewModel/WorkspaceCoordinator
            // restore into. Resolving a fresh transient here silently created an orphaned instance that
            // never received ImportFilterSettings, which is why restored filters never appeared in the
            // visible tab after a restart (sa_minimal_fix / FilterRulePersistenceBug).
            registry.Register(new Core.Models.UI.TabMetadata("TickerList", localizer.GetString("Tab_TickerList"), AllowMultiple: true),
                s => s.GetRequiredService<IWorkspaceViewModelFacade>().TickerList);

            registry.Register(new Core.Models.UI.TabMetadata("DataWindow", localizer.GetString("Tab_DataWindow"), AllowMultiple: true), 
                s => {
                    var dw = s.GetRequiredService<DataWindowViewModel>();
                    var mwvm = s.GetService<MainWindowViewModel>();
                    if (mwvm?.ChartViewModel != null)
                    {
                        dw.SetChartViewModel(mwvm.ChartViewModel);
                    }
                    return dw;
                });

            registry.Register(new Core.Models.UI.TabMetadata("DrawingTools", localizer.GetString("Tab_DrawingTools"), AllowMultiple: true), 
                s => {
                    var sidebar = s.GetRequiredService<DrawingToolSidebarViewModel>();
                    var mwvm = s.GetService<MainWindowViewModel>();
                    if (mwvm?.ChartViewModel != null)
                    {
                        sidebar.SetChartViewModel(mwvm.ChartViewModel);
                    }
                    return sidebar;
                });

            registry.Register(new Core.Models.UI.TabMetadata("PortfolioSummary", localizer.GetString("Tab_Portfolio"), AllowMultiple: true), 
                s => s.GetRequiredService<PortfolioSummaryViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("Allocation", localizer.GetString("Tab_Allocation"), AllowMultiple: true),
                s => s.GetRequiredService<AllocationPanelViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("NoteTimeline", localizer.GetString("Tab_Notes"), AllowMultiple: true),
                s => s.GetRequiredService<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTimelineViewModel>());

            registry.Register(new Core.Models.UI.TabMetadata("DrawingObjects", localizer.GetString("Tab_DrawingObjects"), AllowMultiple: true), 
                s => {
                    var objects = s.GetRequiredService<DrawingObjectsViewModel>();
                    var mwvm = s.GetService<MainWindowViewModel>();
                    if (mwvm?.ChartViewModel != null)
                    {
                        objects.SetChartViewModel(mwvm.ChartViewModel);
                    }
                    return objects;
                });

            // Lock the registry to prevent runtime modifications
            registry.Lock();

            return registry;
        });

        // Register PanelTabFactory (DI will automatically inject ITabRegistry and ILogger<PanelTabFactory>)
        services.AddSingleton<IPanelTabFactory, PanelTabFactory>();
    }
}
