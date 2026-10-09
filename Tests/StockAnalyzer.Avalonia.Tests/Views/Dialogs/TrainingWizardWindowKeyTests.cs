using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// The training wizard's Escape binding (SA_UI_SUBWINDOW_STANDARD 5.1). A XAML <c>KeyBinding</c> with <c>$parent[Window]</c> cannot reach its window, so the
/// binding is made in the code-behind; the test checks what it runs (a real key press cannot be used headless while a ScrollViewer has AllowAutoHide=False).
/// </summary>
public sealed class TrainingWizardWindowKeyTests
{
    [AvaloniaFact]
    public void EscapeClosesTheWindowAndEnterStartsTheTraining()
    {
        var window = new TrainingWizardWindow { DataContext = new TrainingWizardViewModel() };
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
