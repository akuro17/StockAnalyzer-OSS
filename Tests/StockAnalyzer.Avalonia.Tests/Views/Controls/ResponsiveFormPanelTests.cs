using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Constants;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public class ResponsiveFormPanelTests
{
    private class FixedSizedControl : Control
    {
        private readonly Size _desired;
        public FixedSizedControl(double width, double height) => _desired = new Size(width, height);
        protected override Size MeasureOverride(Size availableSize) => _desired;
    }

    [AvaloniaFact]
    public void Measure_ZeroChildren_ReturnsZeroSize()
    {
        var panel = new ResponsiveFormPanel();
        panel.Measure(new Size(800, 600));

        Assert.Equal(0, panel.DesiredSize.Width);
        Assert.Equal(0, panel.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void Measure_InfiniteAvailableWidth_ClampsToMaxContentWidth()
    {
        // MaxContentWidth = 2 * 360 + 24 = 744
        var panel = new ResponsiveFormPanel();
        panel.Children.Add(new FixedSizedControl(100, 30));
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(744, panel.DesiredSize.Width);
    }

    [AvaloniaFact]
    public void Measure_WidthThreshold_SwitchesColumnsCorrectly()
    {
        // Threshold: MinFieldWidth=280, ColumnSpacing=24 -> Divisor = 304.
        // At 584: (584 + 24) / 304 = 2.0 -> 2 columns, colW = (584 - 24) / 2 = 280
        // At 583: (583 + 24) / 304 = 1.996 -> 1 column, colW = 583
        var panelTwoCols = new ResponsiveFormPanel();
        panelTwoCols.Children.Add(new FixedSizedControl(100, 30));
        panelTwoCols.Children.Add(new FixedSizedControl(100, 30));
        panelTwoCols.Measure(new Size(584, 600));
        panelTwoCols.Arrange(new Rect(0, 0, 584, panelTwoCols.DesiredSize.Height));

        Assert.Equal(0, panelTwoCols.Children[0].Bounds.X);
        Assert.Equal(280 + 24, panelTwoCols.Children[1].Bounds.X);

        var panelOneCol = new ResponsiveFormPanel();
        panelOneCol.Children.Add(new FixedSizedControl(100, 30));
        panelOneCol.Children.Add(new FixedSizedControl(100, 30));
        panelOneCol.Measure(new Size(583, 600));
        panelOneCol.Arrange(new Rect(0, 0, 583, panelOneCol.DesiredSize.Height));

        Assert.Equal(0, panelOneCol.Children[0].Bounds.X);
        Assert.Equal(0, panelOneCol.Children[1].Bounds.X);
        Assert.True(panelOneCol.Children[1].Bounds.Y >= 30);
    }

    [AvaloniaFact]
    public void Arrange_FourChildrenTwoColumns_PlacesInGridOrder()
    {
        var panel = new ResponsiveFormPanel();
        for (int i = 0; i < 4; i++)
        {
            panel.Children.Add(new FixedSizedControl(100, 30));
        }

        panel.Measure(new Size(744, 600));
        panel.Arrange(new Rect(0, 0, 744, panel.DesiredSize.Height));

        // colW = 360, spacing = 24, rowSpacing = 12
        Assert.Equal(0, panel.Children[0].Bounds.X);
        Assert.Equal(0, panel.Children[0].Bounds.Y);
        Assert.Equal(360, panel.Children[0].Bounds.Width);

        Assert.Equal(360 + 24, panel.Children[1].Bounds.X);
        Assert.Equal(0, panel.Children[1].Bounds.Y);
        Assert.Equal(360, panel.Children[1].Bounds.Width);

        Assert.Equal(0, panel.Children[2].Bounds.X);
        Assert.Equal(30 + 12, panel.Children[2].Bounds.Y);

        Assert.Equal(360 + 24, panel.Children[3].Bounds.X);
        Assert.Equal(30 + 12, panel.Children[3].Bounds.Y);
    }

    [AvaloniaFact]
    public void Arrange_FullWidthChild_TakesEntireRowAndPushesNextItem()
    {
        var panel = new ResponsiveFormPanel();
        var item1 = new FixedSizedControl(100, 30);
        var item2Full = new FixedSizedControl(100, 40);
        ResponsiveFormPanel.SetIsFullWidth(item2Full, true);
        var item3 = new FixedSizedControl(100, 30);

        panel.Children.Add(item1);
        panel.Children.Add(item2Full);
        panel.Children.Add(item3);

        panel.Measure(new Size(744, 600));
        panel.Arrange(new Rect(0, 0, 744, panel.DesiredSize.Height));

        // item1 on row 0 (col 0)
        Assert.Equal(0, item1.Bounds.X);
        Assert.Equal(0, item1.Bounds.Y);

        // item2Full closes row 0, takes full width on row 1
        Assert.Equal(0, item2Full.Bounds.X);
        Assert.Equal(30 + 12, item2Full.Bounds.Y);
        Assert.Equal(744, item2Full.Bounds.Width);

        // item3 starts on row 2 (col 0)
        Assert.Equal(0, item3.Bounds.X);
        Assert.Equal(30 + 12 + 40 + 12, item3.Bounds.Y);
    }

    [AvaloniaFact]
    public void Arrange_InvisibleChild_IsSkippedWithoutAddingSpacing()
    {
        var panel = new ResponsiveFormPanel();
        var item1 = new FixedSizedControl(100, 30);
        var itemHidden = new FixedSizedControl(100, 30) { IsVisible = false };
        var item2 = new FixedSizedControl(100, 30);

        panel.Children.Add(item1);
        panel.Children.Add(itemHidden);
        panel.Children.Add(item2);

        panel.Measure(new Size(744, 600));
        panel.Arrange(new Rect(0, 0, 744, panel.DesiredSize.Height));

        // item1 at col 0, item2 at col 1 on same row
        Assert.Equal(0, item1.Bounds.X);
        Assert.Equal(0, item1.Bounds.Y);
        Assert.Equal(360 + 24, item2.Bounds.X);
        Assert.Equal(0, item2.Bounds.Y);
    }

    [AvaloniaFact]
    public void Measure_ChildWiderThanColumn_FallsBackToSingleColumn()
    {
        var panel = new ResponsiveFormPanel();
        // At Wp=744, colW=360. A child demanding 380px (> 360 + 0.5 via MinWidth or content) triggers fallback to 1 column.
        var wideChild = new FixedSizedControl(380, 30) { MinWidth = 380 };
        var normalChild = new FixedSizedControl(100, 30);

        panel.Children.Add(wideChild);
        panel.Children.Add(normalChild);

        panel.Measure(new Size(744, 600));
        panel.Arrange(new Rect(0, 0, 744, panel.DesiredSize.Height));

        // Both children should be stacked vertically in 1 column
        Assert.Equal(0, wideChild.Bounds.X);
        Assert.Equal(0, wideChild.Bounds.Y);
        Assert.Equal(0, normalChild.Bounds.X);
        Assert.Equal(30 + 12, normalChild.Bounds.Y);
    }
}
