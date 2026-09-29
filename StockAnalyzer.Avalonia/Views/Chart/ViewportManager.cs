using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Manages the viewport state of the chart.
/// Responsible for Pan, Zoom, and maintaining the visible range in logical units.
/// Supporting both Time-based (ticks) and Index-based (indices) coordinate systems.
/// </summary>
public partial class ViewportManager : ObservableObject
{
    private double _visibleStartUnit; // Primary state: Start of viewport in logical units
    private double _visibleEndUnit;   // Primary state: End of viewport in logical units
    private double _pixelsPerUnit;    // Primary state: Horizontal scale
    private Rect _screenBounds;
    
    // Limits in logical units
    private double _dataStartUnit;
    private double _dataEndUnit;     // Last known data point
    private double _futureEndUnit;   // Data end + future offset
    private double _minVisibleDurationUnits = 10000; // Default minimum zoom (e.g., 1ms or 0.1 index)
    private double _maxVisibleDurationUnits = double.MaxValue;
    private bool _isRightPinned;
    private bool _isResetting; // WebAI Fix: Prevents intermediate updates during family transitions

    public event EventHandler? ViewportChanged;

    /// <summary>
    /// Current visible start unit.
    /// </summary>
    /// <summary>
    /// Current visible start unit.
    /// </summary>
    public double VisibleStartUnit
    {
        get => _visibleStartUnit;
        private set => SetProperty(ref _visibleStartUnit, value);
    }

    /// <summary>
    /// Current visible end unit.
    /// </summary>
    public double VisibleEndUnit
    {
        get => _visibleEndUnit;
        set => SetProperty(ref _visibleEndUnit, value);
    }

    /// <summary>
    /// Current horizontal scale (Pixels per Unit).
    /// </summary>
    public double PixelsPerUnit
    {
        get => _pixelsPerUnit;
        set
        {
            if (SetProperty(ref _pixelsPerUnit, value))
            {
                UpdateVisibleRange();
            }
        }
    }

    #region DateTime Compatibility Helpers

    /// <summary>
    /// Current visible start time (Ticks to DateTime).
    /// </summary>
    public DateTime VisibleStartTime
    {
        get => new DateTime((long)Math.Clamp(VisibleStartUnit, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks));
        private set => VisibleStartUnit = value.Ticks;
    }

    /// <summary>
    /// Current visible end time (Ticks to DateTime).
    /// </summary>
    public DateTime VisibleEndTime
    {
        get => new DateTime((long)Math.Clamp(VisibleEndUnit, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks));
        private set => VisibleEndUnit = value.Ticks;
    }

    /// <summary>
    /// Current horizontal scale in ticks (for legacy compatibility).
    /// </summary>
    public double PixelsPerTick
    {
        get => PixelsPerUnit;
        private set => PixelsPerUnit = value;
    }

    #endregion

    /// <summary>
    /// If true, the viewport remains pinned to the data end (right edge).
    /// Panning and Zooming will only affect the left edge and horizontal scale.
    /// </summary>
    public bool IsRightPinned
    {
        get => _isRightPinned;
        set
        {
            if (SetProperty(ref _isRightPinned, value))
            {
                UpdateVisibleRange();
            }
        }
    }

    /// <summary>
    /// The screen bounds of the chart area.
    /// </summary>
    public Rect ScreenBounds
    {
        get => _screenBounds;
        set
        {
            if (_screenBounds != value)
            {
                _screenBounds = value;
                
                // WebAI Fix: Only perform auto-panning if NOT in the middle of a reset/transition.
                // Otherwise, ScreenBounds assignment might use a stale PixelsPerUnit to shift the view.
                if (!_isResetting)
                {
                    if (_pixelsPerUnit > 0 && _pixelsPerUnit != 0.0001)
                    {
                        double newVisibleUnits = _screenBounds.Width / _pixelsPerUnit;
                        _visibleStartUnit = _visibleEndUnit - newVisibleUnits;
                    }
                    else if (_pixelsPerUnit == 0.0001 && _screenBounds.Width > 0)
                    {
                        double currentUnits = _visibleEndUnit - _visibleStartUnit;
                        if (currentUnits > 0)
                        {
                            _pixelsPerUnit = _screenBounds.Width / currentUnits;
                            OnPropertyChanged(nameof(PixelsPerUnit));
                        }
                    }
                }
                
                OnPropertyChanged(nameof(ScreenBounds));
                UpdateVisibleRange();
            }
        }
    }

