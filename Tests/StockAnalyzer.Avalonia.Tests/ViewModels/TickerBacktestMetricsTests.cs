using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Avalonia.Headless.XUnit;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class TickerBacktestMetricsTests
{
    [Fact]
    public void BacktestMetricColumnRegistry_ContainsAll30Definitions()
    {
        var columns = BacktestMetricColumnRegistry.AllColumns;
        var definitions = BacktestMetricCatalog.Definitions;

        Assert.Equal(definitions.Length, columns.Count);
        Assert.True(columns.Count > 0);

        for (int i = 0; i < definitions.Length; i++)
        {
            var def = definitions[i];
            var col = columns[i];

            Assert.Equal(BacktestMetricColumnRegistry.MetricsCategoryName, col.Category);
            Assert.Equal(BacktestMetricColumnRegistry.MetricMemberPrefix + def.Name, col.MemberName);
            Assert.Equal(BacktestMetricColumnRegistry.MetricHeaderPrefix + def.Name, col.HeaderKey);
            Assert.Equal(BacktestMetricColumnRegistry.DefaultMetricColumnWidth, col.Width);
            Assert.Equal(1000 + i, col.Priority);
            Assert.Equal(def.Name, col.DisplayName);
            Assert.Equal(BacktestMetricColumnRegistry.MetricTooltipPrefix + def.Name, col.Description);
        }
    }

    [Fact]
    public void ColumnChooserViewModel_HasMetricsCategory_TrueWhenMetricColumnsProvided()
    {
        var allColumns = WatchlistColumnRegistry.AllColumns
            .Concat(BacktestMetricColumnRegistry.AllColumns)
            .ToList();

        var activeNames = new List<string> { "Symbol", "Close" };
        var vm = new ColumnChooserViewModel(allColumns, activeNames);

        Assert.True(vm.HasMetricsCategory);

        vm.SelectedCategory = ColumnCategory.Metrics;
        Assert.Equal(BacktestMetricCatalog.Definitions.Length, vm.FilteredItems.Count);
        Assert.All(vm.FilteredItems, item => Assert.Equal(ColumnCategory.Metrics, item.Category));
    }

    [Fact]
    public void ColumnChooserViewModel_HasMetricsCategory_FalseWhenOnlyWatchlistColumnsProvided()
    {
        // Screener usage: passes only WatchlistColumnRegistry.AllColumns without Metrics
        var allColumns = WatchlistColumnRegistry.AllColumns;
        var activeNames = new List<string> { "Symbol", "Close" };
        var vm = new ColumnChooserViewModel(allColumns, activeNames);

        Assert.False(vm.HasMetricsCategory);
    }

    [Fact]
    public void TickerBacktestTemplateSelectorViewModel_InitialState_IsDefaultEntry()
    {
        var dispatcher = new SynchronousDispatcherService();
        using var selector = new TickerBacktestTemplateSelectorViewModel(
            templateService: null,
            defaultEntryName: "Backtest Template",
            dispatcher: dispatcher);

        Assert.NotNull(selector.SelectedEntry);
        Assert.Equal(Guid.Empty, selector.SelectedEntry.Id);
        Assert.Equal("Backtest Template", selector.SelectedEntry.Name);
        Assert.True(selector.IsDefaultEntry(selector.SelectedEntry));
        Assert.Single(selector.Entries);
    }

    [Fact]
    public void TickerBacktestTemplateSelectorViewModel_ResetToDefault_RestoresSentinel()
    {
        var dispatcher = new SynchronousDispatcherService();
        using var selector = new TickerBacktestTemplateSelectorViewModel(
            templateService: null,
            defaultEntryName: "Backtest Template",
            dispatcher: dispatcher);

        var customTemplate = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "My Strategy"
        };
        selector.Entries.Add(customTemplate);
        selector.SelectedEntry = customTemplate;

        Assert.False(selector.IsDefaultEntry(selector.SelectedEntry));

        selector.ResetToDefault();
        Assert.True(selector.IsDefaultEntry(selector.SelectedEntry));
        Assert.Equal(Guid.Empty, selector.SelectedEntry!.Id);
    }

    [Fact]
    public async Task BacktestExecution_WithConditionTemplate_GeneratesValidMetrics()
    {
        var templateService = new StockAnalyzer.Core.Services.TemplateService();
        var templates = await templateService.GetAllAsync<BacktestConditionTemplate>(TemplateType.BacktestCondition);
        Assert.NotEmpty(templates);

        var template = templates[0];
        Assert.NotNull(template.ConditionTree);

        var domainTree = StockAnalyzer.Core.Services.Backtest.Configuration.BacktestConditionTreeDtoMapper.ToTree(template.ConditionTree);
        Assert.True(domainTree.HasAnyLeaf);

        var strategy = StockAnalyzer.Core.Services.Backtest.Engine.ConditionBasedBacktestStrategy.FromTree(domainTree, null);
        var engine = new StockAnalyzer.Core.Services.Backtest.Engine.BacktestEngine(new IndicatorFactory());
        var reportGen = new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportGenerator();

        // Create 100 sample candles
        var now = DateTime.UtcNow.Date;
        var startUtc = now.AddDays(-100);
        var endUtc = now;
        var candles = new List<CandleData>();
        decimal price = 100m;
        for (int i = 0; i < 100; i++)
        {
            price += (i % 2 == 0 ? 1.5m : -1.0m);
            candles.Add(new CandleData(startUtc.AddDays(i), price, price + 1m, price - 1m, price, 1000));
        }

        var rawBars = System.Collections.Immutable.ImmutableArray.CreateRange(candles);
        var input = new BacktestInput(rawBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, startUtc, endUtc, 0, 0);
        var config = new BacktestConfiguration
        {
            ExecutionModel = ExecutionModel.Legacy,
            InitialCapital = 100_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            SizingModel = PositionSizingModel.PercentOfEquity,
            SizingParameter = 0.1m
        };

        var result = engine.Run(input, config, strategy);
        Assert.Equal(RunStatus.Completed, result.Status);

        // Test with current reportOptions without AnnualPeriods:
        var reportOptions = new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportOptions(TimeFrame.D1, 0, startUtc, endUtc);
        
        // This throws because AnnualPeriods is not set!
        var ex = Record.Exception(() => reportGen.Generate(result, reportOptions));
        Assert.NotNull(ex);
        Assert.IsType<ArgumentOutOfRangeException>(ex);
        Assert.Contains("AnnualPeriods", ex.Message);

        // Now test with AnnualPeriods set properly:
        var validReportOptions = new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportOptions(TimeFrame.D1, 0, startUtc, endUtc)
        {
            AnnualPeriods = 252
        };
        var report = reportGen.Generate(result, validReportOptions);
        Assert.NotNull(report);
        Assert.True(report.TotalTrades >= 0);
    }

    [Fact]
    public void WatchlistItemViewModel_MetricIndexerAndNotification_WorksReactively()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);

        var notifiedProperties = new List<string?>();
        item.PropertyChanged += (s, e) => notifiedProperties.Add(e.PropertyName);

        // Initially null
        Assert.Null(item["Metric_TotalPnL"]);

        // Set value
        item.SetMetricValue("Metric_TotalPnL", 12345.67m);
        Assert.Equal(12345.67m, item["Metric_TotalPnL"]);
        Assert.Contains("Item[]", notifiedProperties);
        Assert.Contains("Metric_TotalPnL", notifiedProperties);
        Assert.Contains(string.Empty, notifiedProperties);

        // Reset tracking
        notifiedProperties.Clear();

        // Clear values
        item.ClearMetricValues();
        Assert.Null(item["Metric_TotalPnL"]);
        Assert.Contains("Item[]", notifiedProperties);
        Assert.Contains("Metric_TotalPnL", notifiedProperties);
        Assert.Contains(string.Empty, notifiedProperties);
    }

    [AvaloniaFact]
    public void MetricBinding_OnTextBlock_ReactsToSetMetricValue()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);
        var tb = new global::Avalonia.Controls.TextBlock();
        tb.DataContext = item;

        var def = BacktestMetricCatalog.Definitions.FirstOrDefault(d => d.Name == "TotalPnL");
        var textConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(def);
        tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_TotalPnL]")
        {
            Converter = textConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });

        // Initial check:
        Assert.Equal("-", tb.Text);

        // Update value:
        item.SetMetricValue("Metric_TotalPnL", 100m);
        Assert.Equal("100.0000", tb.Text);

        // Clear value:
        item.ClearMetricValues();
        Assert.Equal("-", tb.Text);
    }

    [Fact]
    public void WatchlistItemViewModel_SetMetricValues_BulkUpdatesWithSingleEmptyNotification()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);

        var notifiedProperties = new List<string?>();
        item.PropertyChanged += (s, e) => notifiedProperties.Add(e.PropertyName);

        var metrics = new Dictionary<string, object?>
        {
            ["Metric_TotalPnL"] = 500m,
            ["Metric_SharpeRatio"] = 1.85m,
            ["Metric_MaxDrawdown"] = -0.12m
        };

        item.SetMetricValues(metrics);

        Assert.Equal(500m, item["Metric_TotalPnL"]);
        Assert.Equal(1.85m, item["Metric_SharpeRatio"]);
        Assert.Equal(-0.12m, item["Metric_MaxDrawdown"]);

        // string.Empty should be notified exactly once for the whole bulk update
        Assert.Equal(1, notifiedProperties.Count(p => p == string.Empty));
        Assert.Contains("Metric_TotalPnL", notifiedProperties);
        Assert.Contains("Metric_SharpeRatio", notifiedProperties);
        Assert.Contains("Metric_MaxDrawdown", notifiedProperties);
    }

    [AvaloniaFact]
    public void MetricBinding_WithValidMetricValue_DisplaysFormattedDecimal()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);
        var tb = new global::Avalonia.Controls.TextBlock();
        tb.DataContext = item;

        var def = BacktestMetricCatalog.Definitions.FirstOrDefault(d => d.Name == "ProfitFactor");
        Assert.NotNull(def);
        var textConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(def);
        tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
        {
            Converter = textConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });

        // Initial state before backtest evaluation: "-"
        Assert.Equal("-", tb.Text);

        // Evaluated with Valid status
        var validMv = MetricValue.Valid(2.5m, MetricUnit.Dimensionless);
        item.SetMetricValue("Metric_ProfitFactor", validMv);

        Assert.Equal("2.5000", tb.Text);
        Assert.Equal(validMv, item["Metric_ProfitFactor"]);
        Assert.Equal(2.5m, item.GetRawValue("Metric_ProfitFactor"));
    }

    [AvaloniaFact]
    public void MetricBinding_WithPositiveInfinityMetricValue_DisplaysNotAvailable()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);
        var tb = new global::Avalonia.Controls.TextBlock();
        tb.DataContext = item;

        var def = BacktestMetricCatalog.Definitions.FirstOrDefault(d => d.Name == "ProfitFactor");
        Assert.NotNull(def);
        var textConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(def);
        tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
        {
            Converter = textConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });

        // When all trades win and GrossLoss == 0, status is PositiveInfinity
        var infMv = MetricValue.NonValid(MetricStatus.PositiveInfinity, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);
        item.SetMetricValue("Metric_ProfitFactor", infMv);

        // Should display N/A (or localized NotAvailable), not "-"
        Assert.NotEqual("-", tb.Text);
        Assert.Equal(infMv, item["Metric_ProfitFactor"]);
        Assert.Equal(decimal.MaxValue, item.GetRawValue("Metric_ProfitFactor"));
    }

    [AvaloniaFact]
    public void MetricBinding_WithInsufficientDataMetricValue_DisplaysNotAvailable()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);
        var tb = new global::Avalonia.Controls.TextBlock();
        tb.DataContext = item;

        var def = BacktestMetricCatalog.Definitions.FirstOrDefault(d => d.Name == "ProfitFactor");
        Assert.NotNull(def);
        var textConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(def);
        tb.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
        {
            Converter = textConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });

        // When trade count is 0, status is InsufficientData(NoClosedTrades)
        var noTradesMv = MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.NoClosedTrades);
        item.SetMetricValue("Metric_ProfitFactor", noTradesMv);

        // Should display N/A (or localized NotAvailable), not "-"
        Assert.NotEqual("-", tb.Text);
        Assert.Equal(noTradesMv, item["Metric_ProfitFactor"]);
        Assert.Null(item.GetRawValue("Metric_ProfitFactor"));
    }

    [AvaloniaFact]
    public async Task EvaluateBacktestTemplate_AaplTemplateAa_ProducesMatchingMetrics()
    {
        var dailyPath = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Daily");
        var dbManager = new StockAnalyzer.Core.Services.DuckDBConnectionManager();
        var dataService = new StockAnalyzer.Core.Services.ParquetDataService(dbManager, Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath }));

        var templateService = new StockAnalyzer.Core.Services.TemplateService();
        var template = await templateService.GetAsync<BacktestConditionTemplate>(TemplateType.BacktestCondition, Guid.Parse("40625a3c-e1c1-47f1-ac39-bbd3efc68a1e"));
        Assert.NotNull(template);

        var domainTree = StockAnalyzer.Core.Services.Backtest.Configuration.BacktestConditionTreeDtoMapper.ToTree(template.ConditionTree);
        var engine = new StockAnalyzer.Core.Services.Backtest.Engine.BacktestEngine(new IndicatorFactory());
        var reportGen = new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportGenerator();

        // A fixed evaluation window: with a window that moves with the clock the profit factor changes
        // (0.0520 for a start on or before 2025-10-10, 0.0510 from 2025-10-11), so the expectation below
        // would silently expire.
        var windowStart = new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var prepService = new StockAnalyzer.Avalonia.Services.BacktestRunPreparationService(dataService);
        var snapshot = new StockAnalyzer.Avalonia.Services.BacktestRunSnapshot(
            "AAPL",
            TimeFrame.D1,
            windowStart,
            windowEnd,
            new BacktestConfiguration
            {
                ExecutionModel = ExecutionModel.Legacy,
                InitialCapital = 1_000_000m,
                SizingModel = PositionSizingModel.FixedQuantity,
                SizingParameter = 1m
            },
            System.Collections.Immutable.ImmutableArray<BacktestConditionEntry>.Empty,
            null,
            System.Collections.Immutable.ImmutableArray<StrategyIndicatorRequest>.Empty,
            BacktestReportDefaults.BuiltIn,
            42,
            2000,
            domainTree);

        var prepared = await prepService.PrepareAsync(snapshot, default);
        var evalService = new StockAnalyzer.Core.Services.Backtest.Evaluation.BacktestEvaluationService(
            engine,
            reportGen,
            new StockAnalyzer.Core.Services.Backtest.Evaluation.NoSamplingEvidenceTrustPolicy());

        var artifact = evalService.Evaluate(prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions);
        Assert.Equal(RunStatus.Completed, artifact.Result.Status);
        Assert.NotNull(artifact.Report);

        var report = artifact.Report;

        // Verify ProfitFactor is valid and formats to 0.0520
        Assert.Equal(MetricStatus.Valid, report.ProfitFactor.Status);
        Assert.Equal(0.0520m, Math.Round(report.ProfitFactor.Value!.Value, 4));

        // Verify AnnualizedSharpe is Valid
        Assert.Equal(MetricStatus.Valid, report.AnnualizedSharpe.Status);

        // Verify UI mapping and formatting via WatchlistItemViewModel
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", "Tech", "Consumer", 150m, 155m, 149m, 153m, 1000000, 2.0);
        var metricDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in BacktestMetricCatalog.Definitions)
        {
            var mv = def.Select(report);
            if (mv != null)
            {
                metricDict[BacktestMetricColumnRegistry.MetricMemberPrefix + def.Name] = mv.Value;
            }
        }
        item.SetMetricValues(metricDict);

        // Verify ProfitFactor cell
        var pfDef = BacktestMetricCatalog.Definitions.First(d => d.Name == "ProfitFactor");
        var pfConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(pfDef);
        var tbPf = new global::Avalonia.Controls.TextBlock { DataContext = item };
        tbPf.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
        {
            Converter = pfConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });
        Assert.Equal("0.0520", tbPf.Text);
        Assert.Equal(report.ProfitFactor.Value, item.GetRawValue("Metric_ProfitFactor"));

        // Verify AnnualizedSharpe cell
        var sharpeDef = BacktestMetricCatalog.Definitions.First(d => d.Name == "AnnualizedSharpe");
        var sharpeConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(sharpeDef);
        var tbSharpe = new global::Avalonia.Controls.TextBlock { DataContext = item };
        tbSharpe.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_AnnualizedSharpe]")
        {
            Converter = sharpeConverter,
            TargetNullValue = "-",
            FallbackValue = "-"
        });
        Assert.NotEqual("-", tbSharpe.Text);
        Assert.Equal(report.AnnualizedSharpe.Value, item.GetRawValue("Metric_AnnualizedSharpe"));
    }

    [AvaloniaFact]
    public async Task TemplateBB_JapaneseWatchlistTickers_CalculatesAndDisplaysProfitFactor()
    {
        var dailyPath = StockAnalyzer.Core.Common.PathDiscovery.ResolveDataPath(null, "Data/Daily");
        var dbManager = new StockAnalyzer.Core.Services.DuckDBConnectionManager();
        var dataService = new StockAnalyzer.Core.Services.ParquetDataService(dbManager, Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath }));
        var templateService = new StockAnalyzer.Core.Services.TemplateService();
        var template = await templateService.GetAsync<BacktestConditionTemplate>(TemplateType.BacktestCondition, Guid.Parse("c04296a3-2f62-4742-bc81-5ed2bd55e807"));
        if (template == null) return;

        var domainTree = StockAnalyzer.Core.Services.Backtest.Configuration.BacktestConditionTreeDtoMapper.ToTree(template.ConditionTree);
        var engine = new StockAnalyzer.Core.Services.Backtest.Engine.BacktestEngine(new IndicatorFactory());
        var reportGen = new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReportGenerator();
        var today = DateTime.UtcNow.Date;
        var prepService = new StockAnalyzer.Avalonia.Services.BacktestRunPreparationService(dataService);
        var evalService = new StockAnalyzer.Core.Services.Backtest.Evaluation.BacktestEvaluationService(
            engine,
            reportGen,
            new StockAnalyzer.Core.Services.Backtest.Evaluation.NoSamplingEvidenceTrustPolicy());

        var defaultSettingsManager = new StockAnalyzer.Core.Services.Backtest.Configuration.BacktestDefaultSettingsManager();
        var defaultLoad = await defaultSettingsManager.LoadAsync();
        var defaultSettings = defaultLoad.Settings;
        var (startUtc, endUtc, resolvedFrame) = defaultSettings.ResolveTargetDataAndPeriod();

        string[] symbols = new[] { "3382-T", "2681-T", "3036-T", "7741-T", "6098-T", "6367-T" };
        var evaluatedCount = 0;

        foreach (var sym in symbols)
        {
            var snapshot = new StockAnalyzer.Avalonia.Services.BacktestRunSnapshot(
                sym,
                resolvedFrame,
                startUtc,
                endUtc,
                new BacktestConfiguration
                {
                    ExecutionModel = ExecutionModel.Legacy,
                    InitialCapital = defaultSettings.InitialCapital,
                    SizingModel = defaultSettings.SizingModel,
                    SizingParameter = defaultSettings.SizingParameter
                },
                System.Collections.Immutable.ImmutableArray<BacktestConditionEntry>.Empty,
                null,
                System.Collections.Immutable.ImmutableArray<StrategyIndicatorRequest>.Empty,
                BacktestReportDefaults.BuiltIn,
                42,
                2000,
                domainTree);

            var prepared = await prepService.PrepareAsync(snapshot, default);
            var artifact = evalService.Evaluate(prepared.Input, prepared.Configuration, prepared.StrategySpecification, prepared.ReportOptions);
            var report = artifact.Report;
            if (report != null)
            {
                evaluatedCount++;
                var item = new WatchlistItemViewModel(sym, "Name", "Sec", "Ind", 100, 105, 95, 102, 1000, 1.0);
                var metricDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var def in BacktestMetricCatalog.Definitions)
                {
                    var mv = def.Select(report);
                    if (mv != null)
                    {
                        metricDict[BacktestMetricColumnRegistry.MetricMemberPrefix + def.Name] = mv.Value;
                    }
                }
                item.SetMetricValues(metricDict);

                var pfDef = BacktestMetricCatalog.Definitions.First(d => d.Name == "ProfitFactor");
                var pfTextConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueTextConverter(pfDef);
                var pfBrushConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueBrushConverter(pfDef);

                var tbPf = new global::Avalonia.Controls.TextBlock { DataContext = item };
                tbPf.Bind(global::Avalonia.Controls.TextBlock.TextProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
                {
                    Converter = pfTextConverter,
                    TargetNullValue = "-",
                    FallbackValue = "-"
                });
                tbPf.Bind(global::Avalonia.Controls.TextBlock.ForegroundProperty, new global::Avalonia.Data.Binding("[Metric_ProfitFactor]")
                {
                    Converter = pfBrushConverter,
                    TargetNullValue = global::Avalonia.AvaloniaProperty.UnsetValue,
                    FallbackValue = global::Avalonia.AvaloniaProperty.UnsetValue
                });

                Assert.Equal(MetricStatus.Valid, report.ProfitFactor.Status);
                Assert.NotEqual("-", tbPf.Text);
                Assert.NotEqual("N/A", tbPf.Text);
                Assert.True(decimal.TryParse(tbPf.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedPf));
                Assert.True(parsedPf > 0m);

                // Critical: Neutral metrics must return UnsetValue so TextBlock.Foreground is NOT set to null (which makes text invisible)
                var brushPf = pfBrushConverter.Convert(item["Metric_ProfitFactor"], typeof(global::Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(global::Avalonia.AvaloniaProperty.UnsetValue, brushPf);
                Assert.False(tbPf.IsSet(global::Avalonia.Controls.TextBlock.ForegroundProperty));

                // Verify GainMagnitude metric (GrossProfit) sets explicit colored brush
                var gpDef = BacktestMetricCatalog.Definitions.First(d => d.Name == "GrossProfit");
                var gpBrushConverter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueBrushConverter(gpDef);
                var brushGp = gpBrushConverter.Convert(item["Metric_GrossProfit"], typeof(global::Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotNull(brushGp);
                Assert.NotEqual(global::Avalonia.AvaloniaProperty.UnsetValue, brushGp);
            }
        }

        Assert.Equal(symbols.Length, evaluatedCount);
    }

    [AvaloniaFact]
    public void MetricValueBrushConverter_NeutralMetricsAndNull_ReturnUnsetValue()
    {
        var neutralDefs = BacktestMetricCatalog.Definitions.Where(d => d.Rule == BacktestMetricSemanticRule.Neutral).ToList();
        Assert.NotEmpty(neutralDefs);

        foreach (var def in neutralDefs)
        {
            var converter = new StockAnalyzer.Avalonia.Common.TreeDataGridExtensions.MetricValueBrushConverter(def);

            // Null value
            var resultNull = converter.Convert(null, typeof(global::Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(global::Avalonia.AvaloniaProperty.UnsetValue, resultNull);

            // Valid decimal value (e.g. ProfitFactor = 1.2345)
            var validMv = MetricValue.Valid(1.2345m, MetricUnit.Dimensionless);
            var resultValid = converter.Convert(validMv, typeof(global::Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(global::Avalonia.AvaloniaProperty.UnsetValue, resultValid);

            // Boxed decimal directly
            var resultDecimal = converter.Convert(1.2345m, typeof(global::Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(global::Avalonia.AvaloniaProperty.UnsetValue, resultDecimal);
        }
    }
}


