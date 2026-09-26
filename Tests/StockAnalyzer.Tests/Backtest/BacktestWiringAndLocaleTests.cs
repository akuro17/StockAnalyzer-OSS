using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Task 17 acceptance tests for the MainWindow menu wiring (spec §5.6) and locale key parity (spec §5.6 / DT-4).</summary>
public class BacktestWiringAndLocaleTests
{
    /// <summary>
    /// DialogService.ShowBacktestWindowAsync (mirrors the existing ShowScreenerDialogAsync) resolves
    /// BacktestWindowViewModel from the app's real DI container and assigns it as a new BacktestWindow's
    /// DataContext; actually showing the OS window itself is exercised once, at the final stage, by
    /// Task 18's UI test (repo policy: UI tests do not run during iterative development). What this
    /// test verifies without an Application/UI thread is the two things that would break "opening the
    /// window" if this feature regressed: (a) the real DI graph can fully construct
    /// BacktestWindowViewModel end to end - a missing/broken registration would throw here exactly as
    /// it would for a user clicking the menu item - and (b) the menu item is actually wired to the
    /// command that starts that path.
    /// </summary>
    [Fact]
    public async Task MainWindow_OpenBacktest_CreatesWindow()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddCommonServices(configuration);
        // BacktestWindowViewModel now depends on IMarketDataProvider (ParquetMarketDataProvider),
        // whose construction chain instantiates other singletons including PythonService, which is
        // IAsyncDisposable-only - a plain `using`/Dispose() throws once such a singleton actually
        // exists in the container (see the InvalidOperationException this replaces).
        await using ServiceProvider provider = services.BuildServiceProvider();

        var vm = provider.GetRequiredService<BacktestWindowViewModel>();

        Assert.NotNull(vm);
        Assert.NotNull(vm.IndicatorSelection);
        Assert.NotNull(vm.Results);

        string axaml = File.ReadAllText(Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Views", "MainWindow.axaml"));
        Assert.Contains("Command=\"{Binding OpenBacktestWindowCommand}\"", axaml);
        Assert.Contains("{l:Localize Menu_Backtest}", axaml);
    }

    /// <summary>Guards this feature's own key subset (spec §5.6 "キー集合一致・重複なし") - the app-wide
    /// locale-parity guard for every key lives in LocalizationKeyCoverageTests; this test is scoped to
    /// Backtest_* so it stays a fast, self-contained part of this feature's own acceptance suite.</summary>
    [Fact]
    public void LocaleKeys_EnJa_SetMatch()
    {
        string enPath = Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", "en.json");
        string jaPath = Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", "ja.json");

        HashSet<string> enKeys = LoadBacktestKeys(enPath);
        HashSet<string> jaKeys = LoadBacktestKeys(jaPath);

        Assert.NotEmpty(enKeys);

        List<string> onlyInEn = enKeys.Except(jaKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> onlyInJa = jaKeys.Except(enKeys).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(
            onlyInEn.Count == 0 && onlyInJa.Count == 0,
            $"Backtest_* locale key sets diverge. Only in en.json: [{string.Join(", ", onlyInEn)}]. Only in ja.json: [{string.Join(", ", onlyInJa)}].");
    }

    [Fact]
    public void ConfigurationView_LocalizesSizingModelsAndUsesDomainBounds()
    {
        BacktestWindowViewModel viewModel = BacktestTestFactory.CreateViewModel();
        Assert.Equal(BacktestConfigurationBounds.InclusiveRatioMinimum, viewModel.SlippageRatioMinimum);
        Assert.Equal(BacktestConfigurationBounds.BelowUnitRatioMaximum, viewModel.SlippageRatioMaximum);
        Assert.Equal(BacktestConfigurationBounds.PositiveRatioMinimum, viewModel.InitialMarginRatioMinimum);
        Assert.Equal(BacktestConfigurationBounds.InclusiveRatioMaximum, viewModel.InitialMarginRatioMaximum);
        Assert.Equal(BacktestConfigurationBounds.PositiveRatioMinimum, viewModel.MaintenanceMarginRatioMinimum);
        Assert.Equal(BacktestConfigurationBounds.BelowUnitRatioMaximum, viewModel.MaintenanceMarginRatioMaximum);
        Assert.Equal(BacktestConfigurationBounds.InclusiveRatioMinimum, viewModel.LiquidationPenaltyRatioMinimum);
        Assert.Equal(BacktestConfigurationBounds.BelowUnitRatioMaximum, viewModel.LiquidationPenaltyRatioMaximum);

        string viewPath = Path.Combine(
            RepoPaths.Root,
            "StockAnalyzer.Avalonia",
            "Views",
            "Backtest",
            "BacktestConfigurationView.axaml");
        XDocument document = XDocument.Load(viewPath);
        XNamespace avalonia = "https://github.com/avaloniaui";

        XElement sizingModel = document
            .Descendants(avalonia + "ComboBox")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding AvailableSizingModels}");
        Assert.Contains(
            "EnumToLocalizedDisplayNameConverter",
            sizingModel.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);

        AssertBoundTo("SlippageRatio", "SlippageRatioMinimum", "SlippageRatioMaximum");
        AssertBoundTo("InitialMarginRatio", "InitialMarginRatioMinimum", "InitialMarginRatioMaximum");
        AssertBoundTo("MaintenanceMarginRatio", "MaintenanceMarginRatioMinimum", "MaintenanceMarginRatioMaximum");
        AssertBoundTo("LiquidationPenaltyRatio", "LiquidationPenaltyRatioMinimum", "LiquidationPenaltyRatioMaximum");

        AssertLocaleContains("en", "Enum_PositionSizingModel_FixedQuantity", "Enum_PositionSizingModel_PercentOfEquity");
        AssertLocaleContains("ja", "Enum_PositionSizingModel_FixedQuantity", "Enum_PositionSizingModel_PercentOfEquity");

        void AssertBoundTo(string valueProperty, string minimumProperty, string maximumProperty)
        {
            XElement input = document
                .Descendants(avalonia + "NumericUpDown")
                .Single(element => (string?)element.Attribute("Value") == $"{{Binding {valueProperty}}}");

            Assert.Equal($"{{Binding {minimumProperty}}}", (string?)input.Attribute("Minimum"));
            Assert.Equal($"{{Binding {maximumProperty}}}", (string?)input.Attribute("Maximum"));
        }
    }

    private static HashSet<string> LoadBacktestKeys(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => name.StartsWith("Backtest_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertLocaleContains(string languageCode, params string[] keys)
    {
        string path = Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{languageCode}.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (string key in keys)
        {
            Assert.True(document.RootElement.TryGetProperty(key, out _), $"{languageCode}.json is missing '{key}'.");
        }
    }
}
