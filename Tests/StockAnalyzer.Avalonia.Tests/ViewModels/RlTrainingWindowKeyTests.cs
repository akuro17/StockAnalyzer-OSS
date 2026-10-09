using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Views;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The single-symbol RL window's Escape binding. A XAML <c>KeyBinding</c> with <c>$parent[Window]</c> cannot reach its window (the command stays null), so
/// the binding is made in the code-behind; the test checks what it runs (a real key press cannot be used headless while a ScrollViewer has AllowAutoHide=False).
/// </summary>
public sealed class RlTrainingWindowKeyTests
{
    [AvaloniaFact]
    public void EscapeIsBoundToCloseAndEnterToTheTrainCommand()
    {
        var window = new RlTrainingWindow();
        try
        {
            window.Show();
            var escape = Assert.Single(window.KeyBindings, k => k.Gesture.Key == Key.Escape);
            Assert.NotNull(escape.Command);
            escape.Command!.Execute(null);
            Assert.False(window.IsVisible);
            Assert.Single(window.KeyBindings, k => k.Gesture.Key == Key.Enter);
        }
        finally { window.Close(); }
    }
}
