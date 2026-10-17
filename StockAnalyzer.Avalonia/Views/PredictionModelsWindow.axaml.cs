using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Prediction Models (Tools menu): a modeless, ownerless window; <c>DialogService.ShowPredictionModelsWindowAsync</c> keeps one instance. It never closes
/// itself; closing it disposes the view model.
/// </summary>
public partial class PredictionModelsWindow : Window
{
    public PredictionModelsWindow()
    {
        InitializeComponent();
        SubWindowKeys.CloseOnEscape(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e) => BeginMoveDrag(e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
