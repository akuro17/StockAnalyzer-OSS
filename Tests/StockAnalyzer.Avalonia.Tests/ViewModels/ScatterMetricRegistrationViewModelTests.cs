#nullable enable
using StockAnalyzer.Core.Models.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;
using Kit = StockAnalyzer.Avalonia.Tests.TestHelpers.ScatterMetricRegistrationTestKit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The scatter metric registration window's view model: navigation, catalog, draft editing and the OK / Cancel transitions.</summary>
public class ScatterMetricRegistrationViewModelTests
{
    private static ScatterMetricDefinition Rsi(int offset = 0) => Kit.Indicator(IndicatorType.RSI, offset);
    private static ScatterMetricDefinition Sma(int offset = 0) => Kit.Indicator(IndicatorType.SMA, offset);

    // ===== Navigation =====

    [Fact]
    public async Task TheNavigation_ListsActive_Divider_AllFilters_Indicators_Columns_InThatOrder()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var nav = harness.ViewModel.NavItems;

            string[] kinds = nav.Select(n => n.Kind == ScatterMetricNavKind.Group ? "Group:" + n.Group!.Name : n.Kind.ToString()).ToArray();
            Assert.Equal(new[]
            {
                "Active", "Divider", "AllFilters",
                "Header", "Group:Price", "Group:Trend", "Group:Oscillator", "Group:Volume", "Group:Volatility", "Group:Math", "Group:Chart",
                "Header", "Group:Basic", "Group:Price/Volume", "Group:Valuation", "Group:Profitability", "Group:Financial Health",
            }, kinds);
            Assert.Equal("ScatterMetric_Nav_Active", nav[0].Title);
            Assert.Equal("Nav_AllFilters", nav[2].Title);
            Assert.Equal("IndicatorRegistration_NavHeader_Indicators", nav[3].Title);
            Assert.Equal("IndicatorRegistration_NavHeader_Columns", nav[11].Title);
            Assert.All(nav.Where(n => n.Kind == ScatterMetricNavKind.Group), n => Assert.StartsWith("ScreenerGroup_", n.Title));
            Assert.DoesNotContain(nav, n => n.Group?.CategoryType == "Criteria");
        }
    }

    [Fact]
    public async Task TheInitialRow_IsAllFilters_WhenNothingIsRegistered_AndActive_WhenSomethingIs()
    {
        var (empty, _) = await Kit.OpenAsync();
        var (filled, _) = await Kit.OpenAsync(new[] { Rsi() });
        using (empty)
        using (filled)
        {
            Assert.Equal(ScatterMetricNavKind.AllFilters, empty.ViewModel.SelectedNavItem!.Kind);
            Assert.True(empty.ViewModel.IsCatalogMode);
            Assert.Equal(ScatterMetricNavKind.Active, filled.ViewModel.SelectedNavItem!.Kind);
            Assert.True(filled.ViewModel.IsActiveMode);
        }
    }

    [Fact]
    public async Task AHeaderOrTheDivider_CannotBeSelected()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            ScatterMetricNavItem trend = vm.NavItems.First(n => n.Group?.Name == "Trend");
            vm.SelectedNavItem = trend;

            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.Header);
            Assert.Same(trend, vm.SelectedNavItem);

            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.Divider);
            Assert.Same(trend, vm.SelectedNavItem);
        }
    }

    // ===== Initialisation =====

    [Fact]
    public async Task TheWindow_WaitsForTheRegistryToLoad_AndIsBusyMeanwhile()
    {
        var registry = new FakeScatterMetricRegistry(new[] { Rsi() }, loaded: false);
        using var harness = new Kit.Harness(registry, Kit.Settings());
        var vm = harness.ViewModel;

        Assert.True(vm.IsBusy);
        Assert.Empty(vm.Draft);
        Assert.False(vm.OkCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));

        registry.CompleteLoading();
        await vm.InitializationTask;

        Assert.False(vm.IsBusy);
        Assert.Single(vm.Draft);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task EntriesTheRegistryDropped_AreAnnouncedOnce()
    {
        var registry = new FakeScatterMetricRegistry(new[] { Rsi() }) { DroppedCount = 2 };
        using var first = new Kit.Harness(registry, Kit.Settings());
        await first.ViewModel.InitializationTask;
        using var second = new Kit.Harness(registry, Kit.Settings());
        await second.ViewModel.InitializationTask;

        Assert.Equal(new[] { "ScatterMetric_Msg_LoadDropped" }, first.Toast.Messages);
        Assert.Empty(second.Toast.Messages);
    }

    // ===== Catalog =====

    [Fact]
    public async Task AllFilters_OffersOnlyEligibleItems_SortedByDisplayName()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);

            Assert.NotEmpty(vm.CatalogItems);
            Assert.All(vm.CatalogItems, item => Assert.True(ScatterMetricEligibility.IsEligible(item), item.ShortName));
            Assert.DoesNotContain(vm.CatalogItems, i => i.CategoryType == ScreenerItemCategoryType.Criteria);
            Assert.Contains(vm.CatalogItems, i => i.CategoryType == ScreenerItemCategoryType.Indicator);
            Assert.Contains(vm.CatalogItems, i => i.CategoryType == ScreenerItemCategoryType.Column);
            string[] names = vm.CatalogItems.Select(i => i.DisplayName).ToArray();
            Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), names);
        }
    }

    [Fact]
    public async Task AGroup_ListsOnlyItsOwnItems_AndThePriceGroupFollowsThePriceOrder()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;

            vm.SelectedNavItem = vm.NavItems.First(n => n.Group?.Name == "Trend");
            Assert.NotEmpty(vm.CatalogItems);
            Assert.All(vm.CatalogItems, i => Assert.Equal("Trend", i.GroupName));

            vm.SelectedNavItem = vm.NavItems.First(n => n.Group?.Name == "Price");
            string[] expected = PriceDataHelper.PriceTypeOptions.Select(t => t.ToString()).Where(name => vm.CatalogItems.Any(i => i.ShortName == name)).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, vm.CatalogItems.Select(i => i.ShortName).Where(expected.Contains).ToArray());
        }
    }

    [Fact]
    public async Task TheSearch_FiltersAfterTheConfiguredDebounce()
    {
        var (harness, _) = await Kit.OpenAsync(scatter: new ScatterMetricSettings { InputDebounceMilliseconds = 300 });
        using (harness)
        {
            var vm = harness.ViewModel;
            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
            int all = vm.CatalogItems.Count;

            var watch = Stopwatch.StartNew();
            vm.SearchText = "RSI";
            Assert.Equal(all, vm.CatalogItems.Count);
            await vm.PendingSearch;
            watch.Stop();

            Assert.True(watch.ElapsedMilliseconds >= 250, $"the search ran after {watch.ElapsedMilliseconds} ms, before the 300 ms debounce");
            Assert.InRange(vm.CatalogItems.Count, 1, all - 1);
            Assert.All(vm.CatalogItems, i => Assert.True(
                i.ShortName.Contains("RSI", StringComparison.OrdinalIgnoreCase) || i.DisplayName.Contains("RSI", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task ASearchTypedAgain_ReplacesTheEarlierOne()
    {
        var (harness, _) = await Kit.OpenAsync(scatter: new ScatterMetricSettings { InputDebounceMilliseconds = 200 });
        using (harness)
        {
            var vm = harness.ViewModel;
            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);

            vm.SearchText = "ZZZ-nothing";
            Task first = vm.PendingSearch;
            vm.SearchText = "SMA";
            await Task.WhenAll(first, vm.PendingSearch);

            Assert.NotEmpty(vm.CatalogItems);
            Assert.All(vm.CatalogItems, i => Assert.True(
                i.ShortName.Contains("SMA", StringComparison.OrdinalIgnoreCase) || i.DisplayName.Contains("SMA", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task ChoosingAnIndicator_ShowsItsDefaults_AndSeveralOutputsOnlyWhenItHasThem()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;

            Kit.Select(vm, "RSI");
            ScatterMetricDraftItem rsi = vm.PendingTarget!;
            Assert.Same(rsi, vm.CurrentEditor);
            Assert.True(rsi.HasParameters);
            Assert.False(rsi.HasMultipleOutputs);
            Assert.Equal(0, rsi.Offset);
            Assert.Equal(14, Convert.ToInt32(rsi.Definition.Source.Parameters["Period"]));

            Kit.Select(vm, "MACD");
            Assert.True(vm.PendingTarget!.HasMultipleOutputs);
        }
    }

    [Fact]
    public async Task EveryOfferedCatalogItem_CanBeAddedToTheDraft()
    {
        var (harness, _) = await Kit.OpenAsync(scatter: new ScatterMetricSettings { MaxRegisteredMetrics = 1 });
        using (harness)
        {
            var vm = harness.ViewModel;
            vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
            var failures = new List<string>();

            foreach (ScreenerCatalogItem item in vm.CatalogItems.ToList())
            {
                vm.SelectedCatalogItem = item;
                if (vm.PendingTarget is null || !vm.AddCommand.CanExecute(null))
                {
                    failures.Add($"{item.CategoryType}/{item.ShortName}: no editor");
                    continue;
                }

                vm.AddCommand.Execute(null);
                if (vm.Draft.Count != 1)
                {
                    failures.Add($"{item.CategoryType}/{item.ShortName}: not added ({string.Join(" | ", harness.Toast.Messages)})");
                }

                foreach (ScatterMetricDraftItem added in vm.Draft.ToList())
                {
                    vm.RemoveCommand.Execute(added);
                }

                harness.Toast.Messages.Clear();
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
    }

    // ===== Adding =====

    [Fact]
    public async Task Add_PutsTheMetricInTheDraft_AnnouncesIt_AndLeavesTheRegistryAlone()
    {
        var (harness, registry) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "RSI");

            vm.AddCommand.Execute(null);

            ScatterMetricDraftItem added = Assert.Single(vm.Draft);
            Assert.Equal(ScatterMetricInvalidReason.None, ScatterMetricRules.Validate(added.Definition, 500, IndicatorFactory.Default));
            Assert.Equal($"{added.Label} Msg_Added", Assert.Single(harness.Toast.Messages));
            Assert.Empty(registry.ReplaceCalls);
            Assert.Empty(registry.Snapshot());
            Assert.True(vm.IsModified);
        }
    }

    [Fact]
    public async Task Add_RefusesTheSameContentTwice_ButAcceptsAnotherOffset()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "RSI");
            vm.AddCommand.Execute(null);
            harness.Toast.Messages.Clear();

            vm.AddCommand.Execute(null);
            Assert.Single(vm.Draft);
            Assert.Equal(new[] { "ScatterMetric_Msg_Duplicate" }, harness.Toast.Messages);

            vm.PendingTarget!.Offset = 1;
            vm.AddCommand.Execute(null);
            Assert.Equal(2, vm.Draft.Count);
        }
    }

    [Fact]
    public async Task Add_StopsAtTheConfiguredLimit()
    {
        var (harness, _) = await Kit.OpenAsync(scatter: new ScatterMetricSettings { MaxRegisteredMetrics = 1 });
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "RSI");
            vm.AddCommand.Execute(null);
            harness.Toast.Messages.Clear();

            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);

            Assert.Single(vm.Draft);
            Assert.Equal(new[] { "ScatterMetric_Msg_LimitReached" }, harness.Toast.Messages);
        }
    }

    [Fact]
    public async Task Add_RefusesAMetricTheSharedRulesRefuse()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "RSI");
            // Week is a supported time frame since the 2026-10-22 extension; an hourly metric is still refused by the shared rules.
            vm.PendingTarget!.Definition.Source.TimeFrame = TimeFrame.H1;

            vm.AddCommand.Execute(null);

            Assert.Empty(vm.Draft);
            Assert.Equal(new[] { "ScatterMetric_Msg_Invalid" }, harness.Toast.Messages);
        }
    }

    [Fact]
    public async Task APriceAndAColumn_AddedFromTheCatalog_GiveTheValueTheScreenerWouldRead()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            var bars = new List<CandleData>();
            for (int i = 0; i < 30; i++)
            {
                decimal close = 100m + i;
                bars.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), close - 1m, close + 1m, close - 2m, close, 1000L + i));
            }

            Kit.Select(vm, "Close");
            vm.AddCommand.Execute(null);
            ScatterMetricDefinition price = vm.Draft.Single().Definition;
            Assert.Equal(129m, ScreenerValueExtractor.Default.ExtractValueNullable(price.Source, bars));

            ScreenerCatalogItem column = vm.CatalogItems.First(i => i.CategoryType == ScreenerItemCategoryType.Column && i.ShortName == "Close");
            vm.SelectedCatalogItem = column;
            vm.AddCommand.Execute(null);
            ScatterMetricDefinition columnMetric = vm.Draft.Last().Definition;
            Assert.Equal(ScreenerItemCategoryType.Column, columnMetric.Source.CategoryType);
            Assert.Equal(129m, ScreenerValueExtractor.Default.ExtractValueNullable(columnMetric.Source, bars));
        }
    }

    // ===== Time frame =====

    [Fact]
    public async Task TheTimeFrameOptions_AreDayWeekMonth_WithTheLocalizedNames()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var options = harness.ViewModel.TimeFrameOptions;

            Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }, options.Select(o => o.Value).ToArray());
            // The test localization service answers the key itself, so the name is the one looked up for that time frame.
            Assert.Equal(new[] { "Enum_TimeFrame_D1", "Enum_TimeFrame_W1", "Enum_TimeFrame_MN1" }, options.Select(o => o.DisplayName).ToArray());
            Assert.Equal(ScatterMetricTimeFrames.Supported, options.Select(o => o.Value).ToArray());
        }
    }

    [Fact]
    public async Task ChoosingAWeek_ChangesTheDefinition_TheLabel_AndTheModifiedFlag_OnceEach()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            var vm = harness.ViewModel;
            ScatterMetricDraftItem item = vm.Draft.Single();
            int edits = 0;
            item.Edited += (_, _) => edits++;
            string dayLabel = item.Label;
            Assert.False(vm.IsModified);
            Assert.True(item.IsTimeFrameEditable);
            Assert.Null(item.TimeFrameToolTip);
            Assert.Equal(TimeFrame.D1, item.TimeFrame);

            item.TimeFrame = TimeFrame.W1;

            Assert.Equal(TimeFrame.W1, item.Definition.Source.TimeFrame);
            Assert.Equal(1, edits);
            Assert.EndsWith(" [W1]", item.Label);
            Assert.NotEqual(dayLabel, item.Label);
            Assert.True(vm.IsModified);

            item.TimeFrame = TimeFrame.W1;
            Assert.Equal(1, edits);

            item.TimeFrame = TimeFrame.D1;
            Assert.Equal(2, edits);
            Assert.Equal(dayLabel, item.Label);
            Assert.False(vm.IsModified);
        }
    }

    [Fact]
    public async Task ATimeFrameTheScatterDoesNotSupport_ChangesNothing()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            ScatterMetricDraftItem item = harness.ViewModel.Draft.Single();
            int edits = 0;
            int notified = 0;
            item.Edited += (_, _) => edits++;
            item.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(ScatterMetricDraftItem.TimeFrame) ? 1 : 0;

            item.TimeFrame = TimeFrame.H1;

            Assert.Equal(TimeFrame.D1, item.TimeFrame);
            Assert.Equal(0, edits);
            // The box is told to show the stored value again.
            Assert.Equal(1, notified);
        }
    }

    [Fact]
    public async Task AFundamentalColumn_StaysOnDay_WithAReasonForTheDisabledBox()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.SelectColumn(vm, "MarketCap");
            ScatterMetricDraftItem item = vm.PendingTarget!;
            int edits = 0;
            item.Edited += (_, _) => edits++;

            Assert.False(item.IsTimeFrameEditable);
            Assert.Equal("ScatterMetric_Tip_TimeFrameFixedDay", item.TimeFrameToolTip);

            item.TimeFrame = TimeFrame.W1;

            Assert.Equal(TimeFrame.D1, item.TimeFrame);
            Assert.Equal(TimeFrame.D1, item.Definition.Source.TimeFrame);
            Assert.Equal(0, edits);
        }
    }

    [Fact]
    public async Task ABarValueColumnAndAPrice_CanChooseWeek_AndTheCatalogItemKeepsDayAsItsDefault()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;

            Kit.SelectColumn(vm, "Close");
            ScatterMetricDraftItem column = vm.PendingTarget!;
            Assert.True(column.IsTimeFrameEditable);
            Assert.Equal(TimeFrame.D1, column.TimeFrame);
            column.TimeFrame = TimeFrame.MN1;
            Assert.EndsWith(" [MN1]", column.Label);

            Kit.Select(vm, "Close");
            ScatterMetricDraftItem price = vm.PendingTarget!;
            Assert.True(price.IsTimeFrameEditable);
            Assert.Equal(TimeFrame.D1, price.TimeFrame);
        }
    }

    [Fact]
    public async Task AWeekMetric_AddedAndSavedWithOk_IsStoredOnWeek()
    {
        var (harness, registry) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "SMA");
            vm.PendingTarget!.TimeFrame = TimeFrame.W1;
            vm.AddCommand.Execute(null);

            Assert.Equal(TimeFrame.W1, vm.Draft.Single().TimeFrame);
            Assert.EndsWith(" [W1]", vm.Draft.Single().Label);

            await vm.OkCommand.ExecuteAsync(null);

            Assert.Equal(TimeFrame.W1, Assert.Single(Assert.Single(registry.ReplaceCalls)).Source.TimeFrame);
            Assert.Equal(TimeFrame.W1, registry.Snapshot().Single().Source.TimeFrame);
        }
    }

    [Fact]
    public async Task TheSameMetricOnDayAndOnWeek_IsNotADuplicate()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);
            vm.PendingTarget!.TimeFrame = TimeFrame.W1;
            vm.AddCommand.Execute(null);

            Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1 }, vm.Draft.Select(d => d.TimeFrame).ToArray());
            Assert.DoesNotContain("ScatterMetric_Msg_Duplicate", harness.Toast.Messages);
        }
    }

    // ===== Active: editing the draft =====

    [Fact]
    public async Task MoveUp_MoveDown_AndRemove_ChangeOnlyTheDraft_AndTheEndsDoNothing()
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi(), Sma(), Rsi(1) });
        using (harness)
        {
            var vm = harness.ViewModel;
            var (first, second, third) = (vm.Draft[0], vm.Draft[1], vm.Draft[2]);

            vm.MoveUpCommand.Execute(first);
            vm.MoveDownCommand.Execute(third);
            Assert.Equal(new[] { first, second, third }, vm.Draft);
            Assert.False(vm.IsModified);

            vm.MoveDownCommand.Execute(first);
            Assert.Equal(new[] { second, first, third }, vm.Draft);
            Assert.True(vm.IsModified);

            vm.MoveUpCommand.Execute(first);
            Assert.Equal(new[] { first, second, third }, vm.Draft);
            Assert.False(vm.IsModified);

            vm.SelectDraftItemCommand.Execute(second);
            Assert.Same(second, vm.CurrentEditor);
            vm.RemoveCommand.Execute(second);
            Assert.Equal(new[] { first, third }, vm.Draft);
            Assert.Null(vm.SelectedDraftItem);
            Assert.True(vm.ShowActiveHint);

            Assert.Empty(registry.ReplaceCalls);
            Assert.Equal(3, registry.Snapshot().Count);
        }
    }

    [Fact]
    public async Task EditingAParameter_UpdatesTheLabelAtOnce_AndRestoringItClearsTheModifiedFlag()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            var vm = harness.ViewModel;
            ScatterMetricDraftItem item = vm.Draft.Single();
            vm.SelectDraftItemCommand.Execute(item);
            string original = item.Label;
            var raised = new List<string?>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            PropertyInfo period = item.ParameterObject!.GetType().GetProperty("Period")!;

            period.SetValue(item.ParameterObject, 21);

            Assert.NotEqual(original, item.Label);
            Assert.Contains("21", item.Label);
            Assert.Equal(21, Convert.ToInt32(item.Definition.Source.Parameters["Period"]));
            Assert.Contains(nameof(ScatterMetricDraftItem.Label), raised);
            Assert.True(vm.IsModified);

            period.SetValue(item.ParameterObject, 14);

            Assert.Equal(original, item.Label);
            Assert.False(vm.IsModified);
        }
    }

    [Fact]
    public async Task TheStoredParameters_AreShownInTheLiveParameterObject()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Kit.Indicator(IndicatorType.RSI, 0, period: 33) });
        using (harness)
        {
            ScatterMetricDraftItem item = harness.ViewModel.Draft.Single();

            Assert.Equal(33, Convert.ToInt32(item.ParameterObject!.GetType().GetProperty("Period")!.GetValue(item.ParameterObject)));
            Assert.False(harness.ViewModel.IsModified);
        }
    }

    [Fact]
    public async Task TheOffset_IsClampedToTheConfiguredRange_AndShownInTheLabel()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Rsi() }, new ScatterMetricSettings { MaxOffset = 40 });
        using (harness)
        {
            ScatterMetricDraftItem item = harness.ViewModel.Draft.Single();
            string plain = item.Label;

            item.Offset = 3;
            Assert.Equal(plain + " [-3]", item.Label);

            item.Offset = 9999;
            Assert.Equal(40, item.Definition.Source.Offset);
            Assert.Equal(40, item.Offset);

            item.Offset = -5;
            Assert.Equal(0, item.Definition.Source.Offset);
            Assert.Equal(plain, item.Label);
            Assert.Equal(0m, harness.ViewModel.MinOffset);
            Assert.Equal(40m, harness.ViewModel.MaxOffset);
        }
    }

    [Fact]
    public async Task EditingTheOutputSeries_ChangesTheDefinition()
    {
        var (harness, _) = await Kit.OpenAsync(new[] { Kit.Indicator(IndicatorType.MACD) });
        using (harness)
        {
            ScatterMetricDraftItem item = harness.ViewModel.Draft.Single();
            string other = item.Outputs.First(o => o != item.SelectedOutput);

            item.SelectedOutput = other;

            Assert.Equal(other, item.Definition.Source.OutputName);
            Assert.True(harness.ViewModel.IsModified);
        }
    }

    // ===== OK / Cancel (plan S5.6) =====

    [Fact]
    public async Task Ok_WithoutChanges_ClosesWithoutSaving()
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            await harness.ViewModel.OkCommand.ExecuteAsync(null);

            Assert.Equal(new[] { false }, harness.Closed);
            Assert.Empty(registry.ReplaceCalls);
            Assert.False(harness.ViewModel.Committed);
        }
    }

    [Fact]
    public async Task Ok_WithChanges_SavesTheWholeDraftInOrder_AndClosesConfirmed()
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);

            await vm.OkCommand.ExecuteAsync(null);

            var saved = Assert.Single(registry.ReplaceCalls);
            Assert.Equal(vm.Draft.Select(d => d.Definition.Id), saved.Select(m => m.Id));
            Assert.Equal(2, saved.Count);
            Assert.Equal(new[] { true }, harness.Closed);
            Assert.True(vm.Committed);
            Assert.Equal(2, registry.Snapshot().Count);
            Assert.Empty(harness.Dialog.LastConfirmationTitle ?? string.Empty);
        }
    }

    [Theory]
    [InlineData(ScatterMetricRegistryResult.PersistFailed, "ScatterMetric_Msg_PersistFailed")]
    [InlineData(ScatterMetricRegistryResult.LimitReached, "ScatterMetric_Msg_LimitReached")]
    [InlineData(ScatterMetricRegistryResult.Invalid, "ScatterMetric_Msg_Invalid")]
    public async Task Ok_WhenTheRegistryRefuses_KeepsTheWindowAndTheDraft_AndSaysWhy(ScatterMetricRegistryResult result, string toast)
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            var vm = harness.ViewModel;
            registry.Result = result;
            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);
            harness.Toast.Messages.Clear();

            await vm.OkCommand.ExecuteAsync(null);

            Assert.Empty(harness.Closed);
            Assert.False(vm.Committed);
            Assert.False(vm.IsBusy);
            Assert.Equal(2, vm.Draft.Count);
            Assert.True(vm.IsModified);
            Assert.Equal(new[] { toast }, harness.Toast.Messages);
            Assert.Single(registry.Snapshot());
        }
    }

    [Fact]
    public async Task Cancel_WithoutChanges_ClosesWithoutAsking()
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            await harness.ViewModel.CancelCommand.ExecuteAsync(null);

            Assert.Equal(new[] { false }, harness.Closed);
            Assert.Equal(0, harness.Dialog.ConfirmationCallCount);
            Assert.Empty(registry.ReplaceCalls);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_WithChanges_AsksFirst_AndDiscardsOnlyOnApproval(bool approve)
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() }, confirmDiscard: approve);
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);

            await vm.CancelCommand.ExecuteAsync(null);

            Assert.Equal(1, harness.Dialog.ConfirmationCallCount);
            Assert.Equal("Settings_DiscardConfirm_Title", harness.Dialog.LastConfirmationTitle);
            Assert.Equal("Settings_DiscardConfirm_Message", harness.Dialog.LastConfirmationMessage);
            Assert.Equal(approve ? new[] { false } : Array.Empty<bool>(), harness.Closed);
            Assert.Empty(registry.ReplaceCalls);
            Assert.Single(registry.Snapshot());
            Assert.Equal(2, vm.Draft.Count);
        }
    }

    [Fact]
    public async Task WhileSaving_OkCancelAndTheEditsDoNothing()
    {
        var (harness, registry) = await Kit.OpenAsync(new[] { Rsi() });
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.Select(vm, "SMA");
            vm.AddCommand.Execute(null);
            registry.Gate = new TaskCompletionSource();

            Task saving = vm.OkCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            Assert.False(vm.IsIdle);
            Assert.False(vm.OkCommand.CanExecute(null));
            Assert.False(vm.CancelCommand.CanExecute(null));
            Assert.False(vm.AddCommand.CanExecute(null));

            await vm.OkCommand.ExecuteAsync(null);
            await vm.CancelCommand.ExecuteAsync(null);
            vm.RemoveCommand.Execute(vm.Draft[0]);

            Assert.Single(registry.ReplaceCalls);
            Assert.Equal(0, harness.Dialog.ConfirmationCallCount);
            Assert.Empty(harness.Closed);
            Assert.Equal(2, vm.Draft.Count);

            registry.Gate.SetResult();
            await saving;
            Assert.Equal(new[] { true }, harness.Closed);
        }
    }

    [Fact]
    public async Task ASecondCancel_WhileTheConfirmationIsOpen_DoesNotAskAgain()
    {
        var answer = new TaskCompletionSource<bool>();
        var dialog = new SlowConfirmation(answer.Task);
        using var harness = new Kit.Harness(new FakeScatterMetricRegistry(new[] { Rsi() }), Kit.Settings(), dialog: dialog);
        await harness.ViewModel.InitializationTask;
        var vm = harness.ViewModel;
        Kit.Select(vm, "SMA");
        vm.AddCommand.Execute(null);

        Task first = vm.CancelCommand.ExecuteAsync(null);
        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Equal(1, dialog.AskedCount);
        Assert.Empty(harness.Closed);

        answer.SetResult(true);
        await first;
        Assert.Equal(new[] { false }, harness.Closed);
    }

    /// <summary>A dialog service whose confirmation stays open until the test answers.</summary>
    private sealed class SlowConfirmation : FakeDialogService, IDialogService
    {
        private readonly Task<bool> _answer;

        public SlowConfirmation(Task<bool> answer) => _answer = answer;

        public int AskedCount { get; private set; }

        Task<bool> IDialogService.ShowConfirmationAsync(string title, string message)
        {
            AskedCount++;
            return _answer;
        }
    }
}
