using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using System.Globalization;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Drawing;

public class FixedRangeVolumeProfileObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FixedRangeVolumeProfile;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public Color ValueAreaColor { get; set; } = Color.Parse("#FFEB3B");
    public Color ProfileColor { get; set; } = Color.Parse("#90A4AE");
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;
    public int FillOpacity { get; set; } = 10;
    public bool LockRange { get; set; } = false;

    private int _rangeBars = 0;
    private int? _pendingRangeBars;

    public int RangeBars
    {
        get => _rangeBars;
        set
        {
            if (_rangeBars != value)
            {
                _rangeBars = value;
                if (value > 0)
                {
                    _pendingRangeBars = value;
                }
            }
        }
    }

    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public VolumeProfileRepeatMode RepeatMode { get; set; } = VolumeProfileRepeatMode.Default;
    public List<VolumeProfileSegment> Segments { get; } = new List<VolumeProfileSegment>();

    public List<VolumeBin> ProfileData { get; private set; } = new List<VolumeBin>();
    public decimal VAH { get; private set; }
    public decimal VAL { get; private set; }

    // Config
    public bool RightToLeft { get; set; } = false;
    public double Opacity { get; set; } = 0.25;

    public FixedRangeVolumeProfileObject(ChartPoint p1, ChartPoint p2)
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, (byte)(255 * Opacity));
    public SKColor SkiaProfileColor => new SKColor(ProfileColor.R, ProfileColor.G, ProfileColor.B, (byte)(255 * Opacity));
    public SKColor SkiaValueColor => new SKColor(ValueAreaColor.R, ValueAreaColor.G, ValueAreaColor.B, (byte)(255 * Opacity));
    public SKColor SkiaFillColor => new SKColor(FillColor.R, FillColor.G, FillColor.B, (byte)Math.Clamp((int)(255 * (FillOpacity / 100.0)), 0, 255));

    // Reused across Render() calls (ZeroAllocation Render Loop, SA_RENDERING_PERFORMANCE.md §1)
    // instead of a `new SKPaint` per frame. Color-dependent properties are refreshed
    // from the current property values on each use since they can change between renders.
    private readonly SKPaint _previewPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _profilePaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _valueAreaPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _backgroundPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = false };

    public void Dispose()
    {
        _previewPaint.Dispose();
        _profilePaint.Dispose();
        _valueAreaPaint.Dispose();
        _backgroundPaint.Dispose();
        _linePaint.Dispose();
        GC.SuppressFinalize(this);
    }

    public void AdjustRightPointToBars(int targetBars, IReadOnlyList<CoreCandleData> candles)
    {
        if (Points.Count < 2 || candles == null || candles.Count == 0) return;

        if (targetBars < 1) targetBars = 1;

        int leftIndex = Points[0].Time <= Points[1].Time ? 0 : 1;
        int rightIndex = 1 - leftIndex;

        int leftBarIdx = FindNearestBarIndex(Points[leftIndex].Time, candles);
        if (leftBarIdx < 0) return;

        int targetRightBarIdx = Math.Clamp(leftBarIdx + targetBars - 1, 0, candles.Count - 1);
        var targetTime = candles[targetRightBarIdx].Timestamp;

        Points[rightIndex] = new ChartPoint(targetTime, Points[rightIndex].Price);
    }

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 2 || candles == null) return;

        var candleList = candles as IReadOnlyList<CoreCandleData> ?? candles.ToList();
        if (candleList.Count == 0) return;

        if (_pendingRangeBars.HasValue && _pendingRangeBars.Value > 0)
        {
            int targetBars = _pendingRangeBars.Value;
            _pendingRangeBars = null;
            AdjustRightPointToBars(targetBars, candleList);
        }

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var start = t1 < t2 ? t1 : t2;
        var end = t1 < t2 ? t2 : t1;

        var range = candleList.Where(c => c.Timestamp >= start && c.Timestamp <= end).ToList();
        _rangeBars = range.Count;

        // Calculate Profile (50 rows) for primary anchor range
        ProfileData = VolumeAnalysis.CalculateProfile(range, 50);

        // Calculate Value Area
        var va = VolumeAnalysis.CalculateValueArea(ProfileData);
        VAH = va.VAH;
        VAL = va.VAL;

        Segments.Clear();
        int baseBars = _rangeBars > 0 ? _rangeBars : 1;
        int startBarIdx = FindNearestBarIndex(start, candleList);

        if (RepeatMode == VolumeProfileRepeatMode.RangeBar && startBarIdx >= 0 && startBarIdx < candleList.Count)
        {
            int currentIdx = startBarIdx;
            int totalCandles = candleList.Count;

            while (currentIdx < totalCandles)
            {
                int count = Math.Min(baseBars, totalCandles - currentIdx);
                if (count <= 0) break;

                var slice = new List<CoreCandleData>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(candleList[currentIdx + i]);
                }

                var profile = VolumeAnalysis.CalculateProfile(slice, 50);
                var segVa = VolumeAnalysis.CalculateValueArea(profile);
                var pocBin = profile.OrderByDescending(b => b.TotalVolume).FirstOrDefault();

                var segment = new VolumeProfileSegment
                {
                    StartIndex = currentIdx,
                    Count = count,
                    StartTime = slice[0].Timestamp,
                    EndTime = slice[^1].Timestamp,
                    IsPartial = count < baseBars,
                    Bins = profile,
                    POC = pocBin?.Price,
                    VAH = segVa.VAH,
                    VAL = segVa.VAL
                };
                Segments.Add(segment);

                if (currentIdx == startBarIdx)
                {
                    ProfileData = profile;
                    VAH = segVa.VAH;
                    VAL = segVa.VAL;
                }

                currentIdx += baseBars;
            }
        }
        else if (RepeatMode == VolumeProfileRepeatMode.Weekly && startBarIdx >= 0 && startBarIdx < candleList.Count)
        {
            int currentIdx = startBarIdx;
            int totalCandles = candleList.Count;

            while (currentIdx < totalCandles)
            {
                var firstCandle = candleList[currentIdx];
                int weekKey = ISOWeek.GetYear(firstCandle.Timestamp) * 100 + ISOWeek.GetWeekOfYear(firstCandle.Timestamp);

                int endIdx = currentIdx;
                while (endIdx < totalCandles)
                {
                    var c = candleList[endIdx];
                    int k = ISOWeek.GetYear(c.Timestamp) * 100 + ISOWeek.GetWeekOfYear(c.Timestamp);
                    if (k != weekKey) break;
                    endIdx++;
                }

                int count = endIdx - currentIdx;
                var slice = new List<CoreCandleData>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(candleList[currentIdx + i]);
                }

                var profile = VolumeAnalysis.CalculateProfile(slice, 50);
                var segVa = VolumeAnalysis.CalculateValueArea(profile);
                var pocBin = profile.OrderByDescending(b => b.TotalVolume).FirstOrDefault();

                var segment = new VolumeProfileSegment
                {
                    StartIndex = currentIdx,
                    Count = count,
                    StartTime = slice[0].Timestamp,
                    EndTime = slice[^1].Timestamp,
                    IsPartial = (currentIdx == startBarIdx && slice[0].Timestamp.DayOfWeek != DayOfWeek.Monday) || endIdx == totalCandles,
                    Bins = profile,
                    POC = pocBin?.Price,
                    VAH = segVa.VAH,
                    VAL = segVa.VAL
                };
                Segments.Add(segment);

                if (currentIdx == startBarIdx)
                {
                    ProfileData = profile;
                    VAH = segVa.VAH;
                    VAL = segVa.VAL;
                }

                currentIdx = endIdx;
            }
        }
        else if (RepeatMode == VolumeProfileRepeatMode.Monthly && startBarIdx >= 0 && startBarIdx < candleList.Count)
        {
            int currentIdx = startBarIdx;
            int totalCandles = candleList.Count;

            while (currentIdx < totalCandles)
            {
                var firstCandle = candleList[currentIdx];
                int monthKey = firstCandle.Timestamp.Year * 100 + firstCandle.Timestamp.Month;

                int endIdx = currentIdx;
                while (endIdx < totalCandles)
                {
                    var c = candleList[endIdx];
                    int k = c.Timestamp.Year * 100 + c.Timestamp.Month;
                    if (k != monthKey) break;
                    endIdx++;
                }

                int count = endIdx - currentIdx;
                var slice = new List<CoreCandleData>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(candleList[currentIdx + i]);
                }

                var profile = VolumeAnalysis.CalculateProfile(slice, 50);
                var segVa = VolumeAnalysis.CalculateValueArea(profile);
                var pocBin = profile.OrderByDescending(b => b.TotalVolume).FirstOrDefault();

                var segment = new VolumeProfileSegment
                {
                    StartIndex = currentIdx,
                    Count = count,
                    StartTime = slice[0].Timestamp,
                    EndTime = slice[^1].Timestamp,
                    IsPartial = (currentIdx == startBarIdx && slice[0].Timestamp.Day != 1) || endIdx == totalCandles,
                    Bins = profile,
                    POC = pocBin?.Price,
                    VAH = segVa.VAH,
                    VAL = segVa.VAL
                };
                Segments.Add(segment);

                if (currentIdx == startBarIdx)
                {
                    ProfileData = profile;
                    VAH = segVa.VAH;
                    VAL = segVa.VAL;
                }

                currentIdx = endIdx;
            }
        }
        else if (range.Count > 0)
        {
            var pocBin = ProfileData.OrderByDescending(b => b.TotalVolume).FirstOrDefault();
            Segments.Add(new VolumeProfileSegment
            {
                StartIndex = startBarIdx,
                Count = range.Count,
                StartTime = range[0].Timestamp,
                EndTime = range[^1].Timestamp,
                IsPartial = false,
                Bins = ProfileData,
                POC = pocBin?.Price,
                VAH = VAH,
                VAL = VAL
            });
        }
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var clip = canvas.LocalClipBounds;
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        float x1 = (float)p1.X;
        float x2 = (float)p2.X;

        _backgroundPaint.Color = SkiaFillColor;
        _profilePaint.Color = SkiaProfileColor;
        _valueAreaPaint.Color = SkiaValueColor;

        byte lineAlpha = (byte)(IsSelected ? Color.A : 180);
        _linePaint.Color = new SKColor(Color.R, Color.G, Color.B, lineAlpha);
        _linePaint.StrokeWidth = (float)(Thickness + (IsSelected ? 1 : 0));

        if (RepeatMode != VolumeProfileRepeatMode.Default && Segments.Count > 0)
        {
            int baseBars = _rangeBars > 0 ? _rangeBars : 1;
            for (int s = 0; s < Segments.Count; s++)
            {
                var seg = Segments[s];
                if (seg.Bins == null || seg.Bins.Count == 0) continue;

                float segLeft = (float)transform.ChartToScreen(new ChartPoint(seg.StartTime, 0)).X;
                float segRight;

                if (s < Segments.Count - 1)
                {
                    segRight = (float)transform.ChartToScreen(new ChartPoint(Segments[s + 1].StartTime, 0)).X;
                }
                else
                {
                    float endX = (float)transform.ChartToScreen(new ChartPoint(seg.EndTime, 0)).X;
                    if (s > 0)
                    {
                        float prevLeft = (float)transform.ChartToScreen(new ChartPoint(Segments[s - 1].StartTime, 0)).X;
                        float standardInterval = Math.Abs(segLeft - prevLeft);
                        float ratio = (float)seg.Count / Math.Max(1, Segments[s - 1].Count);
                        segRight = segLeft + (segLeft >= prevLeft ? 1 : -1) * (standardInterval * ratio);
                    }
                    else if (seg.Count > 1)
                    {
                        float barWidth = Math.Abs(endX - segLeft) / (seg.Count - 1);
                        segRight = endX + (endX >= segLeft ? 1 : -1) * barWidth;
                    }
                    else
                    {
                        segRight = Math.Max(x1, x2);
                        if (Math.Abs(segRight - segLeft) < 1f) segRight = endX + 20f;
                    }
                }

                float left = Math.Min(segLeft, segRight);
                float right = Math.Max(segLeft, segRight);
                float width = right - left;

                // Frustum Culling: Skip rendering if segment is completely outside viewport clip bounds
                if (right < clip.Left || left > clip.Right || width <= 0f) continue;

                // 1. Draw Background Fill Band
                var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
                canvas.DrawRect(bandRect, _backgroundPaint);

                // 2. Draw Histogram Bins
                for (int i = 0; i < seg.Bins.Count; i++)
                {
                    var bin = seg.Bins[i];
                    float yTop = (float)transform.GetYFromPrice(bin.UpperBound);
                    float yBottom = (float)transform.GetYFromPrice(bin.LowerBound);
                    float barWidth = (float)(bin.WidthPercent * width);
                    float xStart = left;
                    float xEnd = left + barWidth;

                    var rect = new SKRect(
                        Math.Min(xStart, xEnd),
                        Math.Min(yTop, yBottom),
                        Math.Max(xStart, xEnd),
                        Math.Max(yTop, yBottom));

                    bool isValueArea = seg.VAL.HasValue && seg.VAH.HasValue &&
                                      bin.Price >= seg.VAL.Value && bin.Price <= seg.VAH.Value;
                    canvas.DrawRect(rect, isValueArea ? _valueAreaPaint : _profilePaint);
                }

                // 3. Draw Vertical Boundary Line at segment start
                canvas.DrawLine(left, clip.Top, left, clip.Bottom, _linePaint);

                // For the last segment, also draw the right boundary line
                if (s == Segments.Count - 1)
                {
                    canvas.DrawLine(right, clip.Top, right, clip.Bottom, _linePaint);
                }
            }
        }
        else
        {
            float left = Math.Min(x1, x2);
            float right = Math.Max(x1, x2);
            float width = right - left;

            // 1. Draw Background Fill Band
            var bandRect = new SKRect(left, clip.Top, right, clip.Bottom);
            canvas.DrawRect(bandRect, _backgroundPaint);

            // 2. Draw Histogram if profile data exists
            if (ProfileData != null && ProfileData.Count > 0 && width > 0)
            {
                for (int i = 0; i < ProfileData.Count; i++)
                {
                    var bin = ProfileData[i];
                    float yTop = (float)transform.GetYFromPrice(bin.UpperBound);
                    float yBottom = (float)transform.GetYFromPrice(bin.LowerBound);

                    float barWidth = (float)(bin.WidthPercent * width);
                    float xStart = left;
                    float xEnd = left + barWidth;

                    var rect = new SKRect(
                        Math.Min(xStart, xEnd),
                        Math.Min(yTop, yBottom),
                        Math.Max(xStart, xEnd),
                        Math.Max(yTop, yBottom));

                    bool isValueArea = bin.Price >= VAL && bin.Price <= VAH;
                    canvas.DrawRect(rect, isValueArea ? _valueAreaPaint : _profilePaint);
                }
            }

            // 3. Draw Full-Height Vertical Lines at Start and End
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _linePaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _linePaint);
        }

        // 4. Draw Handles (at p1 and p2)
        SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;
        if (IsLocked) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(p1.X, p2.X);
        double maxX = Math.Max(p1.X, p2.X);

        if (RepeatMode != VolumeProfileRepeatMode.Default && Segments.Count > 0)
        {
            var firstSeg = Segments[0];
            var lastSeg = Segments[^1];
            double firstX = transform.ChartToScreen(new ChartPoint(firstSeg.StartTime, 0)).X;
            double lastEndX = transform.ChartToScreen(new ChartPoint(lastSeg.EndTime, 0)).X;

            double lastRightX;
            if (Segments.Count > 1)
            {
                double lastLeftX = transform.ChartToScreen(new ChartPoint(lastSeg.StartTime, 0)).X;
                double prevLeftX = transform.ChartToScreen(new ChartPoint(Segments[^2].StartTime, 0)).X;
                double standardInterval = Math.Abs(lastLeftX - prevLeftX);
                double ratio = (double)lastSeg.Count / Math.Max(1, Segments[^2].Count);
                lastRightX = lastLeftX + (lastLeftX >= prevLeftX ? 1 : -1) * (standardInterval * ratio);
            }
            else if (lastSeg.Count > 1)
            {
                double lastLeftX = transform.ChartToScreen(new ChartPoint(lastSeg.StartTime, 0)).X;
                double barWidth = Math.Abs(lastEndX - lastLeftX) / (lastSeg.Count - 1);
                lastRightX = lastEndX + (lastEndX >= lastLeftX ? 1 : -1) * barWidth;
            }
            else
            {
                lastRightX = Math.Max(maxX, lastEndX);
            }

            minX = Math.Min(minX, Math.Min(firstX, Math.Min(lastEndX, lastRightX)));
            maxX = Math.Max(maxX, Math.Max(firstX, Math.Max(lastEndX, lastRightX)));
        }

        bool hitX = screenPoint.X >= minX - tolerance && screenPoint.X <= maxX + tolerance;
        if (!hitX) return false;

        if (transform.ScreenRect.Height > 0)
        {
            return screenPoint.Y >= transform.ScreenRect.Top - tolerance && screenPoint.Y <= transform.ScreenRect.Bottom + tolerance;
        }

        return true;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price);
        }
    }

    public void TranslateBars(int deltaBars, IReadOnlyList<CoreCandleData> candles)
    {
        if (Points.Count < 2 || candles == null || candles.Count == 0 || deltaBars == 0) return;

        int idx0 = FindNearestBarIndex(Points[0].Time, candles);
        int idx1 = FindNearestBarIndex(Points[1].Time, candles);

        if (idx0 < 0 || idx1 < 0) return;

        int minIdx = Math.Min(idx0, idx1);
        int maxIdx = Math.Max(idx0, idx1);

        int clampedDelta = Math.Clamp(deltaBars, -minIdx, candles.Count - 1 - maxIdx);
        if (clampedDelta == 0) return;

        Points[0] = new ChartPoint(candles[idx0 + clampedDelta].Timestamp, Points[0].Price);
        Points[1] = new ChartPoint(candles[idx1 + clampedDelta].Timestamp, Points[1].Price);
    }

    public static int FindNearestBarIndex(DateTime time, IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0) return -1;
        int low = 0;
        int high = candles.Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int cmp = candles[mid].Timestamp.CompareTo(time);
            if (cmp == 0) return mid;
            if (cmp < 0) low = mid + 1;
            else high = mid - 1;
        }
        if (low >= candles.Count) return candles.Count - 1;
        if (high < 0) return 0;
        long diffLow = Math.Abs((candles[low].Timestamp - time).Ticks);
        long diffHigh = Math.Abs((candles[high].Timestamp - time).Ticks);
        return diffHigh <= diffLow ? high : low;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (ProfileData == null || ProfileData.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var vaColor = new IndicatorColor(ValueAreaColor.A, ValueAreaColor.R, ValueAreaColor.G, ValueAreaColor.B);

        decimal pocPrice;
        decimal vah = VAH;
        decimal val = VAL;

        if (RepeatMode != VolumeProfileRepeatMode.Default && Segments.Count > 0)
        {
            VolumeProfileSegment? matching = null;
            for (int i = 0; i < Segments.Count; i++)
            {
                var segStart = Segments[i].StartTime;
                var segNextStart = (i < Segments.Count - 1) ? Segments[i + 1].StartTime : DateTime.MaxValue;
                if (timestamp >= segStart && (timestamp <= Segments[i].EndTime || timestamp < segNextStart))
                {
                    matching = Segments[i];
                    break;
                }
            }

            if (matching != null && matching.POC.HasValue)
            {
                pocPrice = matching.POC.Value;
                vah = matching.VAH ?? vah;
                val = matching.VAL ?? val;
            }
            else
            {
                var pocBin = ProfileData.OrderByDescending(b => b.TotalVolume).FirstOrDefault();
                pocPrice = pocBin?.Price ?? 0m;
            }
        }
        else
        {
            var pocBin = ProfileData.OrderByDescending(b => b.TotalVolume).FirstOrDefault();
            pocPrice = pocBin?.Price ?? 0m;
        }

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("POC", "Point of Control (POC)", pocPrice, $"{pocPrice:F2}", color),
            new DrawingCalculatedValue("VAH", "Value Area High (VAH)", vah, $"{vah:F2}", vaColor),
            new DrawingCalculatedValue("VAL", "Value Area Low (VAL)", val, $"{val:F2}", vaColor)
        };
    }
}
