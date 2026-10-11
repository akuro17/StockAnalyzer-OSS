using System.Linq;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Every plain button of the AI Predictions page takes the theme's Button colours: the "accent" class reads Background / Text / Hover / Pressed from
/// the Theme page. The add-model-from-file behaviour itself is covered in Tests/ViewModels/AIPredictionsSettingsViewModelTests (real registry).
/// </summary>
public sealed class AIPredictionsButtonThemeTests
{
    [AvaloniaFact]
    public void EveryButtonOfTheAiPredictionsPage_TakesTheThemeButtonColours()
    {
        LocalizationManager.Instance.Initialize("en");
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(PredictionSettingsManager.DefaultWindowSize);
        using var vm = new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(), Mock.Of<IToastNotificationService>());
        var view = new AIPredictionsSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // RepeatButton (the numeric spinners) derives from Button but belongs to those controls, not to the page.
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button)).ToArray();
            Assert.True(buttons.Length >= 15, "the page's buttons are mounted");
            Assert.All(buttons, b => Assert.True(b.Classes.Contains("accent"),
                $"'{b.Content}' must use the accent class (theme Button Background / Text / Hover / Pressed)"));
            Assert.Contains(buttons, b => Equals(b.Content, LocalizationManager.Instance["Settings_ModelGenerations_AddFile"]));
            Assert.DoesNotContain(buttons, b => Equals(b.Content, LocalizationManager.Instance["Settings_ModelGenerations_Activate"])
                || Equals(b.Content, LocalizationManager.Instance["Settings_ModelGenerations_Deactivate"]));   // the row check box is the only activation control
            Assert.DoesNotContain("...", LocalizationManager.Instance["Settings_ModelGenerations_AddFile"]);
            Assert.DoesNotContain("\u2026", LocalizationManager.Instance["Settings_ModelGenerations_AddFile"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ThePredictionColorRow_ShowsAColorPickerBoundToTheThemeAttentionColor()
    {
        LocalizationManager.Instance.Initialize("en");
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(PredictionSettingsManager.DefaultWindowSize);
        var theme = new ThemeManager(); // in memory only: nothing here saves
        using var vm = new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(), Mock.Of<IToastNotificationService>(),
            themeManager: theme);
        var view = new AIPredictionsSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var picker = view.GetVisualDescendants().OfType<ColorPicker>().Single();
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == LocalizationManager.Instance["Settings_AIPredictions_Item_PredictionColor"]);

            var before = theme.CurrentTheme.AttentionHeat;
            picker.Color = global::Avalonia.Media.Color.FromArgb(0x80, 0x11, 0x22, 0x33);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new StockAnalyzer.Core.Models.IndicatorColor(0x80, 0x11, 0x22, 0x33), theme.CurrentTheme.AttentionHeat);
            Assert.NotEqual(before, theme.CurrentTheme.AttentionHeat);
            Assert.True(vm.IsModified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheEnsembleSectionComesBeforeTheModelDetails()
    {
        LocalizationManager.Instance.Initialize("en");
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(PredictionSettingsManager.DefaultWindowSize);
        using var vm = new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(), Mock.Of<IToastNotificationService>());
        var view = new AIPredictionsSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var titles = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            var ensemble = titles.IndexOf(LocalizationManager.Instance["Settings_Ensemble_Title"]);
            var details = titles.IndexOf(LocalizationManager.Instance["ModelAnalysis_DetailsTitle"]);
            Assert.True(ensemble >= 0 && details >= 0);
            Assert.True(ensemble < details, "the ensemble block is shown above Model Details");
            Assert.True(titles.IndexOf(LocalizationManager.Instance["Settings_ModelGenerations_Title"]) < ensemble);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheActiveGenerationIsATickedCheckBox_WithTheWholeRowInAMultiLineTip_AndNoHighlightOrDot()
    {
        LocalizationManager.Instance.Initialize("en");
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(PredictionSettingsManager.DefaultWindowSize);
        var registry = new Mock<IModelGenerationRegistry>();
        registry.Setup(r => r.List()).Returns(new[]
        {
            new ModelGenerationSummary(new string('a', 64), "a_very_long_model_file_name_that_pushes_everything_to_the_right_20261011-041941277-19d09b96.onnx",
                "classification", "daily", 1, 0.2, "rev", false),
            new ModelGenerationSummary(new string('b', 64), "active_model.onnx", "classification", "daily", 1, 0.3, "rev", true),
        });
        using var vm = new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(), Mock.Of<IToastNotificationService>(),
            modelRegistry: registry.Object);
        var view = new AIPredictionsSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            vm.GenerationGroupingTask.GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, vm.GenerationRows));
            var containers = Enumerable.Range(0, list.ItemCount).Select(i => (ListBoxItem)list.ContainerFromIndex(i)!).ToArray();
            var header = containers.Single(c => ((GenerationRow)c.DataContext!).IsHeader);
            Assert.False(header.IsEnabled, "a group header cannot be selected");
            var itemContainers = containers.Where(c => ((GenerationRow)c.DataContext!).IsItem).ToArray();
            var rows = itemContainers.Select(item => item.GetVisualDescendants().OfType<Border>().First(b => b.Padding == new global::Avalonia.Thickness(6, 3)))
                .ToArray();
            var texts = rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>().ToArray()).ToArray();
            var boxes = rows.Select(r => r.GetVisualDescendants().OfType<CheckBox>().Single()).ToArray();
            Assert.All(itemContainers, c => Assert.Equal(0, c.Padding.Left)); // the row (and its highlight) fills the whole item frame
            Assert.All(itemContainers, c => Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, c.HorizontalContentAlignment));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationManager.Instance["Settings_ModelGenerations_Group_NotCapable"], 2)); // the mock registry exposes no manifests

            Assert.All(rows, r => Assert.Null(r.Background)); // the badge alone marks the active row; there is no highlight
            Assert.All(rows, r => Assert.False(r.Classes.Contains("active")));
            Assert.False(boxes[0].IsChecked);                  // the check box starts the row, so it is seen however long the file name is
            Assert.True(boxes[1].IsChecked);
            Assert.Same(vm.ToggleGenerationRowActivationCommand, boxes[0].Command);
            Assert.DoesNotContain(texts.SelectMany(t => t), t => t.Text == LocalizationManager.Instance["Settings_ModelGenerations_ActiveBadge"]);   // no "Active" text any more
            Assert.DoesNotContain(texts.SelectMany(t => t), t => t.Text == "\u25CF");

            // The hover tip repeats the whole row, one fact per line.
            var tip = (string)global::Avalonia.Controls.ToolTip.GetTip(rows[0])!;
            var lines = tip.Split('\n');
            Assert.Equal(6, lines.Length);
            Assert.Contains("a_very_long_model_file_name_that_pushes_everything_to_the_right_20261011-041941277-19d09b96.onnx", lines[0]);
            Assert.Contains("classification", lines[1]);
            Assert.Contains("daily", lines[2]);
            Assert.Contains("1", lines[3]);
            Assert.Contains("0.2", lines[4]);
            Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Tip_NotActive"], lines[5]);
            Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Tip_Active"],
                ((string)global::Avalonia.Controls.ToolTip.GetTip(rows[1])!).Split('\n')[5]);
        }
        finally { window.Close(); }
    }
}
