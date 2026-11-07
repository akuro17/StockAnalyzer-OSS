#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;
using Kit = StockAnalyzer.Avalonia.Tests.TestHelpers.ScatterMetricRegistrationTestKit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// S-1: in the scatter metric registration window an indicator that compares two symbols reads the DAILY bars of its comparison symbol, so typing a comparison symbol
/// disables the time frame box (with the reason) and clearing it frees the box again. Nothing is changed for the user: a frame chosen first stays, the shared validation
/// refuses the metric and Add stops with the invalid-metric message.
/// </summary>
public class ScatterMetricTwoTickerTimeFrameTests
{
    private const string TwoTickerReason = "ScatterMetric_Tip_TimeFrameTwoTickerDay";
    private const string FixedReason = "ScatterMetric_Tip_TimeFrameFixedDay";
    private const int MaxOffset = 500;

    private static ScreenerCatalogItem SelectIndicator(ScatterMetricRegistrationViewModel viewModel, IndicatorType type)
    {
        viewModel.SelectedNavItem = viewModel.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
        ScreenerCatalogItem item = viewModel.CatalogItems.First(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.IndicatorType == type);
        viewModel.SelectedCatalogItem = item;
        return item;
    }

    private static void SetComparisonSymbol(ScatterMetricDraftItem item, string symbol)
        => ((ICrossTickerParameter)item.ParameterObject!).ComparisonSymbol = symbol;

