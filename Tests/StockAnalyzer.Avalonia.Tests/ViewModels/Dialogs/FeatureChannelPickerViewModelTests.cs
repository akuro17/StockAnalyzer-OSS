using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

/// <summary>In-memory <see cref="ITemplateService"/> double, scoped to <see cref="FeatureSpecTemplate"/>
/// (Tests/StockAnalyzer.Avalonia.Tests.csproj has no Moq reference; mirrors
/// FilterSettingsViewModelTests.FakeTemplateService's shape).</summary>
public class FakeFeatureTemplateService : ITemplateService
{
    public List<FeatureSpecTemplate> Stored { get; } = new();

    public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase
        => Task.FromResult((T?)(object?)Stored.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
        => Task.FromResult((IReadOnlyList<T>)Stored.Cast<T>().ToList());

    public Task SaveAsync<T>(T template) where T : TemplateBase
    {
        if (template is FeatureSpecTemplate featureTemplate)
        {
            var existingIndex = Stored.FindIndex(t => t.Id == featureTemplate.Id);
            if (existingIndex >= 0) Stored[existingIndex] = featureTemplate;
            else Stored.Add(featureTemplate);
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(TemplateType type, Guid id)
        => Task.FromResult(Stored.RemoveAll(t => t.Id == id) > 0);

    public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
        => Task.FromResult(TemplateValidationResult.Success());

    public Task EnsureMigratedAsync() => Task.CompletedTask;
}

/// <summary>
/// Coverage for <see cref="FeatureChannelPickerViewModel"/>, the self-contained composer that
/// produces a <see cref="FeatureSpec"/> for a composed-features training run. Uses the real
/// <see cref="IndicatorFactory"/>.
/// </summary>
public class FeatureChannelPickerViewModelTests
{
    private static FeatureChannelPickerViewModel NewPicker() => new(IndicatorFactory.Default);

    [Fact]
    public void Catalog_IsPopulatedAndFiltersBySearch()
    {
        var vm = NewPicker();
        Assert.NotEmpty(vm.FilteredCatalogItems);

        vm.SearchText = "RSI";
        Assert.All(vm.FilteredCatalogItems, i =>
            Assert.Contains("RSI", i.ShortName + i.DisplayName, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TogglingPriceChannels_AddsAndRemovesRows()
    {
        var vm = NewPicker();

        vm.IncludeClose = true;
        vm.IncludeOpen = true;
        Assert.Equal(2, vm.Channels.Count);
        Assert.True(vm.HasChannels);

        vm.IncludeOpen = false;
        Assert.Single(vm.Channels);
        Assert.Equal(PriceType.Close, vm.Channels[0].Price);
    }

    [Fact]
    public void AddIndicatorChannel_AppendsRowWithCapturedType()
    {
        var vm = NewPicker();
        vm.SearchText = "RSI";
        vm.SelectedCatalogItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.RSI);

        vm.AddIndicatorChannelCommand.Execute(null);

        var row = Assert.Single(vm.Channels);
        Assert.Equal(FeatureChannelKind.Indicator, row.Kind);
        Assert.Equal(IndicatorType.RSI, row.Indicator);
    }

    [Fact]
    public void AddIndicatorChannel_Label_ShowsPeriodEvenAtDefault()
    {
        // The label must read "ShortName (period)" from the moment of Add, before any edit - not just
        // once the user has changed something away from the registry default.
        var vm = NewPicker();
        vm.SelectedCatalogItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.RSI);

        vm.AddIndicatorChannelCommand.Execute(null);

        var row = Assert.Single(vm.Channels);
        Assert.Matches(@"^RSI \(\d+\)$", row.Label);
    }

    [Fact]
    public void BuildFeatureSpec_PreservesOrderAndPerRowNormalization()
    {
        var vm = NewPicker();
        vm.SelectedNormalization = ChannelNormalization.WindowMinMax;
        vm.IncludeClose = true;

        vm.SelectedNormalization = ChannelNormalization.None;
        vm.SelectedCatalogItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.AddIndicatorChannelCommand.Execute(null);

        vm.Channels[0].Normalization = ChannelNormalization.WindowZScore;

        var spec = vm.BuildFeatureSpec();

        Assert.NotNull(spec);
        Assert.True(spec!.IsValid(out _));
        Assert.Equal(2, spec.Channels.Count);
        Assert.Equal(FeatureChannelKind.Price, spec.Channels[0].Kind);
        Assert.Equal(ChannelNormalization.WindowZScore, spec.Channels[0].Normalization);
        Assert.Equal(FeatureChannelKind.Indicator, spec.Channels[1].Kind);
        Assert.Equal(IndicatorType.SMA, spec.Channels[1].Indicator);
    }

    [Fact]
    public void BuildFeatureSpec_NoChannels_ReturnsNull()
        => Assert.Null(NewPicker().BuildFeatureSpec());

    [Fact]
    public void BuildFeatureSpec_StableAcrossCategoryFilterAndClear()
    {
        // The tabbed library surface adds a category filter + "clear category" affordance.
        // Exercising it must not change what the picker composes.
        var vm = NewPicker();
        vm.IncludeClose = true;

        var smaItem = vm.AllCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.SelectedCategory = smaItem.Category;
        vm.SelectedCatalogItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.AddIndicatorChannelCommand.Execute(null);
        vm.ClearCategoryCommand.Execute(null);

        var spec = vm.BuildFeatureSpec();

        Assert.Null(vm.SelectedCategory);
        Assert.NotNull(spec);
        Assert.Equal(2, spec!.Channels.Count);
        Assert.Equal(FeatureChannelKind.Price, spec.Channels[0].Kind);
        Assert.Equal(PriceType.Close, spec.Channels[0].Price);
        Assert.Equal(FeatureChannelKind.Indicator, spec.Channels[1].Kind);
        Assert.Equal(IndicatorType.SMA, spec.Channels[1].Indicator);
    }

    [Fact]
    public void MoveChannel_ReordersRows()
    {
        var vm = NewPicker();
        vm.IncludeOpen = true;
        vm.IncludeClose = true;

        var first = vm.Channels[0];
        vm.MoveChannelDownCommand.Execute(first);

        Assert.Equal(first, vm.Channels[1]);
    }

    // --- templates ---------------------------------------------------------------------------

    [Fact]
    public async Task SaveTemplateCommand_SavesCurrentChannelsAsOneTemplate()
    {
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);
        vm.IncludeClose = true;
        vm.NewTemplateName = "My Recipe";

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        var saved = Assert.Single(templateService.Stored);
        Assert.Equal("My Recipe", saved.Name);
        Assert.Single(saved.Spec.Channels);
        Assert.Equal(PriceType.Close, saved.Spec.Channels[0].Price);
        Assert.Empty(vm.NewTemplateName); // cleared after a successful save
    }

    [Fact]
    public async Task SaveTemplateCommand_OverwritingSelectedTemplate_RefreshesChannelLabelPreview()
    {
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);
        vm.IncludeClose = true;
        vm.NewTemplateName = "Recipe";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        // Select the just-saved template so its preview is populated from the pre-edit snapshot.
        vm.SelectedTemplate = Assert.Single(vm.Templates);
        Assert.Equal(new[] { "Close" }, vm.SelectedTemplateChannelLabels);

        // Edit the channel set and save over the same name (in-place overwrite, same reference).
        vm.IncludeOpen = true;
        vm.NewTemplateName = "Recipe";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.Single(templateService.Stored); // overwrite, not a second template
        Assert.Equal(new[] { "Close", "Open" }, vm.SelectedTemplateChannelLabels); // preview refreshed
    }

    [Fact]
    public void SaveTemplateCommand_CannotExecute_WithoutNameOrChannels()
    {
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, new FakeFeatureTemplateService());

        Assert.False(vm.SaveTemplateCommand.CanExecute(null)); // no name, no channels

        vm.NewTemplateName = "Name only";
        Assert.False(vm.SaveTemplateCommand.CanExecute(null)); // still no channels

        vm.IncludeClose = true;
        Assert.True(vm.SaveTemplateCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadTemplateCommand_ReplacesChannelsAndSyncsPriceToggles()
    {
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);

        // Start from a different selection so Load must actually replace, not append.
        vm.IncludeOpen = true;

        var template = new FeatureSpecTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Close + RSI",
            Spec = new FeatureSpec
            {
                Channels = new List<FeatureChannel>
                {
                    new() { Kind = FeatureChannelKind.Price, Price = PriceType.Close, Normalization = ChannelNormalization.WindowMinMax },
                    new() { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.RSI, Params = new Dictionary<string, string> { ["period"] = "14" } },
                },
            },
        };

        await vm.LoadTemplateCommand.ExecuteAsync(template);

        Assert.Equal(2, vm.Channels.Count);
        Assert.False(vm.IncludeOpen);
        Assert.True(vm.IncludeClose);
        Assert.Equal(IndicatorType.RSI, vm.Channels[1].Indicator);
        Assert.True(vm.HasChannels);
    }

    [Fact]
    public async Task AppendTemplateCommand_AddsWithoutClearingExistingChannels()
    {
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);

        // Pre-existing selection that Append must preserve (unlike Load, which replaces it).
        vm.IncludeOpen = true;
        vm.SelectedCatalogItem = vm.FilteredCatalogItems.First(i => i.Type == IndicatorType.SMA);
        vm.AddIndicatorChannelCommand.Execute(null);
        Assert.Equal(2, vm.Channels.Count);

        var template = new FeatureSpecTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Close + RSI",
            Spec = new FeatureSpec
            {
                Channels = new List<FeatureChannel>
                {
                    new() { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
                    new() { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.RSI },
                },
            },
        };

        await vm.AppendTemplateCommand.ExecuteAsync(template);

        Assert.Equal(4, vm.Channels.Count); // 2 pre-existing + 2 appended, nothing cleared
        Assert.True(vm.IncludeOpen);
        Assert.True(vm.IncludeClose);
        Assert.Contains(vm.Channels, c => c.Kind == FeatureChannelKind.Indicator && c.Indicator == IndicatorType.RSI);
    }

