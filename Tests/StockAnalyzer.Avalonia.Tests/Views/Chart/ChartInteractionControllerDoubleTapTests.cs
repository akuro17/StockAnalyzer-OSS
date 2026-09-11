using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using System;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

public class ChartInteractionControllerDoubleTapTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    // Regression test: a Shift+Click that lands within Avalonia's double-tap
    // gesture window (e.g. shortly after a prior click on the same object) must
    // never open the drawing settings/edit dialog — Shift is reserved for the
    // delete action. Previously HandleDoubleTap ignored modifier keys entirely,
    // so this scenario opened the editor instead of deleting the object.
    [Theory]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    public void HandleDoubleTap_DeleteOrCancelModifierHeld_ReturnsFalse(KeyModifiers modifiers)
    {
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        var transform = MakeTransform();

        var result = controller.HandleDoubleTap(
            new global::Avalonia.Point(10, 10),
            new global::Avalonia.Point(10, 10),
            viewModel,
            transform,
            snapshot: null!,
            modifiers: modifiers);

        Assert.False(result);
    }

    [Fact]
    public void HandleDoubleTap_NoModifiers_ViewModelNull_ReturnsFalse()
    {
        // Baseline sanity check that the method still behaves for the ordinary
        // (no-modifier) path when there is nothing to edit.
        var controller = new ChartInteractionController();
        var transform = MakeTransform();

        var result = controller.HandleDoubleTap(
            new global::Avalonia.Point(10, 10),
            new global::Avalonia.Point(10, 10),
            viewModel: null,
            transform,
            snapshot: null!,
            modifiers: KeyModifiers.None);

        Assert.False(result);
    }
}