    public ViewportManager()
    {
        // Default initialization (Time-based default)
        _visibleStartUnit = DateTime.Now.AddMonths(-1).Ticks;
        _visibleEndUnit = DateTime.Now.Ticks;
        _pixelsPerUnit = 0.0001; 
    }

    /// <summary>
    /// Initialize with data bounds (Time-based).
    /// </summary>
    public void SetDataBounds(DateTime first, DateTime last)
    {
        _dataStartUnit = first.Ticks;
        _dataEndUnit = last.Ticks;
        if (_futureEndUnit < _dataEndUnit) _futureEndUnit = _dataEndUnit;
        
        UpdateMaxVisibleDuration();

        // WebAI Fix: Skip default initialization during a family transition (ResetScale sets _isResetting).
        // Without this guard, switching to Comparison chart overwrites the viewport with a 30-day window
        // before ForceVisibleRange in the adaptation block can set the correct full range.
        // This matches the existing guard in the Index-based SetDataBounds overload.
        if (_pixelsPerUnit == 0.0001 && !_isResetting) 
        {
             var initialViewDurationUnits = TimeSpan.FromDays(30).Ticks;
             VisibleStartUnit = _dataEndUnit - initialViewDurationUnits;
             VisibleEndUnit = _dataEndUnit;
             UpdateScaleFromDuration(_screenBounds.Width, initialViewDurationUnits);
        }
    }

    /// <summary>
    /// Resets the internal scale (PixelsPerUnit) to the default value.
    /// This also clears the visible unit bounds to prevent coordinate contamination
    /// (e.g. Ticks leaking into Index mode) before the next data-driven range is set.
    /// </summary>
    public void ResetScale()
    {
        _isResetting = true; // WebAI Fix: Start critical section
        _pixelsPerUnit = 0.0001;
        _visibleStartUnit = 0;
        _visibleEndUnit = 1;
        
        OnPropertyChanged(nameof(PixelsPerUnit));
        OnPropertyChanged(nameof(VisibleStartUnit));
        OnPropertyChanged(nameof(VisibleEndUnit));
    }

    /// <summary>
    /// Initialize with data bounds (Index-based).
    /// </summary>
    public void SetDataBounds(double firstIndex, double lastIndex)
    {
        _dataStartUnit = firstIndex;
        _dataEndUnit = lastIndex;
        if (_futureEndUnit < _dataEndUnit) _futureEndUnit = _dataEndUnit;
        
        UpdateMaxVisibleDuration();

        // Default view for index: show last 100 items if not set
        // WebAI Fix: Skip this default initialization during a family transition (ResetScale → ForceVisibleRange).
        // Otherwise this overwrites the viewport to show only 100 items before ForceVisibleRange can set the full range.
        if (_pixelsPerUnit == 0.0001 && !_isResetting)
        {
            double initialViewDurationUnits = 100;
            VisibleStartUnit = _dataEndUnit - initialViewDurationUnits;
            VisibleEndUnit = _dataEndUnit;
            UpdateScaleFromDuration(_screenBounds.Width, initialViewDurationUnits);
        }
    }

    /// <summary>
    /// Resets the view to a specific duration, panning to the latest data.
    /// </summary>
    public void ResetView(TimeSpan visibleDuration) => ResetView(visibleDuration.Ticks);

    /// <summary>
    /// Resets the view to a specific duration in units, panning to the latest data.
    /// </summary>
    public void ResetView(double visibleDurationUnits)
    {
        if (_screenBounds.Width <= 0 || visibleDurationUnits <= 0) return;
        
        if (visibleDurationUnits > _maxVisibleDurationUnits) visibleDurationUnits = _maxVisibleDurationUnits;
        if (visibleDurationUnits < _minVisibleDurationUnits) visibleDurationUnits = _minVisibleDurationUnits;
        
        UpdateScaleFromDuration(_screenBounds.Width, visibleDurationUnits);
        PanToLatestInternal();
        
        _isResetting = false; // WebAI Fix: End critical section
        UpdateVisibleRange(); // Final sync
    }

