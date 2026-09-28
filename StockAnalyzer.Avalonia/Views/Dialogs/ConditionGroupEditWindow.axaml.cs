using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>Add/Edit Group dialog. Closing with OK returns the confirmed <c>ConditionGroupEditResult</c> as the dialog result; Cancel/Esc returns null.</summary>
public partial class ConditionGroupEditWindow : Window
{
    public ConditionGroupEditWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(DataContext is ConditionGroupEditDialogViewModel vm ? vm.BuildResult() : null);
    }

    public void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    public void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }
}
