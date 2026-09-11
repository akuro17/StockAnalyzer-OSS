using System;
using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views.Controls;

public partial class DrawingToolSidebar : UserControl
{
    private bool _isDropDownOpen;

    public DrawingToolSidebar()
    {
        InitializeComponent();
    }

    private void CategoryButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: DrawingToolCategoryViewModel category }
            && DataContext is DrawingToolSidebarViewModel vm)
        {
            vm.OpenCategory(category);
        }
    }

    private void ComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        _isDropDownOpen = true;
    }

    private void ComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        _isDropDownOpen = false;
    }

    private void Sidebar_PointerExited(object? sender, PointerEventArgs e)
    {
        if (_isDropDownOpen) return;

        var pos = e.GetPosition(this);
        if (pos.X >= 0 && pos.X <= Bounds.Width && pos.Y >= 0 && pos.Y <= Bounds.Height)
        {
            return;
        }

        if (DataContext is DrawingToolSidebarViewModel vm)
        {
            if (vm.IsIconDropDownOpen) return;
            vm.CloseFlyoutCommand.Execute(null);
        }
    }
}
