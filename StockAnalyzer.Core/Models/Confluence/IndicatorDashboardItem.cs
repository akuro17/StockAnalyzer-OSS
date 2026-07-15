using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// Pre-allocated item for the Confluence Dashboard to avoid GC.
/// Managed via a pool in the ViewModel for in-place updates.
/// </summary>
public partial class IndicatorDashboardItem : ObservableObject
{
    private static readonly PropertyChangedEventArgs NameArgs = new(nameof(Name));
    private static readonly PropertyChangedEventArgs ValueArgs = new(nameof(Value));
    private static readonly PropertyChangedEventArgs DirectionArgs = new(nameof(Direction));
    private static readonly PropertyChangedEventArgs GroupArgs = new(nameof(Group));
    private static readonly PropertyChangedEventArgs StrengthArgs = new(nameof(Strength));
    private static readonly PropertyChangedEventArgs WeightArgs = new(nameof(Weight));
    private static readonly PropertyChangedEventArgs IsActiveArgs = new(nameof(IsActive));

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value, NameArgs);
    }

    private double _value;
    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value, ValueArgs);
    }

    private SignalDirection _direction;
    public SignalDirection Direction
    {
        get => _direction;
        set => SetProperty(ref _direction, value, DirectionArgs);
    }

    private DecorrelationGroup _group;
    public DecorrelationGroup Group
    {
        get => _group;
        set => SetProperty(ref _group, value, GroupArgs);
    }

    private double _strength;
    public double Strength
    {
        get => _strength;
        set => SetProperty(ref _strength, value, StrengthArgs);
    }

    private double _weight;
    public double Weight
    {
        get => _weight;
        set => SetProperty(ref _weight, value, WeightArgs);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value, IsActiveArgs);
    }

    /// <summary>
    /// Updates the property without allocating new PropertyChangedEventArgs.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, PropertyChangedEventArgs args)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        OnPropertyChanging(args.PropertyName);
        field = value;
        OnPropertyChanged(args);
        return true;
    }
}
