using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models;
using System.Collections.ObjectModel;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class DataWindowItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private IndicatorColor _color;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _hasChildren = false;

    [ObservableProperty]
    private bool _useCustomColor = false;
    
    [ObservableProperty]
    private bool _showCheckbox = true;

    /// <summary>
    /// ID of the associated indicator. 
    /// Can be a string GUID or an IndicatorType name for group headers.
    /// </summary>
    [ObservableProperty]
    private string? _associateId;

    [ObservableProperty]
    private IRelayCommand? _toggleVisibilityCommand;

    [ObservableProperty]
    private IRelayCommand? _toggleGroupVisibilityCommand;

    [ObservableProperty]
    private IRelayCommand? _toggleGroupAllOnCommand;

    [ObservableProperty]
    private IRelayCommand? _toggleGroupAllOffCommand;

    [ObservableProperty]
    private IRelayCommand? _toggleAllOnCommand;

    [ObservableProperty]
    private IRelayCommand? _toggleAllOffCommand;

    [ObservableProperty]
    private IRelayCommand? _openSettingsCommand;


    public ObservableCollection<DataWindowItemViewModel> Children { get; } = new();
}
