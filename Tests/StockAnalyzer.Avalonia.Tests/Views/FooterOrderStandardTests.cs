using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// SA_UI_SUBWINDOW_STANDARD 1.1: the operational buttons are right-aligned and the primary action comes FIRST
/// ([OK][Cancel][Apply]). The three training windows (Training Wizard, RL, Portfolio RL) are pinned to it. The wizard check lives
/// here; the RL and Portfolio RL checks sit next to the fixtures of their own windows and use <see cref="AssertPrimaryFirst"/>.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class FooterOrderStandardTests
{
    /// <summary>The theme resource key every primary action button uses.</summary>
    public const string PrimaryButtonThemeKey = "PrimaryButtonStyle";

    /// <summary>Asserts that <paramref name="primary"/> is visible, uses the primary theme and sits left of every button in <paramref name="followers"/> (which are visible too).</summary>
    public static void AssertPrimaryFirst(Window window, Button primary, params Button[] followers)
    {
        Assert.True(primary.IsVisible, "the primary action is shown");
        Assert.NotNull(primary.Theme);
        Assert.Same(Application.Current!.FindResource(PrimaryButtonThemeKey), primary.Theme);
        double X(Button b) => b.TranslatePoint(new Point(0, 0), window)!.Value.X;
        foreach (var follower in followers)
        {
            Assert.True(follower.IsVisible, "the follower button is shown");
            Assert.NotSame(primary.Theme, follower.Theme);
            Assert.True(X(primary) < X(follower), "the primary action sits left of the other operational buttons");
        }
        for (int i = 1; i < followers.Length; i++)
            Assert.True(X(followers[i - 1]) < X(followers[i]), "the follower buttons keep their given order");
    }

    public static void Pump()
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    [AvaloniaFact]
    public void TrainingWizard_StartIsPrimaryAndLeftOfCancel()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
            var start = buttons.Single(b => ReferenceEquals(b.Command, vm.StartTrainingCommand));
            var cancel = buttons.Single(b => ReferenceEquals(b.Command, vm.CancelTrainingCommand));
            AssertPrimaryFirst(window, start, cancel);
        }
        finally { window.Close(); vm.Dispose(); }
    }
}
