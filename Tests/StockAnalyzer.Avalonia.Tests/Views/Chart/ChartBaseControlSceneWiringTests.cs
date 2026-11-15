using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// The control's layered scene keeps references to the objects of its view model's <c>ChartObjectManager</c> between frames, so it must be registered with
/// that manager (to be told about every removal before the disposal is decided, D-3) for as long as the control has that view model, and with no other.
/// The chart creates its draw operations on the shared default messenger, so the test runs in the non-parallel collection for tests that use it.
/// </summary>
[Collection("MessengerSharedState")]
public class ChartBaseControlSceneWiringTests
{
    [AvaloniaFact]
    public void TheSceneIsRegisteredWithTheManagerOfTheCurrentViewModelOnly()
    {
        var first = new ChartViewModel();
        var second = new ChartViewModel();

        var chart = new ChartBaseControl { ViewModel = first };
        Assert.Equal(1, first.ObjectManager.RetirementListenerCount);

        chart.ViewModel = second;
        Assert.Equal(0, first.ObjectManager.RetirementListenerCount);
        Assert.Equal(1, second.ObjectManager.RetirementListenerCount);

        chart.ViewModel = null;
        Assert.Equal(0, second.ObjectManager.RetirementListenerCount);
    }

    [AvaloniaFact]
    public void TheSameViewModelSetAgain_DoesNotRegisterTheSceneTwice()
    {
        var viewModel = new ChartViewModel();
        var chart = new ChartBaseControl { ViewModel = viewModel };

        chart.ViewModel = null;
        chart.ViewModel = viewModel;

        Assert.Equal(1, viewModel.ObjectManager.RetirementListenerCount);
    }
}
