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

    [Fact]
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
}