    private static List<string> Notified(ScatterMetricDraftItem item)
    {
        var names = new List<string>();
        item.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossCorrelation)]
    public async Task TypingAComparisonSymbol_DisablesTheTimeFrameBox_WithTheTwoTickerReason_AndNotifiesTheBox(IndicatorType type)
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            SelectIndicator(harness.ViewModel, type);
            ScatterMetricDraftItem item = harness.ViewModel.PendingTarget!;
            Assert.True(item.IsTimeFrameEditable, "a blank comparison symbol: Price vs Volume is computed in the metric's own bars");
            Assert.Null(item.TimeFrameToolTip);
            List<string> notified = Notified(item);

            SetComparisonSymbol(item, "SPY");

            Assert.False(item.IsTimeFrameEditable);
            Assert.Equal(TwoTickerReason, item.TimeFrameToolTip);
            Assert.Contains(nameof(ScatterMetricDraftItem.IsTimeFrameEditable), notified);
            Assert.Contains(nameof(ScatterMetricDraftItem.TimeFrameToolTip), notified);
            Assert.Contains(nameof(ScatterMetricDraftItem.TimeFrame), notified);
            Assert.Equal("SPY", item.Definition.Source.Parameters["ComparisonSymbol"]);
        }
    }

    [Fact]
    public async Task ClearingTheComparisonSymbol_FreesTheTimeFrameBoxAgain_AndTheReasonGoesAway()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            SelectIndicator(harness.ViewModel, IndicatorType.Correlation);
            ScatterMetricDraftItem item = harness.ViewModel.PendingTarget!;
            SetComparisonSymbol(item, "SPY");
            Assert.False(item.IsTimeFrameEditable);
            List<string> notified = Notified(item);

            SetComparisonSymbol(item, "  ");

            Assert.True(item.IsTimeFrameEditable);
            Assert.Null(item.TimeFrameToolTip);
            Assert.Contains(nameof(ScatterMetricDraftItem.IsTimeFrameEditable), notified);

            item.TimeFrame = TimeFrame.W1;
            Assert.Equal(TimeFrame.W1, item.TimeFrame);
            Assert.Equal(ScatterMetricInvalidReason.None, ScatterMetricRules.Validate(item.Definition, MaxOffset, IndicatorFactory.Default));
        }
    }

    [Fact]
    public async Task ADisabledBox_RefusesAChangeOfTheFrame_AndStaysOnDay()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            SelectIndicator(harness.ViewModel, IndicatorType.Correlation);
            ScatterMetricDraftItem item = harness.ViewModel.PendingTarget!;
            SetComparisonSymbol(item, "SPY");
            int edits = 0;
            item.Edited += (_, _) => edits++;

            item.TimeFrame = TimeFrame.MN1;

            Assert.Equal(TimeFrame.D1, item.TimeFrame);
            Assert.Equal(0, edits);
        }
    }

    [Fact]
    public async Task WeekChosenFirst_AndASymbolTypedLater_KeepsWeek_DisablesTheBox_AndAddStopsWithTheInvalidMessage()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            SelectIndicator(vm, IndicatorType.Correlation);
            ScatterMetricDraftItem item = vm.PendingTarget!;
            item.TimeFrame = TimeFrame.W1;

            SetComparisonSymbol(item, "SPY");

            Assert.Equal(TimeFrame.W1, item.TimeFrame);
            Assert.Equal(TimeFrame.W1, item.Definition.Source.TimeFrame);
            Assert.False(item.IsTimeFrameEditable);
            Assert.Equal(TwoTickerReason, item.TimeFrameToolTip);
            Assert.Equal(ScatterMetricInvalidReason.TwoTickerNeedsDay, ScatterMetricRules.Validate(item.Definition, MaxOffset, IndicatorFactory.Default));

            vm.AddCommand.Execute(null);

            Assert.Empty(vm.Draft);
            Assert.Equal(new[] { "ScatterMetric_Msg_Invalid" }, harness.Toast.Messages);
        }
    }

    [Fact]
    public async Task OnDayWithASymbol_AddSucceeds()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            SelectIndicator(vm, IndicatorType.Correlation);
            SetComparisonSymbol(vm.PendingTarget!, "SPY");

            vm.AddCommand.Execute(null);

            Assert.Single(vm.Draft);
            Assert.DoesNotContain(harness.Toast.Messages, message => message.Contains("Invalid"));
            // The row added to the draft carries the same reasons (it is built by the same window).
            ScatterMetricDraftItem added = vm.Draft.Single();
            Assert.False(added.IsTimeFrameEditable);
            Assert.Equal(TwoTickerReason, added.TimeFrameToolTip);
        }
    }

    [Fact]
    public async Task ARegisteredMetricWithASymbolOnDay_OpensWithTheBoxDisabled_AndClearingTheSymbolFreesIt()
    {
        ScatterMetricDefinition correlation = Kit.Indicator(IndicatorType.Correlation);
        correlation.Source.Parameters["ComparisonSymbol"] = "SPY";
        var (harness, _) = await Kit.OpenAsync(new[] { correlation });
        using (harness)
        {
            ScatterMetricDraftItem item = harness.ViewModel.Draft.Single();

            Assert.False(item.IsTimeFrameEditable);
            Assert.Equal(TwoTickerReason, item.TimeFrameToolTip);

            SetComparisonSymbol(item, string.Empty);

            Assert.True(item.IsTimeFrameEditable);
            Assert.Null(item.TimeFrameToolTip);
        }
    }

    [Fact]
    public async Task AFundamentalColumn_KeepsItsOwnReason_AndAnOrdinaryIndicatorKeepsAFreeBox()
    {
        var (harness, _) = await Kit.OpenAsync();
        using (harness)
        {
            var vm = harness.ViewModel;
            Kit.SelectColumn(vm, "MarketCap");
            Assert.Equal(FixedReason, vm.PendingTarget!.TimeFrameToolTip);

            Kit.Select(vm, "RSI");
            Assert.True(vm.PendingTarget!.IsTimeFrameEditable);
            Assert.Null(vm.PendingTarget.TimeFrameToolTip);
        }
    }

    [Fact]
    public void TheTwoTickerTipKey_IsLookedUpInCode_AndExistsInBothLanguages_AndTheStatusKeyToo()
    {
        string root = FindRoot();
        string window = File.ReadAllText(Path.Combine(root, "StockAnalyzer.Avalonia", "ViewModels", "Dialogs", "ScatterMetricRegistrationViewModel.cs"));
        string plot = File.ReadAllText(Path.Combine(root, "StockAnalyzer.Avalonia", "ViewModels", "ScatterPlotViewModel.cs"));
        Assert.Matches(new Regex(@"GetString\(\s*""" + TwoTickerReason + @"""\)"), window);
        Assert.Matches(new Regex(@"FormatStatus\(\s*""Scatter_Status_ComparisonSymbolMissing"""), plot);

        foreach (string language in new[] { "en", "ja" })
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
            JsonElement values = document.RootElement;
            string tip = values.GetProperty(TwoTickerReason).GetString()!;
            string status = values.GetProperty("Scatter_Status_ComparisonSymbolMissing").GetString()!;

            Assert.False(string.IsNullOrWhiteSpace(tip), language);
            Assert.Contains("{0}", status);
            Assert.Equal(1, Regex.Matches(status, @"\{\d+\}").Count);
        }

    }

    private static string FindRoot()
    {
        string? directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "StockAnalyzer.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("The repository root (StockAnalyzer.sln) was not found.");
    }
}
