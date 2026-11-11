using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views.Controls;

public class AllocationDoughnutChartControl : Control
{
    /// <summary>The inner hole's share of the outer radius; the ring thickness is (1 - this value) of the outer radius.</summary>
    private const float HoleRadiusPct = 0.6f;

    /// <summary>Share of the shorter bounds side that the doughnut's outer diameter uses.</summary>
    private const float DiameterShare = 0.9f;

    /// <summary>Degrees of sweep per percentage point (360 / 100).</summary>
    private const float DegreesPerPercent = 3.6f;

    /// <summary>Angle the first slice starts at: 12 o'clock.</summary>
    private const float StartAngleDegrees = -90f;

    public static readonly StyledProperty<IEnumerable<AllocationEntryViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<AllocationDoughnutChartControl, IEnumerable<AllocationEntryViewModel>?>(nameof(Items));

    public IEnumerable<AllocationEntryViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private INotifyCollectionChanged? _subscribedCollection;

    // The stroke paint is shared by the control and by the draw operations it created: a frame that is still being composed on the render
    // thread may hold it after the control detached. It is therefore reference counted (the control holds one reference while attached,
    // each operation one until the compositor disposes it) and disposed when the last reference is released. Created on the first
    // Render after (re)attaching.
    private SharedPaint? _sharedPaint;

    /// <summary>A stroke paint that is disposed when its last holder releases it.</summary>
    internal sealed class SharedPaint
    {
        private int _references = 1;   // the creator's reference

        public SKPaint Paint { get; } = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        /// <summary>Number of holders that have not released yet (for tests).</summary>
        internal int References => Volatile.Read(ref _references);

        /// <summary>Whether the paint has been disposed (for tests).</summary>
        internal bool IsReleased => References <= 0;

        public void AddReference() => Interlocked.Increment(ref _references);

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Paint.Dispose();
            }
        }
    }

    static AllocationDoughnutChartControl()
    {
        AffectsRender<AllocationDoughnutChartControl>(ItemsProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            Subscribe(change.NewValue as INotifyCollectionChanged);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Items as INotifyCollectionChanged);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);

        // Release the control's reference; operations still held by the compositor keep the paint alive until they are disposed.
        _sharedPaint?.Release();
        _sharedPaint = null;
    }

    // Replaces the current subscription (never more than one at a time).
    private void Subscribe(INotifyCollectionChanged? collection)
    {
        if (ReferenceEquals(_subscribedCollection, collection)) return;

        if (_subscribedCollection != null)
        {
            _subscribedCollection.CollectionChanged -= OnItemsCollectionChanged;
        }

        _subscribedCollection = collection;

        if (_subscribedCollection != null)
        {
            _subscribedCollection.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    internal bool IsSubscribedToItems => _subscribedCollection != null;

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var items = Items;
        if (items == null) return;

        // The render thread only ever sees this immutable copy; the UI-owned collection is read here, on the UI thread.
        var slices = BuildSlices(items);
        if (slices.Length == 0 && !HasAnyItem(items)) return;

        _sharedPaint ??= new SharedPaint();
        context.Custom(new DoughnutDrawOperation(new Rect(Bounds.Size), slices, _sharedPaint));
    }

    private static bool HasAnyItem(IEnumerable<AllocationEntryViewModel> items)
    {
        if (items is IList<AllocationEntryViewModel> list) return list.Count > 0;
        using var enumerator = items.GetEnumerator();
        return enumerator.MoveNext();
    }

    /// <summary>Copies (sweep, color) of every entry with a positive percentage; the sweep formula is unchanged: (float)Percentage * 3.6.</summary>
    internal static DoughnutSlice[] BuildSlices(IEnumerable<AllocationEntryViewModel> items)
    {
        if (items is IList<AllocationEntryViewModel> list)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Percentage > 0) count++;
            }

            if (count == 0) return Array.Empty<DoughnutSlice>();

            var result = new DoughnutSlice[count];
            int index = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item.Percentage <= 0) continue;
                result[index++] = new DoughnutSlice((float)item.Percentage * DegreesPerPercent, new SKColor(item.Color));
            }

            return result;
        }

        var collected = new List<DoughnutSlice>();
        foreach (var item in items)
        {
            if (item.Percentage <= 0) continue;
            collected.Add(new DoughnutSlice((float)item.Percentage * DegreesPerPercent, new SKColor(item.Color)));
        }

        return collected.ToArray();
    }

    internal readonly record struct DoughnutSlice(float SweepDegrees, SKColor Color);

    private sealed class DoughnutDrawOperation : SkiaLeaseDrawOperation
    {
        private readonly DoughnutSlice[] _slices;
        private readonly SharedPaint _sharedPaint;
        private readonly SKPaint _paint;
        private int _disposed;

        public DoughnutDrawOperation(Rect bounds, DoughnutSlice[] slices, SharedPaint sharedPaint) : base(bounds)
        {
            _slices = slices;
            _sharedPaint = sharedPaint;
            _paint = sharedPaint.Paint;
            sharedPaint.AddReference();
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _sharedPaint.Release();
            }
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
        {
            float width = (float)bounds.Width;
            float height = (float)bounds.Height;

            if (width <= 0 || height <= 0) return;

            float size = Math.Min(width, height) * DiameterShare;
            var centerX = width / 2;
            var centerY = height / 2;

            float currentAngle = StartAngleDegrees;
            float thickness = (size / 2) * (1 - HoleRadiusPct);
            float radius = (size / 2) - (thickness / 2);

            var arcRect = new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius);

            _paint.StrokeWidth = thickness;

            for (int i = 0; i < _slices.Length; i++)
            {
                float sweepAngle = _slices[i].SweepDegrees;
                _paint.Color = _slices[i].Color;

                canvas.DrawArc(arcRect, currentAngle, sweepAngle, false, _paint);
                currentAngle += sweepAngle;
            }
        }
    }
}
