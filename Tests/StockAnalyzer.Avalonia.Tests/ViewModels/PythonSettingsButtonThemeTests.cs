using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Python Library Update card of Settings &gt; Python: two buttons laid out like the AI Predictions ONNX card, both with the "accent" class
/// (Background / Text / Hover / Pressed come from the Settings &gt; Theme Button colours).
/// </summary>
public sealed class PythonSettingsButtonThemeTests
{
    [AvaloniaFact]
    public void TheLibraryUpdateCard_HasTwoThemedButtonsBoundToTheViewModelCommands()
    {
        LocalizationManager.Instance.Initialize("en");
        using var vm = new PythonSettingsViewModel();
        var view = new PythonSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button)).ToArray();
            Assert.Equal(2, buttons.Length);   // update automatically, configure manually
            Assert.All(buttons, b => Assert.True(b.Classes.Contains("accent"),
                $"'{b.Content}' must use the accent class (theme Button Background / Text / Hover / Pressed)"));

            var auto = Assert.Single(buttons, b => Equals(b.Content, LocalizationManager.Instance["Btn_AutoUpdate"]));
            var manual = Assert.Single(buttons, b => Equals(b.Content, LocalizationManager.Instance["Btn_ConfigureManually"]));
            Assert.Same(vm.AutoUpdateLibrariesCommand, auto.Command);
            Assert.Same(vm.ManualUpdateLibrariesCommand, manual.Command);

            var titles = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.True(titles.IndexOf(LocalizationManager.Instance["Settings_Python_UpdateCheckTitle"]) >= 0);
            Assert.True(titles.IndexOf(LocalizationManager.Instance["PythonUpdate_Title"])
                > titles.IndexOf(LocalizationManager.Instance["Settings_Python_UpdateCheckTitle"]), "the library update card follows the update check card");
        }
        finally { window.Close(); }
    }
}
