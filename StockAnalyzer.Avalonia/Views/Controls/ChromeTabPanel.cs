using System;
using Avalonia;
using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Views.Controls;

public class ChromeTabPanel : Panel
{
    public static readonly StyledProperty<double> MaxTabWidthProperty =
        AvaloniaProperty.Register<ChromeTabPanel, double>(nameof(MaxTabWidth), 180.0);

    public static readonly StyledProperty<double> MinTabWidthProperty =
        AvaloniaProperty.Register<ChromeTabPanel, double>(nameof(MinTabWidth), 50.0);

    public double MaxTabWidth
    {
        get => GetValue(MaxTabWidthProperty);
        set => SetValue(MaxTabWidthProperty, value);
    }

    public double MinTabWidth
    {
        get => GetValue(MinTabWidthProperty);
        set => SetValue(MinTabWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        int count = Children.Count;
        if (count == 0)
            return new Size(0, availableSize.Height);

        double calculatedWidth = width / count;
        double tabWidth = Math.Clamp(calculatedWidth, MinTabWidth, MaxTabWidth);

        double totalWidth = 0;
        double maxHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(tabWidth, availableSize.Height));
            totalWidth += tabWidth;
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
        }

        return new Size(Math.Min(totalWidth, width), maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
            return finalSize;

        double calculatedWidth = finalSize.Width / count;
        double tabWidth = Math.Clamp(calculatedWidth, MinTabWidth, MaxTabWidth);

        double x = 0;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, tabWidth, finalSize.Height));
            x += tabWidth;
        }

        return finalSize;
    }
}

public class ChromeTabHeadersPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children)
            {
                child.Measure(availableSize);
            }
            return new Size(0, availableSize.Height);
        }

        var tabs = Children[0];
        var button = Children[1];

        // Reserve space for button (28px + 8px spacing/margin = 36px)
        double btnWidth = 28;
        double spacing = 8;
        double reservedWidth = btnWidth + spacing;
        double tabsAvailableWidth = Math.Max(0, availableSize.Width - reservedWidth);

        tabs.Measure(new Size(tabsAvailableWidth, availableSize.Height));
        button.Measure(new Size(btnWidth, availableSize.Height));

        double totalWidth = tabs.DesiredSize.Width + reservedWidth;
        double maxHeight = Math.Max(tabs.DesiredSize.Height, button.DesiredSize.Height);

        return new Size(Math.Min(totalWidth, availableSize.Width), maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children)
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            }
            return finalSize;
        }

        var tabs = Children[0];
        var button = Children[1];

        double btnWidth = 28;
        double spacing = 8;
        double tabsWidth = tabs.DesiredSize.Width;

        // Position tabs
        tabs.Arrange(new Rect(0, 0, tabsWidth, finalSize.Height));

        // Position button immediately to the right of tabs
        double btnX = tabsWidth + spacing;
        button.Arrange(new Rect(btnX, (finalSize.Height - btnWidth) / 2, btnWidth, btnWidth));

        return finalSize;
    }
}
