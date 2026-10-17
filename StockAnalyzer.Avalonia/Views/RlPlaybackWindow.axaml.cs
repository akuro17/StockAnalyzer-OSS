using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// RL Playback (Tools menu): a modeless, ownerless window; <c>DialogService.ShowRlPlaybackWindowAsync</c> keeps one instance. A successful replay leaves the window open;
/// closing it (Close, Esc) disposes the view model, which cancels a running replay.
/// </summary>
public partial class RlPlaybackWindow : Window
{
    public RlPlaybackWindow()
    {
        InitializeComponent();
        SubWindowKeys.CloseOnEscape(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e) => BeginMoveDrag(e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
