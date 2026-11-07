using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// A host that does not offer the Screener-only two-symbol indicators (the ticker-notes dialog: its evaluation passes no comparison bars) must not let a saved Screener
/// template bring them in either: such a condition would be registered and then never fire. Like a template with an Offset out of range, the template is refused as a whole
/// and the user is told which condition and which indicator. The Screener tab (default) takes the same template unchanged.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class IndicatorRegistrationTemplateOfferTests
{
    private static (IndicatorRegistrationViewModel Vm, List<string> Messages) Create(bool offersScreenerOnlyIndicators)
    {
        var messages = new List<string>();
        var toast = new Mock<IToastNotificationService>();
        toast.Setup(t => t.ShowNotification(It.IsAny<string>())).Callback<string>(messages.Add);
        var templates = new Mock<ITemplateService>();
        templates.Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener)).ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        templates.Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>())).ReturnsAsync(TemplateValidationResult.Success());
        return (new IndicatorRegistrationViewModel(
            toastService: toast.Object,
            templateService: templates.Object,
            offersTwoSymbolIndicatorsOfTheScreener: offersScreenerOnlyIndicators), messages);
    }

    private static ScreenerIndicatorEntry Entry(IndicatorType left, IndicatorType? right = null, RightHandTargetMode mode = RightHandTargetMode.Indicator) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = left },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = mode,
        RightHand = new ScreenerIndicatorSideConfig { IndicatorType = right ?? IndicatorType.EMA },
    };

    private static ScreenerIndicatorTemplate Template(params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "Tpl" };
        template.SetEntries(entries);
        return template;
    }

    [Fact]
    public async Task LoadTemplate_NamingAScreenerOnlyIndicatorOnTheLeft_AppliesNothingAndNamesTheConditionAndTheIndicator()
    {
        var (vm, messages) = Create(offersScreenerOnlyIndicators: false);
        vm.RegisteredEntries.Add(Entry(IndicatorType.SMA));

        await vm.LoadTemplateCommand.ExecuteAsync(Template(Entry(IndicatorType.RSI), Entry(IndicatorType.RollingBeta)));

        Assert.Single(vm.RegisteredEntries);
        Assert.Equal(IndicatorType.SMA, vm.RegisteredEntries[0].LeftHand.IndicatorType);
        Assert.Contains("2", messages[^1]);
        Assert.Contains(IndicatorType.RollingBeta.GetDescription(), messages[^1]);
    }

    [Fact]
    public async Task AppendTemplate_NamingAScreenerOnlyIndicatorOnTheRight_AppliesNothing()
    {
        var (vm, messages) = Create(offersScreenerOnlyIndicators: false);
        vm.RegisteredEntries.Add(Entry(IndicatorType.SMA));

        await vm.AppendTemplateCommand.ExecuteAsync(Template(Entry(IndicatorType.RSI, IndicatorType.Ratiocator)));

        Assert.Single(vm.RegisteredEntries);
        Assert.Contains(IndicatorType.Ratiocator.GetDescription(), messages[^1]);
    }

    [Fact]
    public async Task ARightSideThatTheComparisonDoesNotUse_IsNotJudged()
    {
        var (vm, _) = Create(offersScreenerOnlyIndicators: false);

        await vm.LoadTemplateCommand.ExecuteAsync(Template(Entry(IndicatorType.RSI, IndicatorType.Ratiocator, RightHandTargetMode.NumericValue)));

        Assert.Single(vm.RegisteredEntries);
    }

    [Fact]
    public async Task ATemplateWithOnlyOfferedIndicators_IsAppliedEvenWhenItNamesCorrelation()
    {
        var (vm, _) = Create(offersScreenerOnlyIndicators: false);

        await vm.LoadTemplateCommand.ExecuteAsync(Template(Entry(IndicatorType.Correlation), Entry(IndicatorType.SMA)));

        Assert.Equal(2, vm.RegisteredEntries.Count);
    }

    [Fact]
    public async Task TheScreenerTab_TakesTheSameTemplateUnchanged()
    {
        var (vm, messages) = Create(offersScreenerOnlyIndicators: true);

        await vm.LoadTemplateCommand.ExecuteAsync(Template(Entry(IndicatorType.RollingBeta), Entry(IndicatorType.RSI, IndicatorType.Ratiocator)));

        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.DoesNotContain(messages, message => message.Contains(IndicatorType.RollingBeta.GetDescription(), StringComparison.Ordinal));
    }
}
