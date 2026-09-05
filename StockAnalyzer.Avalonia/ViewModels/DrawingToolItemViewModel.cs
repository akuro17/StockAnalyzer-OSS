namespace StockAnalyzer.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

/// <summary>
/// ViewModel for each individual drawing tool item in the flyout.
/// </summary>
public partial class DrawingToolItemViewModel : ObservableObject
{
    private readonly DrawingToolSidebarViewModel _parent;

    public DrawingTool Tool { get; }
    public string Icon { get; }
    public string NameKey { get; }
    public string DisplayName => LocalizationManager.Instance[NameKey];

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isSelected;

    public DrawingToolItemViewModel(
        DrawingTool tool,
        string icon,
        string nameKey,
        DrawingToolSidebarViewModel parent)
    {
        Tool = tool;
        Icon = icon;
        NameKey = nameKey;
        _parent = parent;
    }

    [RelayCommand]
    private void Select()
    {
        _parent.SelectTool(this);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        IsFavorite = !IsFavorite;
        _parent.UpdateFavorites(this);
    }
}
