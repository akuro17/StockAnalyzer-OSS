using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The wizard reads its architecture lists and defaults from the Core catalog (one definition), and its public surface is unchanged.</summary>
public class TrainingWizardArchitectureCatalogTests
{
    [Theory]
    [InlineData(TrainingFramework.PyTorch)]
    [InlineData(TrainingFramework.LightGBM)]
    [InlineData(TrainingFramework.TensorFlow)]
    public void Wizard_OffersExactlyTheCatalogListAndStartsWithTheCatalogDefault(TrainingFramework framework)
    {
        using var wizard = new TrainingWizardViewModel();
        wizard.SelectedFramework = framework == TrainingFramework.PyTorch ? TrainingFramework.TensorFlow : TrainingFramework.PyTorch;
        wizard.SelectedFramework = framework;

        Assert.Equal(TrainingArchitectureCatalog.For(framework), wizard.AvailableArchitectures);
        Assert.Same(TrainingArchitectureCatalog.For(framework), wizard.AvailableArchitectures);
        Assert.Equal(TrainingArchitectureCatalog.DefaultFor(framework), wizard.SelectedArchitecture);
        Assert.Equal(TrainingArchitectureCatalog.For(framework).Count > 1, wizard.HasArchitectureChoice);
    }

    [Fact]
    public void Wizard_EveryFrameworkValueHasAnArchitectureList()
    {
        using var wizard = new TrainingWizardViewModel();

        foreach (TrainingFramework framework in System.Enum.GetValues<TrainingFramework>())
        {
            wizard.SelectedFramework = framework;
            Assert.NotEmpty(wizard.AvailableArchitectures);
        }
    }
}
