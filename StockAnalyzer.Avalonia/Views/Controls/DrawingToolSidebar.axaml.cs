using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views.Controls;

public partial class DrawingToolSidebar : UserControl
{
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

    private void Sidebar_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is DrawingToolSidebarViewModel vm)
        {
            vm.CloseFlyoutCommand.Execute(null);
        }
    }
}