    [Fact]
    public async Task AppendTemplateCommand_SkipsAlreadyIncludedPriceField()
    {
        // Price channels are single-flag-per-field (IncludeOpen etc. is the existence SSoT) - a second
        // row for the same field would desync that flag from the actual Channels list.
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);
        vm.IncludeClose = true;

        var template = new FeatureSpecTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Duplicate Close",
            Spec = new FeatureSpec
            {
                Channels = new List<FeatureChannel> { new() { Kind = FeatureChannelKind.Price, Price = PriceType.Close } },
            },
        };

        await vm.AppendTemplateCommand.ExecuteAsync(template);

        Assert.Single(vm.Channels); // the duplicate Close was skipped, not appended
    }

    [Fact]
    public void PriceCatalogItems_ContainsAllFifteenPriceTypes_InExactPriceTypeOrder()
    {
        var items = FeatureChannelPickerViewModel.PriceCatalogItems;

        Assert.Equal(15, items.Count);
        Assert.Equal(PriceDataHelper.PriceTypeOptions.Count, items.Count);

        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(PriceDataHelper.PriceTypeOptions[i], items[i].Field);
        }

        // Open, High, Low, Close are items 0..3 without duplicates
        Assert.Equal(PriceType.Open, items[0].Field);
        Assert.Equal(PriceType.High, items[1].Field);
        Assert.Equal(PriceType.Low, items[2].Field);
        Assert.Equal(PriceType.Close, items[3].Field);

        // Extended price types follow in exact order
        Assert.Equal(PriceType.Median, items[4].Field);
        Assert.Equal(PriceType.Midpoint, items[5].Field);
        Assert.Equal(PriceType.Typical, items[6].Field);
        Assert.Equal(PriceType.Weighted, items[7].Field);
        Assert.Equal(PriceType.Average, items[8].Field);
        Assert.Equal(PriceType.HeikinAshiOpen, items[9].Field);
        Assert.Equal(PriceType.HeikinAshiHigh, items[10].Field);
        Assert.Equal(PriceType.HeikinAshiLow, items[11].Field);
        Assert.Equal(PriceType.HeikinAshiClose, items[12].Field);
        Assert.Equal(PriceType.TrueHigh, items[13].Field);
        Assert.Equal(PriceType.TrueLow, items[14].Field);

        // Assert distinct
        Assert.Equal(15, items.Select(x => x.Field).Distinct().Count());
    }

    [Fact]
    public void AddPriceChannel_AddsExtendedPriceTypes_AndPreventsDuplicates()
    {
        var vm = NewPicker();

        // 1. Select and add Median
        vm.SelectedPriceField = FeatureChannelPickerViewModel.PriceCatalogItems.First(i => i.Field == PriceType.Median);
        Assert.True(vm.AddPriceChannelCommand.CanExecute(null));
        vm.AddPriceChannelCommand.Execute(null);

        Assert.Single(vm.Channels);
        Assert.Equal(PriceType.Median, vm.Channels[0].Price);
        Assert.False(vm.AddPriceChannelCommand.CanExecute(null)); // Cannot add duplicate Median

        // 2. Select and add HeikinAshiClose
        vm.SelectedPriceField = FeatureChannelPickerViewModel.PriceCatalogItems.First(i => i.Field == PriceType.HeikinAshiClose);
        Assert.True(vm.AddPriceChannelCommand.CanExecute(null));
        vm.AddPriceChannelCommand.Execute(null);

        Assert.Equal(2, vm.Channels.Count);
        Assert.Equal(PriceType.HeikinAshiClose, vm.Channels[1].Price);
        Assert.False(vm.AddPriceChannelCommand.CanExecute(null)); // Cannot add duplicate HeikinAshiClose

        // 3. Select and add TrueHigh
        vm.SelectedPriceField = FeatureChannelPickerViewModel.PriceCatalogItems.First(i => i.Field == PriceType.TrueHigh);
        Assert.True(vm.AddPriceChannelCommand.CanExecute(null));
        vm.AddPriceChannelCommand.Execute(null);

        Assert.Equal(3, vm.Channels.Count);
        Assert.Equal(PriceType.TrueHigh, vm.Channels[2].Price);
        Assert.False(vm.AddPriceChannelCommand.CanExecute(null)); // Cannot add duplicate TrueHigh

        // 4. Remove HeikinAshiClose, then verify it can be added again
        var haRow = vm.Channels[1];
        vm.RemoveChannelCommand.Execute(haRow);

        Assert.Equal(2, vm.Channels.Count);
        Assert.DoesNotContain(vm.Channels, c => c.Price == PriceType.HeikinAshiClose);

        vm.SelectedPriceField = FeatureChannelPickerViewModel.PriceCatalogItems.First(i => i.Field == PriceType.HeikinAshiClose);
        Assert.True(vm.AddPriceChannelCommand.CanExecute(null)); // Can add again after removal
    }

    /// <summary>Toast sink that records every message, for asserting user-visible failure feedback.</summary>
    private sealed class RecordingToastService : IToastNotificationService
    {
        public List<string> Messages { get; } = new();
        public string? NotificationMessage { get; private set; }
        public bool IsNotificationVisible { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void ShowNotification(string message)
        {
            Messages.Add(message);
            NotificationMessage = message;
            IsNotificationVisible = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationMessage)));
        }
    }

    /// <summary><see cref="ITemplateService"/> whose <see cref="SaveAsync{T}"/> always faults, to prove
    /// a failed template save is surfaced to the user instead of being swallowed silently (the two
    /// sibling picker ViewModels already show <c>Msg_Template_SaveFailed</c>).</summary>
    private sealed class SaveFaultingTemplateService : ITemplateService
    {
        public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase => Task.FromResult<T?>(null);

        public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
            => Task.FromResult((IReadOnlyList<T>)new List<T>());

        public Task SaveAsync<T>(T template) where T : TemplateBase
            => throw new InvalidOperationException("simulated persistence failure");

        public Task<bool> DeleteAsync(TemplateType type, Guid id) => Task.FromResult(true);

        public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
            => Task.FromResult(TemplateValidationResult.Success());

        public Task EnsureMigratedAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task SaveTemplateCommand_WhenPersistenceFails_ShowsToastAndDoesNotThrowOrClearName()
    {
        var toast = new RecordingToastService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, new SaveFaultingTemplateService(), toast);
        vm.IncludeClose = true;
        vm.NewTemplateName = "Doomed";

        var ex = await Record.ExceptionAsync(() => vm.SaveTemplateCommand.ExecuteAsync(null));

        Assert.Null(ex); // the fault is caught and reported, not propagated to the command caller
        Assert.NotEmpty(toast.Messages); // user gets feedback instead of a silent no-op
        Assert.Equal("Doomed", vm.NewTemplateName); // name is kept so the user can retry
    }

    [Fact]
    public async Task DeleteTemplateCommand_RemovesFromTemplatesAndStore()
    {
        var templateService = new FakeFeatureTemplateService();
        var vm = new FeatureChannelPickerViewModel(IndicatorFactory.Default, templateService);
        vm.IncludeClose = true;
        vm.NewTemplateName = "Disposable";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(vm.Templates);

        await vm.DeleteTemplateCommand.ExecuteAsync(saved);

        Assert.Empty(vm.Templates);
        Assert.Empty(templateService.Stored);
    }
}
