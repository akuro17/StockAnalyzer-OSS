using StockAnalyzer.Core.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Offset input boxes of the registration dialog take their upper bound from <c>Screener:MaxConditionOffset</c>. A saved template with an Offset
/// outside the accepted range is an error: nothing of it is applied and the user is told which entry (never clamped silently).
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class IndicatorRegistrationOffsetLimitTests
{
    private sealed class OffsetSettings : MockStockAnalyzerSettings, IStockAnalyzerSettings
    {
        public new int ScreenerMaxConditionOffset => 300;
    }

    private static (IndicatorRegistrationViewModel Vm, List<string> Messages) Create()
    {
        var messages = new List<string>();
        var toast = new Mock<IToastNotificationService>();
        toast.Setup(t => t.ShowNotification(It.IsAny<string>())).Callback<string>(messages.Add);
        var templates = new Mock<ITemplateService>();
        templates.Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener)).ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        templates.Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>())).ReturnsAsync(TemplateValidationResult.Success());
        return (new IndicatorRegistrationViewModel(settings: new OffsetSettings(), toastService: toast.Object, templateService: templates.Object), messages);
    }

    private static ScreenerIndicatorEntry Entry(int leftOffset, int rightOffset = 0) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Offset = leftOffset },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        RightHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.EMA, Offset = rightOffset }
    };

    private static ScreenerIndicatorTemplate Template(params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "Tpl" };
        template.SetEntries(entries);
        return template;
    }

    [Fact]
    public void OffsetBounds_FollowTheSetting_AndTheCausalityMinimum()
    {
        var (vm, _) = Create();

        Assert.Equal(300m, vm.MaxConditionOffset);
        Assert.Equal(0m, vm.MinConditionOffset);
    }

    // The binding is read back from the real view (a mistyped or stale binding would leave the former fixed limit): both Offset boxes take the configured range and clip.
    [AvaloniaFact]
    public void OffsetInputBoxes_TakeTheirRangeFromTheSetting_AndClipAnOutOfRangeEntry()
    {
        var (vm, _) = Create();
        var window = new Window { Content = new IndicatorRegistrationView { DataContext = vm }, Width = 1400, Height = 1000 };
        try
        {
            window.Show();
            var boxes = window.GetVisualDescendants().OfType<NumericUpDown>().Where(n => n.Maximum == 300m).ToList();

            Assert.Equal(2, boxes.Count);
            Assert.All(boxes, box =>
            {
                Assert.Equal(0m, box.Minimum);
                Assert.True(box.ClipValueToMinMax);
            });
        }
        finally { window.Close(); }
    }

    [Fact]
    public void OffsetBounds_WithoutSettings_UseTheScreenerDefault()
    {
        var vm = new IndicatorRegistrationViewModel(settings: new MockStockAnalyzerSettings());

        Assert.Equal(new StockAnalyzer.Core.Models.Settings.ScreenerSettings().MaxConditionOffset, (int)vm.MaxConditionOffset);
    }

    [Theory]
    [InlineData(301, 0, 1, "301")]
    [InlineData(0, 301, 2, "301")]
    [InlineData(-1, 0, 1, "-1")]
    public async Task LoadTemplate_OffsetOutsideTheRange_AppliesNothingAndNamesTheEntry(int left, int right, int badEntryNumber, string badValueText)
    {
        var (vm, messages) = Create();
        vm.RegisteredEntries.Add(Entry(5));

        var template = badEntryNumber == 1 ? Template(Entry(left, right), Entry(1)) : Template(Entry(1), Entry(left, right));
        await vm.LoadTemplateCommand.ExecuteAsync(template);

        Assert.Single(vm.RegisteredEntries);
        Assert.Equal(5, vm.RegisteredEntries[0].LeftHand.Offset);
        Assert.Contains(badEntryNumber.ToString(), messages[^1]);
        Assert.Contains(badValueText, messages[^1]);
    }

    // The scan judges the right Offset in every mode (it sizes the history), so a numeric comparison with a stray right Offset is refused here as well.
    [Fact]
    public async Task LoadTemplate_RightOffsetAboveTheLimitInNumericMode_AppliesNothing()
    {
        var (vm, messages) = Create();
        var numeric = Entry(0, 301);
        numeric.TargetMode = RightHandTargetMode.NumericValue;

        await vm.LoadTemplateCommand.ExecuteAsync(Template(numeric));

        Assert.Empty(vm.RegisteredEntries);
        Assert.Contains("301", messages[^1]);
    }

    [Fact]
    public async Task AppendTemplate_OffsetAboveTheLimit_AppliesNothing()
    {
        var (vm, messages) = Create();
        vm.RegisteredEntries.Add(Entry(5));

        await vm.AppendTemplateCommand.ExecuteAsync(Template(Entry(1), Entry(301)));

        Assert.Single(vm.RegisteredEntries);
        Assert.Contains("301", messages[^1]);
    }

    [Fact]
    public async Task LoadTemplate_OffsetsAtTheEdges_AreApplied()
    {
        var (vm, _) = Create();

        await vm.LoadTemplateCommand.ExecuteAsync(Template(Entry(0, 300), Entry(300, 0)));

        Assert.Equal(2, vm.RegisteredEntries.Count);
    }
}
