using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Views;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public class DetachedWindowHeaderTests
{
    [AvaloniaFact]
    public void ShouldBeginWindowMove_NullSource_ReturnsTrue()
    {
        Assert.True(DetachedWindow.ShouldBeginWindowMove(null));
    }

    [AvaloniaFact]
    public void ShouldBeginWindowMove_PlainHeaderBorder_ReturnsTrue()
    {
        var header = new Border { Child = new TextBlock { Text = "empty header area" } };
        var window = new Window { Content = header };
        window.Show();
        try
        {
            Assert.True(DetachedWindow.ShouldBeginWindowMove(header));
            Assert.True(DetachedWindow.ShouldBeginWindowMove((TextBlock)header.Child));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShouldBeginWindowMove_TextInsideTabItem_ReturnsFalse()
    {
        var (window, tabItem, text, button) = ShowTabWithContent();
        try
        {
            Assert.False(DetachedWindow.ShouldBeginWindowMove(text));
            Assert.False(DetachedWindow.ShouldBeginWindowMove(tabItem));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShouldBeginWindowMove_ButtonInsideTabItem_ReturnsFalse()
    {
        var (window, _, _, button) = ShowTabWithContent();
        try
        {
            Assert.False(DetachedWindow.ShouldBeginWindowMove(button));
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, TabItem Tab, TextBlock Text, Button Button) ShowTabWithContent()
    {
        var text = new TextBlock { Text = "tab title" };
        var button = new Button { Content = "x" };
        var panel = new DockPanel();
        panel.Children.Add(button);
        panel.Children.Add(text);
        var tab = new TabItem { Header = panel };
        var tabControl = new TabControl { ItemsSource = new[] { tab } };
        var window = new Window { Content = tabControl, Width = 300, Height = 200 };
        window.Show();
        return (window, tab, text, button);
    }
}
