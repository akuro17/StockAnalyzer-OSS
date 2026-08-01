using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views.Controls;

public class AllocationDoughnutChartControl : Control, ICustomDrawOperation
{
    public static readonly StyledProperty<IEnumerable<AllocationEntryViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<AllocationDoughnutChartControl, IEnumerable<AllocationEntryViewModel>?>(nameof(Items));

    public IEnumerable<AllocationEntryViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private IDisposable? _collectionChangedSubscription;
    private readonly SKPaint _paint;
    private Rect _operationBounds;
    private IList<AllocationEntryViewModel>? _itemsSource;

    static AllocationDoughnutChartControl()
    {
        AffectsRender<AllocationDoughnutChartControl>(ItemsProperty);
    }

    public AllocationDoughnutChartControl()
    {
        _paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            _collectionChangedSubscription?.Dispose();
            
            if (change.NewValue is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += OnItemsCollectionChanged;
                _collectionChangedSubscription = System.Reactive.Disposables.Disposable.Create(() => 
                    ncc.CollectionChanged -= OnItemsCollectionChanged);
            }
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        
        // Try to cast to IList to avoid ToList() allocation. 
        // ObservableCollection implements IList.
        _itemsSource = Items as IList<AllocationEntryViewModel> ?? Items?.ToList();
        if (_itemsSource == null || _itemsSource.Count == 0) return;

        _operationBounds = new Rect(Bounds.Size);
        context.Custom(this);
    }

    // ICustomDrawOperation Implementation
    Rect ICustomDrawOperation.Bounds => _operationBounds;
    void IDisposable.Dispose() { }
    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    bool ICustomDrawOperation.HitTest(global::Avalonia.Point p) => _operationBounds.Contains(p);

    void ICustomDrawOperation.Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        float width = (float)_operationBounds.Width;
        float height = (float)_operationBounds.Height;
        
        if (width <= 0 || height <= 0) return;

        float size = Math.Min(width, height) * 0.9f;
        var centerX = width / 2;
        var centerY = height / 2;

        float currentAngle = -90f;
        const float holeRadiusPct = 0.6f;
        float thickness = (size / 2) * (1 - holeRadiusPct);
        float radius = (size / 2) - (thickness / 2);
        
        var arcRect = new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius);
        
        _paint.StrokeWidth = thickness;

        // Use for loop on IList to avoid enumerator allocation (boxing)
        for (int i = 0; i < _itemsSource!.Count; i++)
        {
            var item = _itemsSource[i];
            if (item.Percentage <= 0) continue;

            float sweepAngle = (float)item.Percentage * 3.6f;
            _paint.Color = new SKColor(item.Color);

            canvas.DrawArc(arcRect, currentAngle, sweepAngle, false, _paint);
            currentAngle += sweepAngle;
        }
    }
}
