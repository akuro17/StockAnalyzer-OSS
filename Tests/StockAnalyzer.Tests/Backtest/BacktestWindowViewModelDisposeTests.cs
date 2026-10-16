using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// The Backtest window view-model owns its <see cref="BacktestResultsViewModel"/>, which subscribes to the process-wide
/// <c>LocalizationManager.Instance.LocaleChanged</c> event. Disposing the window view-model must therefore end that subscription.
/// The current language is re-initialized (not changed): the strings are reloaded from the default resources, so the class is part of the
/// "LocalizationSharedState" collection like every test that calls <c>Initialize</c>.
/// </summary>
[Collection("LocalizationSharedState")]
public class BacktestWindowViewModelDisposeTests
{
    private static void RaiseLocaleChanged() =>
        LocalizationManager.Instance.Initialize(LocalizationManager.Instance.CurrentLanguage);

    [Fact]
    public void Dispose_UnsubscribesResultsFromLocaleChanges()
    {
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel();
        int raised = 0;
        vm.Results.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BacktestResultsViewModel.SelectedTradePageSize)) raised++;
        };

        RaiseLocaleChanged();
        Assert.True(raised >= 1, "The Results view-model must react to a locale reload while the window view-model is alive.");

        raised = 0;
        vm.Dispose();
        RaiseLocaleChanged();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel();

        vm.Dispose();
        vm.Dispose();
    }
}
