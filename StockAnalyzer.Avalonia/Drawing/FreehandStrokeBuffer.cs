using System;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Fixed-capacity, zero-allocation buffer for storing sampled ChartPoints during an active freehand stroke.
/// </summary>
public sealed class FreehandStrokeBuffer
{
    private readonly ChartPoint[] _points;
    private int _count;

    public FreehandStrokeBuffer(int capacity = DrawingInteractionLimits.MaxStrokeSamples)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _points = new ChartPoint[capacity];
        _count = 0;
    }

    /// <summary>
    /// Gets the number of points currently held in the buffer.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Gets the maximum capacity of points the buffer can hold.
    /// </summary>
    public int Capacity => _points.Length;

    /// <summary>
    /// Attempts to append a point to the buffer.
    /// Returns true if appended, or false if the buffer is at maximum capacity.
    /// </summary>
    public bool TryAppend(ChartPoint point)
    {
        if (_count >= _points.Length)
        {
            return false;
        }

        _points[_count++] = point;
        return true;
    }

    /// <summary>
    /// Returns a readonly span view over the valid points in the buffer without heap allocation.
    /// </summary>
    public ReadOnlySpan<ChartPoint> AsSpan() => new ReadOnlySpan<ChartPoint>(_points, 0, _count);

    /// <summary>
    /// Gets a point at the specified index.
    /// </summary>
    public ChartPoint this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return _points[index];
        }
    }

    /// <summary>
    /// Resets the point count to 0 without reallocating or zeroing the buffer array.
    /// </summary>
    public void Clear()
    {
        _count = 0;
    }
}
