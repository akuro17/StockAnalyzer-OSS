using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public class BacktestConditionTemplateCrudTests
{
    private sealed class InMemoryBacktestTemplateService : ITemplateService
    {
        public List<BacktestConditionTemplate> Templates { get; } = new();

        public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase
        {
            var match = Templates.FirstOrDefault(t => t.Id == id);
            return Task.FromResult((T?)(object?)match);
        }

        public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
        {
            var list = Templates.Cast<T>().ToList();
            return Task.FromResult<IReadOnlyList<T>>(list);
        }

        public Task SaveAsync<T>(T template) where T : TemplateBase
        {
            if (template is BacktestConditionTemplate bct)
            {
                var idx = Templates.FindIndex(t => t.Id == bct.Id);
                if (idx >= 0) Templates[idx] = bct;
                else Templates.Add(bct);
            }
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(TemplateType type, Guid id)
        {
            var removed = Templates.RemoveAll(t => t.Id == id) > 0;
            return Task.FromResult(removed);
        }

        public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
        {
            return Task.FromResult(TemplateValidationResult.Success());
        }

        public Task EnsureMigratedAsync() => Task.CompletedTask;
    }

    private static (BacktestIndicatorSelectionViewModel Vm, InMemoryBacktestTemplateService TemplateService, FakeToastNotificationService Toast) CreateViewModel()
    {
        var catalogProvider = new FakeScreenerCatalogProvider(new ScreenerCatalogItem[]
        {
            new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
            new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Volume", ShortName = "VOL", DisplayName = "Volume", IndicatorType = IndicatorType.Volume },
            new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "Trend", ShortName = "ADX", DisplayName = "Average Directional Index", IndicatorType = IndicatorType.ADX },
        });

        var templateService = new InMemoryBacktestTemplateService();
        var toast = new FakeToastNotificationService();

        var vm = new BacktestIndicatorSelectionViewModel(
            catalogProvider,
            NullLocalizationService.Instance,
            toastService: toast,
            templateService: templateService);

        return (vm, templateService, toast);
    }

    [Fact]
    public void Catalog_IncludesVolumeFollowedBySeparatorAndTemplates()
    {
        var (vm, _, _) = CreateViewModel();

        var groups = vm.NavGroups;
        int volumeIndex = -1;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].DisplayName == "Volume")
            {
                volumeIndex = i;
                break;
            }
        }

        Assert.True(volumeIndex >= 0, "Volume category should exist in nav groups.");
        Assert.True(volumeIndex + 2 < groups.Count, "Nav groups must have separator and templates after Volume.");

        var separator = groups[volumeIndex + 1];
        Assert.True(separator.IsSeparator);
        Assert.False(separator.IsSelectable);

        var templates = groups[volumeIndex + 2];
        Assert.True(templates.IsTemplates);
        Assert.True(templates.IsSelectable);
        Assert.Equal("Library_Templates", templates.DisplayName);
    }

    [Fact]
    public void SelectingTemplates_TogglesVisibilityProperties_AndLoadsTemplates()
    {
        var (vm, templateService, _) = CreateViewModel();

        templateService.Templates.Add(new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Pre-existing Template",
            ConditionTree = new BacktestConditionTreeDto()
        });

        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        var volumeItem = vm.NavGroups.First(g => g.DisplayName == "Volume");

        // Initially not on Templates
        Assert.False(vm.IsTemplatesCategory);
        Assert.True(vm.IsNotTemplatesCategory);

        // Switch to Templates
        vm.SelectedGroupItem = templatesItem;
        Assert.True(vm.IsTemplatesCategory);
        Assert.False(vm.IsNotTemplatesCategory);
        Assert.Single(vm.ConditionTemplates);
        Assert.Equal("Pre-existing Template", vm.ConditionTemplates[0].Name);

        // Switch back to Volume
        vm.SelectedGroupItem = volumeItem;
        Assert.False(vm.IsTemplatesCategory);
        Assert.True(vm.IsNotTemplatesCategory);
    }

    [Fact]
    public async Task SaveTemplateCommand_WithEmptyName_DoesNotSave()
    {
        var (vm, templateService, toast) = CreateViewModel();
        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        vm.SelectedGroupItem = templatesItem;

        vm.NewTemplateName = "   ";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.Empty(templateService.Templates);
        Assert.Empty(vm.ConditionTemplates);
    }

    [Fact]
    public async Task SaveTemplateCommand_WithNoConditions_DoesNotSave()
    {
        var (vm, templateService, toast) = CreateViewModel();
        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        vm.SelectedGroupItem = templatesItem;

        vm.NewTemplateName = "Valid Name";
        // Tree has 0 conditions
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.Empty(templateService.Templates);
        Assert.Empty(vm.ConditionTemplates);
        Assert.NotNull(toast.NotificationMessage);
    }

    [Fact]
    public async Task SaveTemplateCommand_WithValidNameAndConditions_SavesTemplateAndUpdatesSelection()
    {
        var (vm, templateService, toast) = CreateViewModel();

        // Add a condition to the tree first
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m
        };
        vm.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, entry);

        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        vm.SelectedGroupItem = templatesItem;

        vm.NewTemplateName = "Breakout Strategy";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.Single(templateService.Templates);
        Assert.Equal("Breakout Strategy", templateService.Templates[0].Name);
        Assert.Single(vm.ConditionTemplates);
        Assert.Equal("Breakout Strategy", vm.ConditionTemplates[0].Name);
        Assert.Equal(string.Empty, vm.NewTemplateName);
        Assert.Same(vm.ConditionTemplates[0], vm.SelectedConditionTemplate);
        Assert.NotNull(toast.NotificationMessage);
    }

    [Fact]
    public async Task LoadTemplateCommand_ReplacesActiveTree()
    {
        var (vm, _, toast) = CreateViewModel();

        // Create leaf condition in a template
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 150m
        };
        var leaf = new BacktestConditionLeaf(entry);
        var entryLongGroup = new BacktestConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IBacktestConditionNode>(leaf));

        var tree = new BacktestConditionTree(
            entryLongGroup,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd);

        var template = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "SMA Filter",
            ConditionTree = BacktestConditionTreeDtoMapper.ToDto(tree)
        };

        // Initially active tree has 0 conditions
        Assert.Equal(0, vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);

        // Load template
        await vm.LoadTemplateCommand.ExecuteAsync(template);

        // Active tree now has 1 condition under EntryLong
        Assert.Equal(1, vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);
        Assert.NotNull(toast.NotificationMessage);
    }

    [Fact]
    public async Task AppendTemplateCommand_MergesConditionsIntoActiveTree()
    {
        var (vm, _, toast) = CreateViewModel();

        // 1. First add a condition to the active tree
        var initialEntry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.Volume },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1000m
        };
        vm.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, initialEntry);
        Assert.Equal(1, vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);

        // 2. Prepare template with a different condition
        var templateEntry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m
        };
        var templateTree = new BacktestConditionTree(
            new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(templateEntry))),
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd);

        var template = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Template to Append",
            ConditionTree = BacktestConditionTreeDtoMapper.ToDto(templateTree)
        };

        // 3. Append template
        await vm.AppendTemplateCommand.ExecuteAsync(template);

        // Active tree should now have 2 conditions
        Assert.Equal(2, vm.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long).Children.Count);
        Assert.NotNull(toast.NotificationMessage);
    }

    [Fact]
    public async Task DeleteTemplateCommand_RemovesTemplateAndClearsSelection()
    {
        var (vm, templateService, toast) = CreateViewModel();
        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        vm.SelectedGroupItem = templatesItem;

        var template = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "To Delete",
            ConditionTree = new BacktestConditionTreeDto()
        };
        templateService.Templates.Add(template);
        vm.ConditionTemplates.Add(template);
        vm.SelectedConditionTemplate = template;

        await vm.DeleteTemplateCommand.ExecuteAsync(template);

        Assert.Empty(templateService.Templates);
        Assert.Empty(vm.ConditionTemplates);
        Assert.Null(vm.SelectedConditionTemplate);
        Assert.NotNull(toast.NotificationMessage);
    }

    [Fact]
    public void PreviewSections_GenerateCorrectHeadersAndFormats()
    {
        var (vm, _, _) = CreateViewModel();

        // 1. When empty template selected
        var emptyTemplate = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Empty",
            ConditionTree = new BacktestConditionTreeDto()
        };
        vm.SelectedConditionTemplate = emptyTemplate;
        Assert.True(vm.IsSelectedTemplateEmpty);

        // 2. When template with conditions selected
        var leaf = new BacktestConditionLeaf(new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 200m
        });
        var tree = new BacktestConditionTree(
            new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(leaf)),
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd);

        var templateWithConditions = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "SMA 200",
            ConditionTree = BacktestConditionTreeDtoMapper.ToDto(tree)
        };
        vm.SelectedConditionTemplate = templateWithConditions;

        Assert.False(vm.IsSelectedTemplateEmpty);
        Assert.NotEmpty(vm.SelectedTemplateConditionPreviewSections);

        var entryLongSection = vm.SelectedTemplateConditionPreviewSections.FirstOrDefault(s => s.SectionTitle.Contains("Entry") && s.SectionTitle.Contains("Long"));
        Assert.NotNull(entryLongSection);
        Assert.True(entryLongSection.HasItems);
        Assert.Single(entryLongSection.ConditionDescriptions);
    }

    [Fact]
    public void PreviewSections_UsesLocalizedRootTitles_WhenAvailable()
    {
        var catalogProvider = new FakeScreenerCatalogProvider(new ScreenerCatalogItem[]
        {
            new() { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
        });

        var localization = new FakeLocalizationService(new Dictionary<string, string>
        {
            ["Backtest_Template_Root_EntryLong"] = "Custom Entry (Long)",
            ["Backtest_Template_Root_ExitShort"] = "Custom Exit (Short)",
        });

        var vm = new BacktestIndicatorSelectionViewModel(
            catalogProvider,
            localization,
            toastService: new FakeToastNotificationService(),
            templateService: new InMemoryBacktestTemplateService());

        var leaf = new BacktestConditionLeaf(new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 200m
        });
        var tree = new BacktestConditionTree(
            new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(leaf)),
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd,
            BacktestConditionGroup.EmptyAnd);

        var template = new BacktestConditionTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Localized Test",
            ConditionTree = BacktestConditionTreeDtoMapper.ToDto(tree)
        };

        vm.SelectedConditionTemplate = template;

        Assert.Single(vm.SelectedTemplateConditionPreviewSections);
        Assert.Equal("Custom Entry (Long)", vm.SelectedTemplateConditionPreviewSections[0].SectionTitle);
    }
}
