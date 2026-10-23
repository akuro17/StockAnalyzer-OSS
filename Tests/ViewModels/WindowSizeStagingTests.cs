using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Settings;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

/// <summary>
/// Finding F4 of the model-execution separation plan, fixed: a Window Size typed on Settings > AI Predictions stays in the page until Apply/OK, so
/// <see cref="StockAnalyzerSettings.PredictionWindowSize"/> (which model activation is checked against) keeps the saved value; Cancel has nothing to take back.
/// Real manager and real settings object; nothing is read from or written to disk until <c>SaveChangesAsync</c>, which the tests do not call.
/// </summary>
public class WindowSizeStagingTests
{
    private const int SavedSize = 60;
    private const int EditedSize = 90;

    private static StockAnalyzerSettings CreateSettings(IPredictionSettingsManager manager) => new(
        new ConfigurationBuilder().Build(),
        Options.Create(new PythonSettings()),
        Options.Create(new ChartDefaultSettings()),
        Options.Create(new PredictionSettings()),
        Options.Create(new ScreenerSettings()),
        Options.Create(new SmartScreenerSettings()),
        Options.Create(new InfrastructureSettings()),
        Options.Create(new MarketStructureSettings()),
        Options.Create(new PatternRecognitionSettings()),
        Options.Create(new ResilienceSettings()),
        Options.Create(new LocalizationSettings()),
        predictionSettingsManager: manager);

    private static PredictionSettingsManager CreateManagerHoldingTheSavedSize()
    {
        var manager = new PredictionSettingsManager();
        manager.SetWindowSize(SavedSize);
        return manager;
    }

    [Fact]
    public void WindowSizeEditedInSettings_IsNotVisibleToPredictionWindowSizeBeforeSave()
    {
        var manager = CreateManagerHoldingTheSavedSize();
        var settings = CreateSettings(manager);
        using var page = new AIPredictionsSettingsViewModel(manager);
        Assert.Equal(SavedSize, settings.PredictionWindowSize);

        page.SelectedWindowSize = EditedSize;   // typed into the page, Apply/OK not pressed

        Assert.True(page.IsModified);
        Assert.Equal(EditedSize, page.SelectedWindowSize);
        Assert.Equal(SavedSize, manager.WindowSize);
        Assert.Equal(SavedSize, settings.PredictionWindowSize);
    }

    [Fact]
    public void RevertAndResetLeaveTheManagerAtTheSavedWindowSize()
    {
        var manager = CreateManagerHoldingTheSavedSize();
        var settings = CreateSettings(manager);
        using var page = new AIPredictionsSettingsViewModel(manager);
        page.SelectedWindowSize = EditedSize;

        page.ResetToDefault();
        Assert.Equal(SavedSize, settings.PredictionWindowSize);

        page.SelectedWindowSize = EditedSize;
        page.RevertChanges();   // Cancel

        Assert.Equal(SavedSize, settings.PredictionWindowSize);
        Assert.Equal(SavedSize, page.SelectedWindowSize);
        Assert.False(page.IsModified);
    }

    [Fact]
    public async Task SaveChangesAsync_IsTheOnlyPlaceTheWindowSizeReachesTheManager()
    {
        var manager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        using var page = new AIPredictionsSettingsViewModel(manager);
        var saved = manager.WindowSize;
        page.SelectedWindowSize = saved + 30;
        Assert.Equal(saved, manager.WindowSize);

        await page.SaveChangesAsync();

        Assert.Equal(saved + 30, manager.WindowSize);
        Assert.False(page.IsModified);
    }
}
