using System;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Controller governing the state machine and pointer ownership for freehand stroke drawing.
/// Ensures single-pointer ownership and ZeroAllocation during in-flight gesture updates.
/// </summary>
public sealed class FreehandInputController
{
    private enum State
    {
        Idle,
        Drawing
    }

    private State _state = State.Idle;
    private long _ownedPointerId;
    private readonly FreehandStrokeBuffer _buffer;
    private DrawingCancelReason? _lastCancelReason;

    public FreehandInputController(int capacity = StockAnalyzer.Core.Models.Drawing.DrawingInteractionLimits.MaxStrokeSamples)
    {
        _buffer = new FreehandStrokeBuffer(capacity);
    }

    public FreehandInputController(FreehandStrokeBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>
    /// Gets whether a freehand stroke is actively being drawn.
    /// </summary>
    public bool IsDrawing => _state == State.Drawing;

    /// <summary>
    /// Gets the current pointer ID that owns the active drawing stroke.
    /// </summary>
    public long OwnedPointerId => _ownedPointerId;

    /// <summary>
    /// Gets the number of points currently captured in the active stroke.
    /// </summary>
    public int CurrentPointCount => _buffer.Count;

    /// <summary>
    /// Gets the reason for the most recent cancellation, if any.
    /// </summary>
    public DrawingCancelReason? LastCancelReason => _lastCancelReason;

    /// <summary>
    /// Gets a view over the current points captured in the active stroke.
    /// </summary>
    public ReadOnlySpan<ChartPoint> GetCurrentPoints() => _buffer.AsSpan();

    /// <summary>
    /// Begins a new stroke gesture.
    /// Transitions from Idle to Drawing, acquires pointer ownership, and appends the initial point.
    /// </summary>
    public DrawingInputResult Begin(FreehandInputSample input, ChartPoint chartPoint)
    {
        if (_state != State.Idle)
        {
            return DrawingInputResult.Rejected;
        }

        _lastCancelReason = null;
        _buffer.Clear();

        if (!_buffer.TryAppend(chartPoint))
        {
            _lastCancelReason = DrawingCancelReason.CapacityExceeded;
            return DrawingInputResult.Cancelled;
        }

        _ownedPointerId = input.PointerId;
        _state = State.Drawing;
        return DrawingInputResult.Started;
    }

    /// <summary>
    /// Updates an in-progress stroke gesture with a new sampled point.
    /// Ignored if the pointer ID does not match the owner.
    /// </summary>
    public DrawingInputResult Move(FreehandInputSample input, ChartPoint chartPoint)
    {
        if (_state != State.Drawing || input.PointerId != _ownedPointerId)
        {
            return DrawingInputResult.Ignored;
        }

        if (!_buffer.TryAppend(chartPoint))
        {
            // Capacity exceeded: abort completely without committing partial stroke
            _buffer.Clear();
            _state = State.Idle;
            _lastCancelReason = DrawingCancelReason.CapacityExceeded;
            return DrawingInputResult.Cancelled;
        }

        return DrawingInputResult.Updated;
    }

    /// <summary>
    /// Finalizes and commits the stroke upon pointer release.
    /// Appends the release point, resets state to Idle, and returns Committed.
    /// </summary>
    public DrawingInputResult Release(FreehandInputSample input, ChartPoint chartPoint)
    {
        if (_state != State.Drawing || input.PointerId != _ownedPointerId)
        {
            return DrawingInputResult.Ignored;
        }

        if (!_buffer.TryAppend(chartPoint))
        {
            _buffer.Clear();
            _state = State.Idle;
            _lastCancelReason = DrawingCancelReason.CapacityExceeded;
            return DrawingInputResult.Cancelled;
        }

        _state = State.Idle;
        return DrawingInputResult.Committed;
    }

    /// <summary>
    /// Explicitly cancels the active stroke and discards all collected points.
    /// </summary>
    public DrawingInputResult Cancel(DrawingCancelReason reason)
    {
        if (_state != State.Drawing)
        {
            return DrawingInputResult.Ignored;
        }

        _buffer.Clear();
        _state = State.Idle;
        _lastCancelReason = reason;
        return DrawingInputResult.Cancelled;
    }

    /// <summary>
    /// Completes the stroke immediately using currently captured points (e.g., triggered by modifier keys).
    /// </summary>
    public DrawingInputResult CompleteByModifier()
    {
        if (_state != State.Drawing)
        {
            return DrawingInputResult.Ignored;
        }

        if (_buffer.Count == 0)
        {
            _state = State.Idle;
            return DrawingInputResult.Cancelled;
        }

        _state = State.Idle;
        return DrawingInputResult.Committed;
    }
}
