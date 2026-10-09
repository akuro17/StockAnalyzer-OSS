using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Key bindings every sub-window shares (SA_UI_SUBWINDOW_STANDARD 5.1: Escape closes). They are made in code because a XAML KeyBinding written as
/// <c>Command="{Binding $parent[Window].Close}"</c> cannot reach its window and its command stays null, so the key does nothing.
/// </summary>
internal static class SubWindowKeys
{
    public static void CloseOnEscape(Window window)
        => window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(window.Close) });
}
