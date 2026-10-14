using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.Views;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    private const double OrderTolerance = 0.5;

    /// <summary>
    /// S3a: the Tab order (document order) equals the visual order of the inputs: Symbol, Start, End | Capital, Flat cost, Unit cost, Slippage, Liquidation |
    /// Continuous policy, Trial run, Short history | Train. Controls on one row are ordered left to right, rows top to bottom.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsDocumentOrderEqualsVisualOrder_BasicThenAdvanced()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy = new() };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        var window = new RlTrainingWindow { DataContext = vm };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            string[] ids =
            [
                RlUiConstants.Symbol, RlUiConstants.StartDate, RlUiConstants.EndDate,
                RlUiConstants.Capital, RlUiConstants.FlatCommission, RlUiConstants.UnitCommission, RlUiConstants.Slippage, RlUiConstants.Liquidation,
                RlUiConstants.ContinuousMode, RlUiConstants.Smoke, RlUiConstants.ShortHistory, RlUiConstants.Train,
            ];
            var all = window.GetVisualDescendants().OfType<Control>().ToList();
            var controls = ids.Select(id => all.First(c => AutomationProperties.GetAutomationId(c) == id)).ToArray();
            Assert.All(controls, c => Assert.True(c.IsEffectivelyVisible, AutomationProperties.GetAutomationId(c)));
            // document order = position in the visual tree walk
            Assert.Equal(controls.Select(c => all.IndexOf(c)).OrderBy(i => i), controls.Select(c => all.IndexOf(c)));
            for (int i = 1; i < controls.Length; i++)
            {
                var previous = controls[i - 1].TranslatePoint(new Point(0, 0), window)!.Value;
                var current = controls[i].TranslatePoint(new Point(0, 0), window)!.Value;
                bool nextRow = current.Y > previous.Y + OrderTolerance;
                bool sameRowToTheRight = Math.Abs(current.Y - previous.Y) <= OrderTolerance && current.X > previous.X;
                Assert.True(nextRow || sameRowToTheRight, $"{ids[i]} must follow {ids[i - 1]} visually");
            }
            // the three group headers use the shared style and sit where the sequence changes group: Basic above Symbol, Advanced above Continuous, Results below the training card
            TrainingGroupHeaderTests.AssertSharedHeaderValues(window, "Basic Settings", "Advanced Settings", "Results and management");
            var headers = all.OfType<TextBlock>().Where(t => t.Classes.Contains("groupHeader")).ToArray();
            double Top(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(Top(headers[0]) < Top(controls[0]));
            Assert.True(Top(controls[4]) < Top(headers[1]) && Top(headers[1]) < Top(controls[8]));     // Flat cost (Basic) | Continuous policy (Advanced)
            Assert.True(Top(controls[10]) < Top(headers[2]));                                          // Short history (Advanced) above the Results group
        }
        finally { window.Close(); }
    }
}
