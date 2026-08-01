using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels;
using System;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Window for displaying progress of multiple synchronization tasks.
/// </summary>
public partial class MultiSyncProgressWindow : Window
{
    public MultiSyncProgressWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MultiSyncProgressViewModel vm)
        {
            // Wire up the close request from ViewModel to the Window close method
            vm.RequestClose += () => Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MultiSyncProgressViewModel vm)
        {
            vm.StopAllCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
