using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using System;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        
        DataContextChanged += (s, e) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.RequestClose += () => Close();
            }
        };

        Closing += (s, e) => (DataContext as SettingsViewModel)?.OnClosing();
        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void OnCategoryTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is global::Avalonia.Visual visual)
        {
            var item = visual.FindAncestorOfType<TreeViewItem>();
            if (item != null)
            {
                // Only toggle if the item has children (like the "Chart" category)
                // In Avalonia 11, TreeViewItem has an Items property.
                if (item.ItemCount > 0)
                {
                    item.IsExpanded = !item.IsExpanded;
                }
            }
        }
    }
}
