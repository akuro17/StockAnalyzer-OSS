using System;
using System.Collections.Generic;
using Avalonia;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Defines the mode of the X-axis for coordinate transformation.
/// </summary>
public enum ChartAxisMode
{
    /// <summary>
    /// Standard time-based X-axis (DateTime).
    /// </summary>
    Time,

    /// <summary>
    /// Index-based X-axis (Integer Index).
    /// Used for Renko, Kagi, P&F where time is not linear.
    /// ChartPoint.Time.Ticks stores the Index.
    /// </summary>
    Index,

    /// <summary>
    /// Gapless Time-based X-axis (DateTime to fractional Index bridge).
    /// Used for skipping weekends/holidays while preserving absolute DateTime scale.
    /// </summary>
    GaplessTime,

    /// <summary>
    /// Volume-based X-axis (Decimal Volume).
    /// Used for Reverse Watch Curve.
    /// ChartPoint.Time.Ticks stores the Volume (as long).
    /// </summary>
    Volume
}

/// <summary>
/// A generic coordinate transform that supports Time, Index, and Volume based X-axes.
/// Replaces LinearCoordinateTransform to support non-time series charts.
/// </summary>
public class GenericCoordinateTransform : ICoordinateTransform
{
    private ChartAxisMode _mode;
    
    // Domain Bounds (Logical)
    private DateTime _minTime;
    private DateTime _maxTime;
    private double _minIndex; // double to support fractional scrolling/zooming if needed
    private double _maxIndex;
    private decimal _minVolume;
    private decimal _maxVolume;
    private IReadOnlyList<DateTime>? _timeMap;

    // Y-Axis (Always Price for now)
    private decimal _minPrice;
    private decimal _maxPrice;
    private double _logMin;
    private double _logMax;

    /// <summary>Canvas width (pixels)</summary>
    public double CanvasWidth { get; private set; }

    /// <summary>Canvas height (pixels)</summary>
    public double CanvasHeight { get; private set; }

    // Viewport bounds (Inner chart area)
    public double ViewportX { get; private set; }
    public double ViewportY { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }

    // Screen Mapping Coefficients
    private double _scaleX; 
    private double _scaleY;
    private double _offsetY;
    
    // Intrinsic Padding to prevent borderline clipping
    private double _currentPaddingX = 8.0; // Dynamic, to protect the right-half of thick candle bodies and labels
    private double _currentPaddingY = 5.0; // Dynamic, to protect vertical clipping

    public double GetMinIndex() => _minIndex;
    public double GetScaleX() => _scaleX;

    /// <summary>X-Axis Scaling factor (pixels per unit)</summary>
    public double ScaleX => _scaleX;

    /// <summary>
    /// When set, forces X-axis scaling to exactly match Y-axis scaled by this value.
    /// Used for Point & Figure to ensure 1:1 aspect ratio (1 Box Width = 1 Box Height Price).
    /// </summary>
    public decimal? FixedPricePerIndex { get; set; }

    /// <summary>Pixels per price unit (read-only, for HitTest precision)</summary>
    public double PixelPerPrice => _scaleY;

    /// <summary>Bounding rectangle of the screen area</summary>
    public global::Avalonia.Rect ScreenRect => new global::Avalonia.Rect(0, 0, CanvasWidth, CanvasHeight);

    /// <summary>
    /// Gets the current axis mode.
    /// </summary>
    public ChartAxisMode Mode => _mode;

    public PriceScaleType PriceScale { get; set; } = PriceScaleType.Linear;

    /// <summary>
    /// Metadata for layout synchronization.
    /// </summary>
    public TransformMetadata Metadata { get; set; }

    /// <summary>
    /// Sets the axis mode and recalculates coefficients.
    /// </summary>
    public void SetMode(ChartAxisMode mode)
    {
        if (_mode != mode)
        {
            _mode = mode;
            RecalculateCoefficients();
        }
    }

    /// <summary>Constructor</summary>
    public GenericCoordinateTransform(
        ChartAxisMode mode,
        double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0) canvasWidth = 1000;
        if (canvasHeight <= 0) canvasHeight = 500;
            
        _mode = mode;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        
        // Ensure Viewport variables are initialized so standalone panel configurations don't collapse.
        ViewportWidth = canvasWidth;
        ViewportHeight = canvasHeight;
        
        // Default ranges
        _minTime = DateTime.Now.AddDays(-1);
        _maxTime = DateTime.Now;
        _minIndex = 0;
        _maxIndex = 100;
        _minVolume = 0;
        _maxVolume = 1000;
        _minPrice = 0;
        _maxPrice = 100;