    /// <summary>
    /// Sets the maximum allowable future offset from the end of data.
    /// </summary>
    public void SetFutureOffset(TimeSpan futureOffset)
    {
        _futureEndUnit = _dataEndUnit + futureOffset.Ticks;
        UpdateMaxVisibleDuration();
    }

    public void SetFutureOffsetUnits(double futureOffsetUnits)
    {
        _futureEndUnit = _dataEndUnit + futureOffsetUnits;
        UpdateMaxVisibleDuration();
    }

    /// <summary>
    /// Sets the data interval (Time-based).
    /// </summary>
    public void SetDataInterval(TimeSpan interval) => SetDataIntervalUnits(interval.Ticks);

    /// <summary>
    /// Sets the data interval to dynamically adjust minimum visible duration.
    /// </summary>
    public void SetDataIntervalUnits(double intervalUnits)
    {
        if (intervalUnits > 0)
        {
            _minVisibleDurationUnits = intervalUnits * 2; // Allow zooming in to 2 units
        }
    }

    private void UpdateMaxVisibleDuration()
    {
        double total = _futureEndUnit - _dataStartUnit;
        if (total < 1) total = 1;

        // Snaps exactly to the bounds (0 to total + offset) without excessive 50% blank padding
        _maxVisibleDurationUnits = total; 
    }

