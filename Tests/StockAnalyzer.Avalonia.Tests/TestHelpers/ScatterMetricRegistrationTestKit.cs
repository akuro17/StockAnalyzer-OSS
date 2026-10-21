#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>An <see cref="IScatterMetricRegistry"/> the test drives: when it has loaded, what it answers to a replacement, and what it was asked to store.</summary>
public sealed class FakeScatterMetricRegistry : IScatterMetricRegistry
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<ScatterMetricDefinition> _confirmed;

    public FakeScatterMetricRegistry(IEnumerable<ScatterMetricDefinition>? confirmed = null, bool loaded = true)
    {
        _confirmed = (confirmed ?? Enumerable.Empty<ScatterMetricDefinition>()).Select(m => m.Clone()).ToList();
        if (loaded)
        {
            _loaded.SetResult();
        }
    }

    /// <summary>What the next <see cref="ReplaceAllAsync"/> answers; on <see cref="ScatterMetricRegistryResult.Ok"/> the list becomes the confirmed one.</summary>
    public ScatterMetricRegistryResult Result { get; set; } = ScatterMetricRegistryResult.Ok;

    /// <summary>When set, <see cref="ReplaceAllAsync"/> waits for it (the registration window is busy meanwhile).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int DroppedCount { get; set; }

    public List<IReadOnlyList<ScatterMetricDefinition>> ReplaceCalls { get; } = new();

    public Task Loaded => _loaded.Task;

    public void CompleteLoading() => _loaded.TrySetResult();

    public IReadOnlyList<ScatterMetricDefinition> Snapshot() => _confirmed.Select(m => m.Clone()).ToList();

    public int TakeLoadDroppedCount()
    {
        int dropped = DroppedCount;
        DroppedCount = 0;
        return dropped;
    }

    public async Task<ScatterMetricRegistryResult> ReplaceAllAsync(IReadOnlyList<ScatterMetricDefinition> metrics)
    {
        ReplaceCalls.Add(metrics.Select(m => m.Clone()).ToList());
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Result == ScatterMetricRegistryResult.Ok)
        {
            _confirmed = metrics.Select(m => m.Clone()).ToList();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Result;
    }

    public event EventHandler? Changed;
}

/// <summary>Builders for the scatter metric registration window tests.</summary>
public static class ScatterMetricRegistrationTestKit
{
    private static readonly ScreenerCatalogProvider Catalog = new();

    /// <summary>The real settings object, with the scatter section replaced when given (otherwise the shipped defaults).</summary>
    public static StockAnalyzerSettings Settings(ScatterMetricSettings? scatter = null)
        => new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Options.Create(new PythonSettings()),
            Options.Create(new ChartDefaultSettings()),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings()),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()),
            scatterMetricOptions: scatter is null ? null : Options.Create(scatter));

    /// <summary>An Indicator metric as the registration window makes it: the first output series of the catalog and the given Period, if any.</summary>
    public static ScatterMetricDefinition Indicator(IndicatorType type, int offset = 0, int? period = null)
    {
        var source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            OutputName = Catalog.GetOutputSeriesNames(type, IndicatorFactory.Default).First(),
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };
        CoreIndicatorParameterBase? defaults = Catalog.GetDefaultSettings(type, IndicatorFactory.Default)?.ParameterObject;
        if (defaults is not null)
        {
            foreach (KeyValuePair<string, object> pair in StockAnalyzer.Avalonia.ViewModels.IndicatorRegistrationViewModel.BuildParameterDictionary(defaults))
            {
                source.Parameters[pair.Key] = pair.Value;
            }
        }

        if (period is not null)
        {
            source.Parameters["Period"] = period.Value;
        }

        return new ScatterMetricDefinition { Source = source };
    }

    /// <summary>The window, wired like the dialog service wires it, with the doubles it talks to.</summary>
    public sealed class Harness : IDisposable
    {
        public Harness(IScatterMetricRegistry registry, StockAnalyzerSettings settings, bool confirmDiscard = false, FakeDialogService? dialog = null)
        {
            Registry = registry;
            Settings = settings;
            Dialog = dialog ?? new FakeDialogService();
            Dialog.ConfirmationResult = confirmDiscard;
            ViewModel = new ScatterMetricRegistrationViewModel(registry, Catalog, NullLocalizationService.Instance, Toast, Dialog, settings, IndicatorFactory.Default);
            ViewModel.RequestClose = committed => Closed.Add(committed);
        }

        public IScatterMetricRegistry Registry { get; }
        public StockAnalyzerSettings Settings { get; }
        public RecordingToastNotificationService Toast { get; } = new();
        public FakeDialogService Dialog { get; }
        public ScatterMetricRegistrationViewModel ViewModel { get; }

        /// <summary>The argument of every <c>RequestClose</c> call (true = confirmed with OK).</summary>
        public List<bool> Closed { get; } = new();

        public void Dispose() => ViewModel.Dispose();
    }

    /// <summary>A window over a fake registry holding <paramref name="confirmed"/>; the initialisation has finished.</summary>
    public static async Task<(Harness Harness, FakeScatterMetricRegistry Registry)> OpenAsync(
        IEnumerable<ScatterMetricDefinition>? confirmed = null,
        ScatterMetricSettings? scatter = null,
        bool confirmDiscard = false)
    {
        var registry = new FakeScatterMetricRegistry(confirmed);
        var harness = new Harness(registry, Settings(scatter), confirmDiscard);
        await harness.ViewModel.InitializationTask;
        return (harness, registry);
    }

    /// <summary>Selects the Column with the given <c>ColumnMemberName</c> (Close, MarketCap, ...) in "All Filters" and returns it.</summary>
    public static ScreenerCatalogItem SelectColumn(ScatterMetricRegistrationViewModel viewModel, string memberName)
    {
        viewModel.SelectedNavItem = viewModel.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
        ScreenerCatalogItem item = viewModel.CatalogItems.First(i => i.CategoryType == ScreenerItemCategoryType.Column && i.ColumnMemberName == memberName);
        viewModel.SelectedCatalogItem = item;
        return item;
    }

    /// <summary>Selects the catalog item with the given short name in "All Filters" and returns it.</summary>
    public static ScreenerCatalogItem Select(ScatterMetricRegistrationViewModel viewModel, string shortName, ScreenerItemCategoryType category = ScreenerItemCategoryType.Indicator)
    {
        viewModel.SelectedNavItem = viewModel.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
        ScreenerCatalogItem item = viewModel.CatalogItems.First(i => i.CategoryType == category && i.ShortName == shortName);
        viewModel.SelectedCatalogItem = item;
        return item;
    }
}
