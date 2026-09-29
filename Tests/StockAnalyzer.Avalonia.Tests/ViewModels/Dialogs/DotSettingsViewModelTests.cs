using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Dialogs;

public class DotSettingsViewModelTests
{
    private static DotSettingsViewModel Create(MockChartSettingsManager manager)
        => new(manager, NullLogger<DotSettingsViewModel>.Instance);

    [Fact]
    public void Load_TakesEveryValueFromGlobalChartSettingsDefaults()
    {
        var vm = Create(new MockChartSettingsManager());

        Assert.Equal(ChartSettingsConstants.DefaultDotBaseRadius, vm.BaseRadius);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor), vm.UpColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor), vm.DownColor);
        Assert.Equal(HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultNeutralColor), vm.NaturalColor);
        Assert.Equal(DotShapeType.Circle, vm.ShapeType);
        Assert.Equal(PriceType.Close, vm.PriceType);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingBaseRadius_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.BaseRadius = 5.0;

        Assert.True(vm.IsModified);

        // Revert back
        vm.BaseRadius = ChartSettingsConstants.DefaultDotBaseRadius;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingShapeType_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.ShapeType = DotShapeType.Diamond;
        Assert.True(vm.IsModified);

        vm.ShapeType = DotShapeType.Circle;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingPriceType_MarksModified()
    {
        var vm = Create(new MockChartSettingsManager());

        vm.PriceType = PriceType.High;
        Assert.True(vm.IsModified);

        vm.PriceType = PriceType.Close;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task SaveCommand_PersistsValuesToManager()
    {
        var manager = new MockChartSettingsManager();
        var vm = Create(manager);

        vm.BaseRadius = 4.5;
        vm.ShapeType = DotShapeType.Square;
        vm.PriceType = PriceType.Typical;

        await vm.SaveChangesAsync();

        Assert.Equal(4.5, manager.Current.DotBaseRadius);
        Assert.Equal(DotShapeType.Square, manager.Current.DotShape);
        Assert.Equal(PriceType.Typical, manager.Current.DotPriceType);
        Assert.False(vm.IsModified);
    }

    [AvaloniaFact]
    public void DotSettingsView_NumericUpDown_IsVerticallyCentered()
    {
        var vm = Create(new MockChartSettingsManager());
        var view = new global::StockAnalyzer.Avalonia.Views.Dialogs.Chart.DotSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 600, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var numericUpDown = view.FindDescendantOfType<NumericUpDown>();
        Assert.NotNull(numericUpDown);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, numericUpDown.VerticalContentAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, numericUpDown.VerticalAlignment);

        var textBox = numericUpDown.FindDescendantOfType<TextBox>();
        Assert.NotNull(textBox);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, textBox.VerticalContentAlignment);

        var textPresenter = textBox.FindDescendantOfType<global::Avalonia.Controls.Presenters.TextPresenter>();
        Assert.NotNull(textPresenter);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, textPresenter.VerticalAlignment);

        // Verify that top and bottom gaps are balanced (difference <= 1.0 pixel due to integer pixel rounding)
        double relY = 0;
        for (global::Avalonia.Visual? v = textPresenter; v != null && v != numericUpDown; v = (global::Avalonia.Visual?)v.GetVisualParent())
        {
            relY += v.Bounds.Y;
        }

        double gapTop = relY;
        double gapBottom = numericUpDown.Bounds.Height - (relY + textPresenter.Bounds.Height);
        Assert.True(System.Math.Abs(gapTop - gapBottom) <= 1.0, $"Gaps must be balanced: top={gapTop}, bottom={gapBottom}");
    }
}
