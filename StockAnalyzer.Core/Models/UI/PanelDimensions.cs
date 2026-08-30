using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// A property container that manages the size (width or height), visibility,
/// and the pre-hidden size of a single docking panel.
/// Prevents invalid values (NaN, infinity, negative numbers) via defensive clamping instead of exceptions.
/// </summary>
public partial class PanelDimensions : ObservableObject
{
    private readonly double _maxClamp;
    private double _widthOrHeight;
    
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isPinned = true;

    private double _lastSize;

    /// <summary>
    /// Gets or sets the current size (width or height) of the panel.
    /// Automatically clamped within LayoutConstants boundaries depending on visibility.
    /// </summary>
    public double WidthOrHeight
    {
        get => _widthOrHeight;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Invalid double value");
            }
            double clamped = ClampWidthOrHeight(value, IsVisible);
            if (Math.Abs(_widthOrHeight - clamped) >= 0.5)
            {
                SetProperty(ref _widthOrHeight, clamped);
            }
        }
    }

    /// <summary>
    /// Gets or sets the size of the panel prior to being hidden.
    /// Automatically clamped within LayoutConstants boundaries.
    /// </summary>
    public double LastSize
    {
        get => _lastSize;
        set
        {
            double clamped = ClampLastSize(value);
            SetProperty(ref _lastSize, clamped);
        }
    }

    partial void OnIsPinnedChanged(bool oldValue, bool newValue)
    {
        // Pin state changes no longer affect layout size since auto-hide is abolished.
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelDimensions"/> class.
    /// </summary>
    /// <param name="initialSize">Initial size. Must be positive and at least the minimum dimension (50.0).</param>
    /// <param name="initialVisibility">Initial visibility state.</param>
    /// <param name="maxClamp">Maximum allowed dimension boundary clamp.</param>
    public PanelDimensions(double initialSize, bool initialVisibility, double maxClamp = LayoutConstants.MaxPanelWidthClamp)
    {
        _maxClamp = maxClamp;
        double validSize = ClampLastSize(initialSize);
        _lastSize = validSize;
        _isVisible = initialVisibility;
        _widthOrHeight = initialVisibility ? validSize : 0.0;
    }

    /// <summary>
    /// Synchronizes the dimension when visibility changes.
    /// true -> false (hidden): Backs up current size to LastSize and sets WidthOrHeight to 0.0.
    /// false -> true (visible): Restores WidthOrHeight from LastSize.
    /// </summary>
    partial void OnIsVisibleChanged(bool oldValue, bool newValue)
    {
        if (newValue)
        {
            WidthOrHeight = LastSize;
        }
        else
        {
            LastSize = WidthOrHeight > 0.0 ? WidthOrHeight : LastSize;
            WidthOrHeight = 0.0;
        }
    }

    private double ClampLastSize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < LayoutConstants.MinPanelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid panel size");
        }
        return Math.Min(value, _maxClamp);
    }

    private double ClampWidthOrHeight(double value, bool isVisible)
    {
        if (!isVisible)
        {
            if (value != 0.0)
            {
                throw new ArgumentException("Cannot set non-zero size on a hidden panel", nameof(value));
            }
            return 0.0;
        }
        if (double.IsNaN(value) || double.IsInfinity(value) || value < LayoutConstants.MinPanelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid panel size");
        }
        return Math.Min(value, _maxClamp);
    }
}
