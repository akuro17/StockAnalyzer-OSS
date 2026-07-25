using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

using StockAnalyzer.Core.Models.Confluence;

namespace StockAnalyzer.Core.Models;

public partial class CoreIndicatorSettings : ObservableObject
{
    /// <summary>
    /// Weight used for this indicator in the Signal Orchestrator (0.0 to 5.0).
    /// </summary>
    public double ConfluenceWeight { get; set; } = 1.0;
    public bool ShowSignals { get; set; } = false;

    /// <summary>
    /// Decorrelation group for this indicator to prevent multi-collinearity score inflation.
    /// </summary>
    public DecorrelationGroup ConfluenceGroup { get; set; } = DecorrelationGroup.None;

    public IndicatorType? TypeEnum { get; set; }
    public CoreIndicatorCategory Category { get; set; } = CoreIndicatorCategory.Other;
    [ObservableProperty]
    private bool _isEnabled = false;


    private CoreIndicatorParameterBase? _parameterObject;
    public CoreIndicatorParameterBase? ParameterObject
    {
        get => _parameterObject;
        set
        {
            if (_parameterObject != value)
            {
                if (_parameterObject != null)
                {
                    _parameterObject.PropertyChanged -= OnParameterChanged;
                }
                _parameterObject = value;
                if (_parameterObject != null)
                {
                    _parameterObject.PropertyChanged += OnParameterChanged;
                }
                OnPropertyChanged(nameof(ParameterObject));
                UpdateDisplayName();
            }
        }
    }

    private void OnParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Propagate change
        OnPropertyChanged(nameof(ParameterObject));
        UpdateDisplayName();
        MathematicalVersion++;
    }
    
    [ObservableProperty]
    private IndicatorColor _color = new(255, 255, 0, 0); // Red
    [ObservableProperty]
    private double _thickness = 1.5;
    [ObservableProperty]
    private CoreLineStyle _style = CoreLineStyle.Solid;
    
    [ObservableProperty]
    private int _offset = 0;
    partial void OnOffsetChanged(int value) => MathematicalVersion++;

    [ObservableProperty]
    private bool _useUpDownColors = false;
    [ObservableProperty]
    private IndicatorColor _upColor = new(255, 0, 255, 0); // Lime
    [ObservableProperty]
    private IndicatorColor _downColor = new(255, 255, 0, 0); // Red
    [ObservableProperty]
    private bool _isOverlay = true;

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// Tracking version for mathematical parameters (Period, Offset, etc.).
    /// Incremented whenever a change occurs that requires a full re-calculation.
    /// Visual-only changes (Color, Thickness) do NOT increment this version.
    /// </summary>
    [ObservableProperty]
    private long _mathematicalVersion = 0;

    public string DisplayName { get; set; } = string.Empty;
    public string ShortDisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// Optional fixed minimum value for the indicator's vertical scale (e.g., 0 for RSI).
    /// If null, scaling is automatic based on data.
    /// </summary>
    [ObservableProperty]
    private decimal? _minValue;

    /// <summary>
    /// Optional fixed maximum value for the indicator's vertical scale (e.g., 100 for RSI).
    /// If null, scaling is automatic based on data.
    /// </summary>
    [ObservableProperty]
    private decimal? _maxValue;

    /// <summary>
    /// Optional panel group identifier for multi-oscillator overlay.
    /// When set, indicators with the same OverlayPanelId share a single sub-window panel.
    /// When null or empty, the indicator occupies its own independent panel.
    /// </summary>
    [ObservableProperty]
    private string? _overlayPanelId;

    /// <summary>
    /// When true, renders visual markers (triangles) at golden cross and dead cross
    /// points between the indicator's line series.
    /// </summary>
    [ObservableProperty]
    private bool _showCrossMarkers = false;

    /// <summary>
    /// When true, projects the indicator's latest value onto the Y-axis
    /// as a label rendered by the AxisLabelRenderer.
    /// </summary>
    [ObservableProperty]
    private bool _showAxisLabel = false;

    /// <summary>
    /// Configuration for individual series colors.
    /// If empty, the indicator uses the single 'Color' property and defaults.
    /// </summary>
    public ObservableCollection<SeriesColorConfig> SeriesColors { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    [ObservableProperty]
    private string? _errorMessage;

    public void UpdateDisplayName()
    {
        string typeName = TypeEnum.HasValue ? TypeEnum.Value.ToString() : "Unknown";
        // Attempt to get friendly name via description if TypeEnum has value
        if (TypeEnum.HasValue)
        {
            typeName = TypeEnum.Value.GetDescription();
        }

        DisplayName = typeName;

        // --- Update ShortDisplayName (Strict Version) ---
        string shortName = TypeEnum.HasValue ? TypeEnum.Value.ToString() : "Unknown";
        
        if (ParameterObject != null)
        {
            // ParameterObject.GetDisplayName(shortName) returns "SMA (20)"
            // Strict Version: Remove the space to get "SMA(20)"
            ShortDisplayName = ParameterObject.GetDisplayName(shortName).Replace(" (", "(");
        }
        else
        {
            ShortDisplayName = shortName;
        }
        
        OnPropertyChanged(nameof(ShortDisplayName));
    }

    public CoreIndicatorSettings Clone()
    {
        var clone = (CoreIndicatorSettings)MemberwiseClone();
        clone.Id = Guid.NewGuid().ToString(); // Prevent duplicate ID crashes
        if (ParameterObject != null)
        {
            clone.ParameterObject = ParameterObject.Clone();
        }
        
        // OverlayPanelId is a string (immutable), so MemberwiseClone already copies it correctly.

        clone.SeriesColors = new ObservableCollection<SeriesColorConfig>();
        foreach(var sc in SeriesColors)
        {
            clone.SeriesColors.Add(sc.Duplicate());
        }

        return clone;
    }

    /// <summary>
    /// Creates a deep-copy of the settings but PRESERVES the same Id and MathematicalVersion.
    /// Used for internal state caching/comparison where identity must be maintained.
    /// </summary>
    public CoreIndicatorSettings Snapshot()
    {
        var snapshot = (CoreIndicatorSettings)MemberwiseClone();
        if (ParameterObject != null)
        {
            snapshot.ParameterObject = ParameterObject.Clone();
        }

        snapshot.SeriesColors = new ObservableCollection<SeriesColorConfig>();
        foreach (var sc in SeriesColors)
        {
            snapshot.SeriesColors.Add(sc.Duplicate());
        }

        return snapshot;
    }
}
