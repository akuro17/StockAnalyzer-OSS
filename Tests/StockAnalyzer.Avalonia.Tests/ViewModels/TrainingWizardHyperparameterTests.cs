using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The wizard's hyperparameter rows pick the key from a list (per framework / architecture), the trainer's default fills the value, and every key explains
/// itself in a tooltip. Defaults themselves are checked against the Python trainers in Core.Tests (TrainingHyperparameterCatalogTests).
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardHyperparameterTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    public TrainingWizardHyperparameterTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static TrainingHyperparameterOption Option(string key) =>
        TrainingHyperparameterOption.From(TrainingHyperparameterCatalog.Find(key)!);

    private static string[] Keys(TrainingWizardViewModel wizard) => wizard.HyperparameterOptions.Select(o => o.Key).ToArray();

    [Fact]
    public void TheKeyListFollowsTheFrameworkAndArchitecture()
    {
        using var wizard = new TrainingWizardViewModel();
        Assert.Contains("lr", Keys(wizard));
        Assert.DoesNotContain("heads", Keys(wizard));

        wizard.SelectedArchitecture = AttentionContract.Architecture;
        Assert.Contains("heads", Keys(wizard));

        wizard.SelectedFramework = TrainingFramework.LightGBM;
        Assert.Contains("n-estimators", Keys(wizard));
        Assert.DoesNotContain("epochs", Keys(wizard));
        Assert.DoesNotContain("heads", Keys(wizard));
    }

    [Fact]
    public void ChoosingAKeyFillsTheTrainersDefault_ButNeverOverwritesATypedValue()
    {
        var entry = new TrainingHyperparameterEntry();

        entry.SelectedOption = Option("epochs");
        Assert.Equal("epochs", entry.Key);
        Assert.Equal("15", entry.Value);

        entry.SelectedOption = Option("batch"); // the value was still the previous key's default
        Assert.Equal("256", entry.Value);

        entry.Value = "512"; // typed by the owner
        entry.SelectedOption = Option("lr");
        Assert.Equal("512", entry.Value);

        entry.SelectedOption = null; // what a list does while its items are replaced
        Assert.Equal("lr", entry.Key);
    }

    [Fact]
    public void ARowTheNewFrameworkRejectsIsRemovedWithANotice_AndOthersStay()
    {
        using var wizard = new TrainingWizardViewModel();
        wizard.Hyperparameters.Add(new TrainingHyperparameterEntry { Key = "hidden" });
        wizard.Hyperparameters.Add(new TrainingHyperparameterEntry { Key = "seed" });
        wizard.Hyperparameters.Add(new TrainingHyperparameterEntry()); // not chosen yet
        Assert.Equal("64", wizard.Hyperparameters[0].Value);

        wizard.SelectedFramework = TrainingFramework.LightGBM;

        Assert.Equal(new[] { "seed", "" }, wizard.Hyperparameters.Select(h => h.Key).ToArray());
        Assert.NotNull(wizard.HyperparameterNotice);
        Assert.Contains("hidden", wizard.HyperparameterNotice);

        wizard.AddHyperparameterCommand.Execute(null);
        Assert.Null(wizard.HyperparameterNotice);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryKeyHasATooltipInBothLanguages(string language)
    {
        LocalizationManager.Instance.Initialize(language);
        foreach (var definition in TrainingHyperparameterCatalog.All)
        {
            var option = TrainingHyperparameterOption.From(definition);
            Assert.False(string.IsNullOrWhiteSpace(option.Description), definition.Key);
            Assert.DoesNotContain("TrainingWizard_Hp_", option.Description);
        }
    }

    [AvaloniaFact]
    public void TheWindowOffersAKeyList_WhoseItemsExplainThemselvesOnHover_AndTheValueGetsItsDefault()
    {
        var wizard = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = wizard };
        try
        {
            window.Show();
            wizard.AddHyperparameterCommand.Execute(null);
            Pump();

            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.PlaceholderText == LocalizationManager.Instance["TrainingWizard_Hyperparameters_KeyWatermark"]);
            Assert.Equal(wizard.HyperparameterOptions.Count, combo.ItemCount);

            combo.IsDropDownOpen = true;
            Pump();
            var items = Enumerable.Range(0, combo.ItemCount).Select(i => combo.ContainerFromIndex(i)).OfType<ComboBoxItem>().ToArray();
            Assert.Equal(wizard.HyperparameterOptions.Count, items.Length);
            Assert.All(items, item =>
            {
                var option = Assert.IsType<TrainingHyperparameterOption>(item.DataContext);
                Assert.Equal(option.Description, ToolTip.GetTip(item));
            });

            combo.SelectedItem = wizard.HyperparameterOptions.Single(o => o.Key == "epochs");
            Pump();
            var entry = Assert.Single(wizard.Hyperparameters);
            Assert.Equal("epochs", entry.Key);
            Assert.Equal("15", entry.Value);
            Assert.Equal(entry.KeyDescription, ToolTip.GetTip(combo));
            Assert.False(string.IsNullOrEmpty(entry.KeyDescription));
        }
        finally
        {
            window.Close();
            wizard.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARowKeepsItsKeyShown_WhenTheArchitectureChangesAndTheKeyStillApplies()
    {
        var wizard = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = wizard };
        try
        {
            window.Show();
            wizard.Hyperparameters.Add(new TrainingHyperparameterEntry { Key = "lr" });
            Pump();
            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.PlaceholderText == LocalizationManager.Instance["TrainingWizard_Hyperparameters_KeyWatermark"]);
            Assert.Equal("lr", Assert.IsType<TrainingHyperparameterOption>(combo.SelectedItem).Key);

            wizard.SelectedArchitecture = AttentionContract.Architecture; // the list is replaced; "lr" still applies
            Pump();

            Assert.Equal("lr", wizard.Hyperparameters.Single().Key);
            Assert.Equal("lr", Assert.IsType<TrainingHyperparameterOption>(combo.SelectedItem).Key);
        }
        finally
        {
            window.Close();
            wizard.Dispose();
        }
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