        RecalculateCoefficients();
    }

    /// <summary>Set Time Range (Mode = Time)</summary>
    public void SetTimeRange(DateTime min, DateTime max)
    {
        _minTime = min;
        _maxTime = max;
        // Validation: ensure at least 1 minute interval for single-candle datasets or degenerate ranges
        if (_minTime >= _maxTime) _maxTime = _minTime.AddMinutes(1);
        RecalculateCoefficients();
    }

    /// <summary>Set Index Range (Mode = Index)</summary>
    public void SetIndexRange(double min, double max)
    {
        _minIndex = min;
        _maxIndex = max;
        if (_minIndex >= _maxIndex) _maxIndex = _minIndex + 1;
        RecalculateCoefficients();
    }

    /// <summary>Set Volume Range (Mode = Volume)</summary>
    public void SetVolumeRange(decimal min, decimal max)
    {
        _minVolume = min;
        _maxVolume = max;
        if (_minVolume >= _maxVolume) _maxVolume = _minVolume + 1m;
        RecalculateCoefficients();
    }

    /// <summary>Set Price Range (Y-Axis)</summary>
    public void SetPriceRange(decimal min, decimal max)
    {
        _minPrice = min;
        _maxPrice = max;
        if (_minPrice >= _maxPrice) _maxPrice = _minPrice + 1m;
        RecalculateCoefficients();
    }

    /// <summary>Sets the time map for GaplessTime axis mapping</summary>
    public void SetTimeMap(IReadOnlyList<DateTime> timeMap)
    {
        _timeMap = timeMap;
    }

    /// <summary>Retrieves the active timestamp array if configured</summary>
    public IReadOnlyList<DateTime>? TimeMap => _timeMap;

    public double GetFractionalIndex(DateTime time)
    {
        if (_timeMap == null || _timeMap.Count == 0) return 0;
        
        double avgMs = (_timeMap[^1] - _timeMap[0]).TotalMilliseconds / _timeMap.Count;
        if (avgMs <= 0) avgMs = TimeSpan.FromDays(1).TotalMilliseconds;

        if (time <= _timeMap[0])
        {
            return 0 - (time - _timeMap[0]).TotalMilliseconds / avgMs;
        }
        if (time >= _timeMap[^1])
        {
            return (_timeMap.Count - 1) + (time - _timeMap[^1]).TotalMilliseconds / avgMs;
        }

        int low = 0;
        int high = _timeMap.Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int comparison = _timeMap[mid].CompareTo(time);
            if (comparison == 0) return mid;
            if (comparison < 0) low = mid + 1;
            else high = mid - 1;
        }

        DateTime t1 = _timeMap[high];
        DateTime t2 = _timeMap[low];
        
        double ratio = (time - t1).TotalMilliseconds / (t2 - t1).TotalMilliseconds;
        return high + ratio;
    }

    public DateTime GetTimeFromFractionalIndex(double idx)
    {
        if (_timeMap == null || _timeMap.Count == 0) return DateTime.MinValue;

        double avgMs = (_timeMap[^1] - _timeMap[0]).TotalMilliseconds / _timeMap.Count;
        if (avgMs <= 0) avgMs = TimeSpan.FromDays(1).TotalMilliseconds;

        if (idx <= 0)
        {
            return _timeMap[0].AddMilliseconds(idx * avgMs);
        }
        if (idx >= _timeMap.Count - 1)
        {
            return _timeMap[^1].AddMilliseconds((idx - (_timeMap.Count - 1)) * avgMs);
        }

        int lowBound = (int)Math.Floor(idx);
        int highBound = (int)Math.Ceiling(idx);

        if (lowBound == highBound) return _timeMap[lowBound];

        DateTime t1 = _timeMap[lowBound];
        DateTime t2 = _timeMap[highBound];
        double ratio = idx - lowBound;

        return t1.AddMilliseconds((t2 - t1).TotalMilliseconds * ratio);
    }

    public void UpdateCanvasSize(double width, double height, double vX = 0, double vY = 0, double vWidth = 0, double vHeight = 0)
    {
        if (width > 0) CanvasWidth = width;
        if (height > 0) CanvasHeight = height;
        
        ViewportX = vX;
        ViewportY = vY;
        
        // Instead of falling back to CanvasWidth/Height (which causes massive visual protrusion if the 
        // layout provider temporally returns a completely squashed/negative ChartArea), force clamping 
        // to exactly 1.0 so that invalid layout frames just render safely out of sight (above the clip rect).
        ViewportWidth = vWidth > 0 ? vWidth : 1.0;
        ViewportHeight = vHeight > 0 ? vHeight : 1.0;

        RecalculateCoefficients();
    }

    public void UpdateRange(
        DateTime minTime, DateTime maxTime,
        decimal minPrice, decimal maxPrice,
        double? newCanvasWidth = null,
        double? newCanvasHeight = null)
    {
        SetTimeRange(minTime, maxTime);
        SetPriceRange(minPrice, maxPrice);
        
        if (newCanvasWidth.HasValue && newCanvasHeight.HasValue)
        {
            UpdateCanvasSize(newCanvasWidth.Value, newCanvasHeight.Value);
        }
    }

    private void RecalculateCoefficients()
    {
        // Set dynamic padding based on Mode
        _currentPaddingX = (_mode == ChartAxisMode.Volume) ? 0.0 : 8.0;
        _currentPaddingY = (_mode == ChartAxisMode.Volume) ? 0.0 : 5.0;

        double availableHeight = ViewportHeight - (_currentPaddingY * 2);
        if (availableHeight <= 0) availableHeight = 1;
        
        if (PriceScale == PriceScaleType.Log)
        {
            double safeMin = _minPrice > 0m ? (double)_minPrice : Math.Max(0.0001, (double)_maxPrice * 0.01);
            double safeMax = Math.Max(safeMin + 0.0001, (double)_maxPrice);
            _logMin = Math.Log(safeMin);
            _logMax = Math.Log(safeMax);
            double logRange = _logMax - _logMin;
            if (logRange == 0) logRange = 1.0;
            _scaleY = availableHeight / logRange;
        }
        else
        {
            decimal priceRange = _maxPrice - _minPrice;
            if (priceRange == 0) priceRange = 1m;
            _scaleY = availableHeight / (double)priceRange;
        }
        _offsetY = ViewportHeight - _currentPaddingY; // Screen Y at MinPrice is ViewportHeight - _currentPaddingY

        // X-Axis based on Mode
        double availableWidth = ViewportWidth - (_currentPaddingX * 2);
        if (availableWidth <= 0) availableWidth = 1;

        switch (_mode)
        {
            case ChartAxisMode.Time:
                double timeRangeMs = (_maxTime - _minTime).TotalMilliseconds;
                if (timeRangeMs == 0) timeRangeMs = 1;
                _scaleX = availableWidth / timeRangeMs;
                break;

            case ChartAxisMode.GaplessTime:
                _minIndex = GetFractionalIndex(_minTime);
                _maxIndex = GetFractionalIndex(_maxTime);
                double gaplessRange = _maxIndex - _minIndex;
                if (gaplessRange <= 0) gaplessRange = 1;
                
                _scaleX = availableWidth / gaplessRange;

                // Dynamic padding: If scaled unit is wider than twice the padding,
                // candle bodies will clip. Recalculate scale and padding.
                if (_scaleX / 2 > _currentPaddingX)
                {
                    _scaleX = ViewportWidth / (gaplessRange + 1);
                    _currentPaddingX = _scaleX / 2;
                }
                break;

            case ChartAxisMode.Index:
                double indexRange = _maxIndex - _minIndex;
                if (indexRange == 0) indexRange = 1;
                _scaleX = availableWidth / indexRange;

                if (_scaleX / 2 > _currentPaddingX)
                {
                    _scaleX = ViewportWidth / (indexRange + 1);
                    _currentPaddingX = _scaleX / 2;
                }

                if (FixedPricePerIndex.HasValue && FixedPricePerIndex.Value > 0)
                {
                    _scaleY = _scaleX / (double)FixedPricePerIndex.Value;
                }
                break;

            case ChartAxisMode.Volume:
                decimal volRange = _maxVolume - _minVolume;
                if (volRange == 0) volRange = 1m;
                _scaleX = availableWidth / (double)volRange;
                _minIndex = (double)_minVolume;
                break;
        }

        // Zero-scale division protection
        if (double.IsNaN(_scaleX) || double.IsInfinity(_scaleX) || _scaleX <= 0.0)
        {
            _scaleX = 1.0;
        }
        if (double.IsNaN(_scaleY) || double.IsInfinity(_scaleY) || _scaleY <= 0.0)
        {
            _scaleY = 1.0;
        }
    }

    /// <summary>
    /// FR-PNF-03: Index モード（Renko/P&amp;F/TLB）における論理インデックスから
    /// 安全に <see cref="ChartPoint"/> を生成するファクトリメソッド。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Index モードでは、論理インデックスを <c>ChartPoint.Time.Ticks</c> (long, &gt;= 0) に
    /// 格納して X 座標へマッピングしている。
    /// しかし水平線・トレンドライン・パターンマーカー等の描画オブジェクトは、
    /// アンカーから前後に数カラム分オフセットした絶対インデックス（例: <c>absoluteIndex - colsBack</c>
    /// や三角形パターンの <c>absoluteTriggerIndex - 4</c> 等）を直接 <c>new DateTime(index)</c> として
    /// 構築するため、データ先頭付近にスクロールした際に index が負値となり
    /// <see cref="ArgumentOutOfRangeException"/> が発生し、描画オブジェクトがグリッドと
    /// 同期されなくなる（ズレる／例外で描画が中断される）バグの原因となっていた。
    /// </para>
    /// <para>
    /// 本メソッドは index を [0, DateTime.MaxValue.Ticks] にクランプし、かつ NaN/Infinity を
    /// 安全に 0 へフォールバックすることで、常に有効な ChartPoint を返す。
    /// Index モードのレンダラーは、生の <c>new DateTime((long)index)</c> の代わりに
    /// この静的メソッドを使用すること。
    /// </para>
    /// </remarks>
    /// <param name="index">論理カラムインデックス（絶対値、負値・小数を許容）。</param>
    /// <param name="price">Y 軸用の価格値（必要なければ 0）。</param>
    /// <returns>X 座標マッピングに使用可能な、安全にクランプされた ChartPoint。</returns>
    public static ChartPoint SafeIndexPoint(double index, decimal price = 0m)
    {
        if (double.IsNaN(index) || double.IsInfinity(index))
        {
            index = 0;
        }

        long ticks = (long)Math.Round(index);
        if (ticks < 0) ticks = 0;
        if (ticks > DateTime.MaxValue.Ticks) ticks = DateTime.MaxValue.Ticks;

        return new ChartPoint(new DateTime(ticks), price);
    }

    /// <summary>Directly converts Index to screen X coordinate (Mathematical Formula)</summary>
    public double GetXFromIndex(double index)
    {
        return _currentPaddingX + (index - _minIndex) * _scaleX;
    }

    /// <summary>Directly converts Price to screen Y coordinate (Mathematical Formula)</summary>
    public double GetYFromPrice(decimal price)
    {
        if (PriceScale == PriceScaleType.Inverted)
        {
            return _currentPaddingY + (double)(price - _minPrice) * _scaleY;
        }
        else if (PriceScale == PriceScaleType.Log)
        {
            double logVal = Math.Log((double)Math.Max(0.0001m, price));
            return _offsetY - (logVal - _logMin) * _scaleY;
        }
        else // Linear & Percent
        {
            return _offsetY - (double)(price - _minPrice) * _scaleY;
        }
    }

    /// <summary>Convert chart coordinate to screen coordinate</summary>
    public global::Avalonia.Point ChartToScreen(ChartPoint chartPoint)
    {
        double x = _currentPaddingX;
        switch (_mode)
        {
            case ChartAxisMode.Time:
                x += (chartPoint.Time - _minTime).TotalMilliseconds * _scaleX;
                break;
            case ChartAxisMode.GaplessTime:
                double fracIdx = GetFractionalIndex(chartPoint.Time);
                x += (fracIdx - _minIndex) * _scaleX;
                break;
            case ChartAxisMode.Index:
                double idx;
                // WebAI: Ticksが極端に大きい場合（リアルの日時のタイムスタンプが入っている場合）、TimeMapからfractional indexを取得するガード
                if (chartPoint.Time.Ticks > 31536000000000000L) // 1000年後相当以上のTick
                {
                    idx = GetFractionalIndex(chartPoint.Time);
                }
                else
                {
                    idx = (double)chartPoint.Time.Ticks;
                }
                x += (idx - _minIndex) * _scaleX;
                break;
            case ChartAxisMode.Volume:
                decimal vol = (decimal)chartPoint.Time.Ticks;
                x += (double)(vol - _minVolume) * _scaleX;
                break;
        }

        double y = GetYFromPrice(chartPoint.Price);
        
        // Return local screen position (Renderers translate canvas to chart area offset)
        return new global::Avalonia.Point(x, y);
    }

    /// <summary>Convert screen coordinate to chart coordinate</summary>
    public ChartPoint ScreenToChart(global::Avalonia.Point screenPoint)
    {
        // Subtract intrinsic padding to get point relative to logical origin (Viewport offset already stripped)
        double relX = screenPoint.X - _currentPaddingX;
        double relY = screenPoint.Y;

        // Y-Axis
        decimal price = 0m;
        if (PriceScale == PriceScaleType.Inverted)
        {
            double relPrice = (relY - _currentPaddingY) / _scaleY;
            price = _minPrice + (decimal)relPrice;
        }
        else if (PriceScale == PriceScaleType.Log)
        {
            double logVal = _logMin + (_offsetY - relY) / _scaleY;
            price = (decimal)Math.Exp(logVal);
        }
        else // Linear & Percent
        {
            decimal priceOffset = (decimal)(_offsetY - relY) / (decimal)_scaleY;
            price = _minPrice + priceOffset;
        }

        // X-Axis
        DateTime timeOrValue = DateTime.MinValue;

        switch (_mode)
        {
            case ChartAxisMode.Time:
                double msFromStart = relX / _scaleX;
                if (double.IsNaN(msFromStart) || double.IsInfinity(msFromStart))
                {
                    timeOrValue = _minTime;
                }
                else
                {
                    try {
                        timeOrValue = _minTime.AddMilliseconds(msFromStart);
                    } catch {
                        timeOrValue = _minTime;
                    }
                }
                break;

            case ChartAxisMode.GaplessTime:
                double fracIdxFromStart = relX / _scaleX;
                double globalFracIdx = _minIndex + fracIdxFromStart;
                try {
                    timeOrValue = GetTimeFromFractionalIndex(globalFracIdx);
                } catch {
                    timeOrValue = _minTime; // Safety fallback
                }
                break;

            case ChartAxisMode.Index:
                double idxFromStart = relX / _scaleX;
                double targetIdx = _minIndex + idxFromStart;
                if (targetIdx < 0) targetIdx = 0;
                
                // WebAI: TimeMapが存在する場合、インデックスからリアルタイムを逆算して描画オブジェクトの時間トラッキングを可能にする
                if (_timeMap != null && _timeMap.Count > 0)
                {
                    try {
                        timeOrValue = GetTimeFromFractionalIndex(targetIdx);
                    } catch {
                        if (targetIdx > DateTime.MaxValue.Ticks) targetIdx = DateTime.MaxValue.Ticks;
                        timeOrValue = new DateTime((long)targetIdx);
                    }
                }
                else
                {
                    if (targetIdx > DateTime.MaxValue.Ticks) targetIdx = DateTime.MaxValue.Ticks;
                    timeOrValue = new DateTime((long)targetIdx);
                }
                break;

            case ChartAxisMode.Volume:
                double volFromStart = relX / _scaleX;
                decimal targetVol = _minVolume + (decimal)volFromStart;
                // Store as Ticks (ensure within long range, usually volume fits)
                try {
                    timeOrValue = new DateTime((long)targetVol);
                } catch {
                    timeOrValue = new DateTime(0);
                }
                break;
        }

        return new ChartPoint(timeOrValue, price);
    }

    /// <summary>Convert raw numeric (X, Y) to screen coordinate</summary>
    public global::Avalonia.Point NumericToScreen(double x, double y)
    {
        double screenX = GetXFromIndex(x);
        double screenY = GetYFromPrice((decimal)y);
        return new global::Avalonia.Point(screenX, screenY);
    }

    /// <summary>Convert screen coordinate to raw numeric (X, Y)</summary>
    public (double x, double y) ScreenToNumeric(global::Avalonia.Point screenPoint)
    {
        double relX = screenPoint.X - _currentPaddingX;
        double relY = screenPoint.Y;

        double y = 0;
        if (PriceScale == PriceScaleType.Inverted)
        {
            y = (double)_minPrice + (relY - _currentPaddingY) / _scaleY;
        }
        else if (PriceScale == PriceScaleType.Log)
        {
            double logVal = _logMin + (_offsetY - relY) / _scaleY;
            y = Math.Exp(logVal);
        }
        else // Linear & Percent
        {
            y = (double)_minPrice + (_offsetY - relY) / _scaleY;
        }
        double x = 0;

        switch (_mode)
        {
            case ChartAxisMode.Time:
                x = relX / _scaleX; // milliseconds from start
                break;
            case ChartAxisMode.GaplessTime:
            case ChartAxisMode.Index:
                x = _minIndex + (relX / _scaleX);
                break;
            case ChartAxisMode.Volume:
                x = (double)_minVolume + (relX / _scaleX);
                break;
        }

        return (x, y);
    }
}
