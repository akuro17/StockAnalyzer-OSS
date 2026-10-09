using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// Escape closes every sub-window (SA_UI_SUBWINDOW_STANDARD 5.1). A XAML <c>KeyBinding</c> with <c>$parent[Window]</c> cannot reach its window (its
/// command stays null), so the binding is made in code by <see cref="SubWindowKeys"/>; the test checks what the binding runs (a real key press cannot be
/// used headless while a ScrollViewer has AllowAutoHide=False).
/// </summary>
public sealed class SubWindowEscapeTests
{
    private static void AssertEscapeCloses(Window window)
    {
        try
        {
            window.Show();
            var escape = Assert.Single(window.KeyBindings, k => k.Gesture.Key == Key.Escape);
            Assert.NotNull(escape.Command);
            escape.Command!.Execute(null);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public void TheBacktestWindowClosesOnEscape() => AssertEscapeCloses(new BacktestWindow());
    [AvaloniaFact] public void TheSettingsWindowClosesOnEscape() => AssertEscapeCloses(new SettingsWindow());
    [AvaloniaFact] public void TheScreenerWindowClosesOnEscapeAndKeepsEnterForTheScan() { var window = new ScreenerWindow(); Assert.Single(window.KeyBindings, k => k.Gesture.Key == Key.Enter); AssertEscapeCloses(window); }
    [AvaloniaFact] public void TheLogViewerClosesOnEscape() => AssertEscapeCloses(new LogViewerView());
}
