namespace StockAnalyzer.Avalonia.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// ViewModel for each individual drawing tool item in the flyout.
/// </summary>
public partial class DrawingToolItemViewModel : ObservableObject
{
    private readonly DrawingToolSidebarViewModel _parent;
    private readonly string? _customDisplayName;

    public DrawingTool Tool { get; }
    public string Icon { get; }
    public string NameKey { get; }
    public string DisplayName => _customDisplayName ?? LocalizationManager.Instance[NameKey];
    public IconItemInfo? IconInfo { get; }
    public global::Avalonia.Media.Imaging.Bitmap? Image { get; }

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isSelected;

    public DrawingToolItemViewModel(
        DrawingTool tool,
        string icon,
        string nameKey,
        DrawingToolSidebarViewModel parent,
        string? customDisplayName = null,
        global::Avalonia.Media.Imaging.Bitmap? image = null,
        IconItemInfo? iconInfo = null)
    {
        Tool = tool;
        Icon = icon;
        NameKey = nameKey;
        _parent = parent;
        _customDisplayName = customDisplayName;
        Image = image;
        IconInfo = iconInfo;
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
