using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Screener;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Filters tree holds at most <c>Screener:MaxFilterTreeNodes</c> nodes. An entry that does not fit must not be registered at all:
/// a registered entry without a tree leaf would be listed (and counted) but never evaluated.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class IndicatorRegistrationTreeLimitTests
{
    // Root + 2 leaves.
    private sealed class ThreeNodeSettings : MockStockAnalyzerSettings, IStockAnalyzerSettings
    {
        public new int ScreenerMaxFilterTreeNodes => 3;
    }

    private static IndicatorRegistrationViewModel Create(IToastNotificationService? toast = null, ITemplateService? templates = null)
    {
        var vm = new IndicatorRegistrationViewModel(settings: new ThreeNodeSettings(), toastService: toast, templateService: templates);
        vm.LeftSelectedIndicator = new ScreenerCatalogItem { DisplayName = "SMA" };
        vm.RightTargetMode = RightHandTargetMode.NumericValue;
        return vm;
    }

    private static int LeafCount(IndicatorRegistrationViewModel vm)
        => vm.ConditionTree.Root.Children.OfType<ScreenerConditionLeafViewModel>().Count();

    private static ScreenerIndicatorEntry Entry(IndicatorType type, decimal value) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = type },
        Operator = ComparisonOperator.GreaterThan,
        RightNumericValue = value
    };

    private static ScreenerIndicatorTemplate TemplateOfThreeEntries()
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "Tpl" };
        template.SetEntries(new[] { Entry(IndicatorType.SMA, 1m), Entry(IndicatorType.RSI, 2m), Entry(IndicatorType.MACD, 0m) });
        return template;
    }

    private static Mock<ITemplateService> TemplateService()
    {
        var service = new Mock<ITemplateService>();
        service.Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener)).ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        service.Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>())).ReturnsAsync(TemplateValidationResult.Success());
        return service;
    }

    [Fact]
    public void Register_AtNodeLimit_CommandDisabled_EntriesAndTreeLeavesEqual()
    {
        var vm = Create();

        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));
        vm.RegisterIndicatorCommand.Execute(null);
        vm.RegisterIndicatorCommand.Execute(null);

        Assert.False(vm.RegisterIndicatorCommand.CanExecute(null));
        // Even when the command is invoked anyway (e.g. by a key binding), nothing is registered.
        vm.RegisterIndicatorCommand.Execute(null);
        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.Equal(2, LeafCount(vm));
    }

    [Fact]
    public void Register_AfterLeafDeleted_ReenablesCommand()
    {
        var vm = Create();
        vm.RegisterIndicatorCommand.Execute(null);
        vm.RegisterIndicatorCommand.Execute(null);
        Assert.False(vm.RegisterIndicatorCommand.CanExecute(null));

        int canExecuteChanged = 0;
        vm.RegisterIndicatorCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        vm.RegisteredEntries.RemoveAt(0);

        Assert.True(canExecuteChanged > 0);
        Assert.True(vm.RegisterIndicatorCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadTemplate_ExceedingLimit_AddsOnlyWhatFits_AndNotifies()
    {
        var messages = new List<string>();
        var toast = new Mock<IToastNotificationService>();
        toast.Setup(t => t.ShowNotification(It.IsAny<string>())).Callback<string>(messages.Add);
        var vm = Create(toast.Object, TemplateService().Object);

        await vm.LoadTemplateCommand.ExecuteAsync(TemplateOfThreeEntries());

        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.Equal(2, LeafCount(vm));
        Assert.Equal(IndicatorType.SMA, vm.RegisteredEntries[0].LeftHand.IndicatorType);
        Assert.Equal(IndicatorType.RSI, vm.RegisteredEntries[1].LeftHand.IndicatorType);
        // The limit text (it names the node limit 3) is the last message, after the "template loaded" toast.
        Assert.Contains("3", messages[^1]);
    }

    [Fact]
    public async Task AppendTemplate_ExceedingLimit_AddsOnlyWhatFits_AndNotifies()
    {
        var messages = new List<string>();
        var toast = new Mock<IToastNotificationService>();
        toast.Setup(t => t.ShowNotification(It.IsAny<string>())).Callback<string>(messages.Add);
        var vm = Create(toast.Object, TemplateService().Object);
        vm.RegisteredEntries.Add(Entry(IndicatorType.EMA, 5m));

        await vm.AppendTemplateCommand.ExecuteAsync(TemplateOfThreeEntries());

        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.Equal(2, LeafCount(vm));
        Assert.Contains("3", messages[^1]);
    }

    [Fact]
    public async Task LoadTemplate_WithinLimit_AddsEverythingWithoutLimitNotice()
    {
        var messages = new List<string>();
        var toast = new Mock<IToastNotificationService>();
        toast.Setup(t => t.ShowNotification(It.IsAny<string>())).Callback<string>(messages.Add);
        var vm = Create(toast.Object, TemplateService().Object);
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "Tpl" };
        template.SetEntries(new[] { Entry(IndicatorType.SMA, 1m), Entry(IndicatorType.RSI, 2m) });

        await vm.LoadTemplateCommand.ExecuteAsync(template);

        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.Equal(2, LeafCount(vm));
        Assert.DoesNotContain(messages, m => m.Contains("3"));
    }
}