    public void Pan(double pixelDeltaX)
    {
        if (_pixelsPerUnit <= 0 || _screenBounds.Width <= 0) return;

        if (IsRightPinned)
        {
            // Scrolling/Panning when pinned to the right becomes a "Scale Change" anchored at the right.
            // Move the left edge, and recalculate pixelsPerUnit to keep the right edge fixed.
            double unitDelta = pixelDeltaX / _pixelsPerUnit;
            double newStart = _visibleStartUnit - unitDelta;
            
            // Limit start
            if (newStart < _dataStartUnit - 1) newStart = _dataStartUnit - 1;
            
            double newDuration = _futureEndUnit - newStart;
            if (newDuration < _minVisibleDurationUnits) newDuration = _minVisibleDurationUnits;
            if (newDuration > _maxVisibleDurationUnits) newDuration = _maxVisibleDurationUnits;
            
            _pixelsPerUnit = _screenBounds.Width / newDuration;
            _visibleStartUnit = _futureEndUnit - newDuration;
            UpdateVisibleRange();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        double unitDeltaStandard = pixelDeltaX / _pixelsPerUnit;
        double newStartStandard = _visibleStartUnit - unitDeltaStandard;
        double visibleUnits = _screenBounds.Width / _pixelsPerUnit;
        
        double newEnd = newStartStandard + visibleUnits;
        
        // Soft Clamp Right: Don't push further out of bounds if already out, limit to bounds if moving out
        if (newEnd > _futureEndUnit)
        {
            if (_visibleEndUnit > _futureEndUnit && newEnd > _visibleEndUnit) 
            {
                newStartStandard = _visibleEndUnit - visibleUnits;
            }
            else
            {
                newStartStandard = _futureEndUnit - visibleUnits;
            }
        }
        
        // Soft Clamp Left
        if (newStartStandard < _dataStartUnit - 1)
        {
            if (_visibleStartUnit < _dataStartUnit - 1 && newStartStandard < _visibleStartUnit)
            {
                newStartStandard = _visibleStartUnit;
            }
            else
            {
                newStartStandard = _dataStartUnit - 1;
            }
        }
        
        VisibleStartUnit = newStartStandard;
        UpdateVisibleRange();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }
    
    public void Zoom(double zoomFactor, double pivotScreenX)
    {
        if (_screenBounds.Width <= 0 || _pixelsPerUnit <= 0) return;

        double effectivePivotX = IsRightPinned ? _screenBounds.Right : pivotScreenX;
        
        double pivotOffsetPixels = effectivePivotX - _screenBounds.X;
        double pivotUnitFromStart = pivotOffsetPixels / _pixelsPerUnit;
        double pivotUnit = _visibleStartUnit + pivotUnitFromStart;

        double newPixelsPerUnit = _pixelsPerUnit * zoomFactor;

        double maxScale = _screenBounds.Width / _minVisibleDurationUnits;
        double minScale = _screenBounds.Width / _maxVisibleDurationUnits;
        
        if (newPixelsPerUnit > maxScale) newPixelsPerUnit = maxScale;
        if (newPixelsPerUnit < minScale) newPixelsPerUnit = minScale;

        // Apply calculated scale to global state
        _pixelsPerUnit = newPixelsPerUnit;

        double newPivotUnitFromStart = pivotOffsetPixels / newPixelsPerUnit;
        double newStart = pivotUnit - newPivotUnitFromStart;
        double visibleUnits = _screenBounds.Width / newPixelsPerUnit;
        
        if (IsRightPinned)
        {
            // Lock right edge exactly to boundary
            newStart = _futureEndUnit - visibleUnits;
        }
        else
        {
            // 1. Clamp right out-of-bounds (Smoothly push the chart left without snapping to 0-margin mid-zoom)
            double newEnd = newStart + visibleUnits;
            if (newEnd > _futureEndUnit)
            {
                newStart = _futureEndUnit - visibleUnits;
            }

            // 2. Clamp left only if it won't break the right boundary
            if (newStart < _dataStartUnit - 1)
            {
                double potentialEnd = (_dataStartUnit - 1) + visibleUnits;
                if (potentialEnd <= _futureEndUnit)
                {
                    newStart = _dataStartUnit - 1;
                }
            }
        }
        
        VisibleStartUnit = newStart;
        UpdateVisibleRange();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forcefully sets the visible range in logical units.
    /// Used when the underlying data is re-converted (e.g., Kagi, Renko).
    /// </summary>
    public void ForceVisibleRange(double startUnit, double endUnit)
    {
        VisibleStartUnit = startUnit;
        VisibleEndUnit = endUnit;
        if (_screenBounds.Width > 0)
        {
            double duration = endUnit - startUnit;
            if (duration > 0)
            {
                _pixelsPerUnit = _screenBounds.Width / duration;
                OnPropertyChanged(nameof(PixelsPerUnit));
            }
        }
        
        _isResetting = false; // WebAI Fix: End critical section
        UpdateVisibleRange(); // Final sync
    }

    private void UpdateVisibleRange()
    {
        if (_isResetting) return; // WebAI Fix: Skip processing during atomic reset/transition
        if (_pixelsPerUnit <= 0 || _screenBounds.Width <= 0) return;
        
        double visibleUnits = _screenBounds.Width / _pixelsPerUnit;
        
        if (IsRightPinned)
        {
            VisibleEndUnit = _futureEndUnit;
            VisibleStartUnit = _futureEndUnit - visibleUnits;
        }
        else
        {
            VisibleEndUnit = _visibleStartUnit + visibleUnits;
        }
        
        OnPropertyChanged(nameof(VisibleStartUnit));
        OnPropertyChanged(nameof(VisibleEndUnit));
        OnPropertyChanged(nameof(VisibleStartTime));
        OnPropertyChanged(nameof(VisibleEndTime));
    }
    
    private void UpdateScaleFromDuration(double width, double durationUnits)
    {
        if (width <= 0 || durationUnits <= 0) return;
        PixelsPerUnit = width / durationUnits;
    }

    /// <summary>
    /// Convert Unit to Screen X coordinate.
    /// </summary>
    public double UnitToScreenX(double unit)
    {
        if (_pixelsPerUnit <= 0) return 0;
        return (unit - _visibleStartUnit) * _pixelsPerUnit + _screenBounds.X;
    }

    /// <summary>
    /// Convert Screen X coordinate to Unit.
    /// </summary>
    public double ScreenXToUnit(double screenX)
    {
        if (_pixelsPerUnit <= 0) return _visibleStartUnit;
        return (screenX - _screenBounds.X) / _pixelsPerUnit + _visibleStartUnit;
    }
    
    /// <summary>
    /// Pans the view to show the latest data.
    /// </summary>
    public void PanToLatest()
    {
        PanToLatestInternal();
        _isResetting = false; // WebAI Fix: End critical section
        UpdateVisibleRange();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PanToLatestInternal()
    {
        VisibleEndUnit = _futureEndUnit;
        if (_pixelsPerUnit > 0 && _screenBounds.Width > 0)
        {
            double visibleUnits = _screenBounds.Width / _pixelsPerUnit;
            _visibleStartUnit = _visibleEndUnit - visibleUnits;
            OnPropertyChanged(nameof(VisibleStartUnit));
        }
    }
}
