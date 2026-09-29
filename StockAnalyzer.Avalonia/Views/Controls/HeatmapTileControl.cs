using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

public class HeatmapTileControl : Control, ICustomDrawOperation
{
    public static readonly StyledProperty<IReadOnlyList<HeatmapEntry>?> EntriesProperty =
        AvaloniaProperty.Register<HeatmapTileControl, IReadOnlyList<HeatmapEntry>?>(nameof(Entries));

    public IReadOnlyList<HeatmapEntry>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    private readonly SKPaint[] _fillLut = new SKPaint[101];
    private SKPaint? _borderPaint;
    private SKPaint? _textPaint;
    private SKPaint? _largeTextPaint;
    private SKPaint? _groupBorderPaint;
    private ThemeColors? _lastTheme;

    private readonly List<TileBounds> _hitMap = new();

    private record TileBounds(SKRect Rect, string Ticker);

    static HeatmapTileControl()
    {
        AffectsRender<HeatmapTileControl>(EntriesProperty);
    }

    private void InitializePaints(ThemeColors colors)
    {
        if (_lastTheme == colors && _borderPaint != null) return;
        _lastTheme = colors;

        var bullishColor = colors.Bullish.ToSkColor();
        var bearishColor = colors.Bearish.ToSkColor();
        var midpointColor = colors.ChartBackground.ToSkColor();

        for (int i = 0; i <= 100; i++)
        {
            float t = i / 100f;
            SKColor color;
            if (t < 0.5f)
            {
                color = Interpolate(bearishColor, midpointColor, t * 2);
            }
            else
            {
                color = Interpolate(midpointColor, bullishColor, (t - 0.5f) * 2);
            }
            if (_fillLut[i] == null)
            {
                _fillLut[i] = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            }
            _fillLut[i].Color = color;
        }

        if (_borderPaint == null) _borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        _borderPaint.Color = colors.ShellBorder.ToSkColor();

        if (_groupBorderPaint == null) _groupBorderPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        _groupBorderPaint.Color = colors.ShellBorder.ToSkColor();

        if (_textPaint == null) _textPaint = new SKPaint { IsAntialias = true, TextSize = 10 };
        _textPaint.Color = SKColors.White;

        if (_largeTextPaint == null) _largeTextPaint = new SKPaint { IsAntialias = true, TextSize = 13, FakeBoldText = true };
        _largeTextPaint.Color = SKColors.White;
    }

    private static SKColor Interpolate(SKColor c1, SKColor c2, float t)
    {
        return new SKColor(
            (byte)(c1.Red + (c2.Red - c1.Red) * t),
            (byte)(c1.Green + (c2.Green - c1.Green) * t),
            (byte)(c1.Blue + (c2.Blue - c1.Blue) * t)
        );
    }

    private global::Avalonia.Rect _operationBounds;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        for (int i = 0; i <= 100; i++)
        {
            _fillLut[i]?.Dispose();
            _fillLut[i] = null!;
        }
        _borderPaint?.Dispose();
        _borderPaint = null;
        _textPaint?.Dispose();
        _textPaint = null;
        _largeTextPaint?.Dispose();
        _largeTextPaint = null;
        _groupBorderPaint?.Dispose();
        _groupBorderPaint = null;
        _lastTheme = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var skP = new SKPoint((float)p.X, (float)p.Y);

        // Simple hit test
        for (int i = 0; i < _hitMap.Count; i++)
        {
            if (_hitMap[i].Rect.Contains(skP.X, skP.Y))
            {
                WeakReferenceMessenger.Default.Send(new TickerSelectedMessage(_hitMap[i].Ticker));
                break;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        _operationBounds = new global::Avalonia.Rect(Bounds.Size);
        if (Entries == null || Entries.Count == 0) return;
        context.Custom(this);
    }

    global::Avalonia.Rect ICustomDrawOperation.Bounds => _operationBounds;
    void IDisposable.Dispose() { }
    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    bool ICustomDrawOperation.HitTest(global::Avalonia.Point p) => _operationBounds.Contains(p);

    void ICustomDrawOperation.Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        var themeManager = App.Current.Services?.GetService<IThemeManager>();
        var themeColors = themeManager?.CurrentTheme ?? ThemeColors.Dark;
        InitializePaints(themeColors);

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        var entries = Entries;
        if (entries == null || entries.Count == 0) return;

        _hitMap.Clear();

        // 1. Hierarchical Grouping (Region > Sector)
        var grouped = entries
            .GroupBy(e => e.Region)
            .Select(rg => new GroupInfo(
                rg.Key,
                (float)rg.Sum(e => e.Weight),
                rg.GroupBy(e => e.Sector)
                  .Select(sg => new GroupInfo(
                      null, // Sub-group title omitted for simplicity here
                      (float)sg.Sum(e => e.Weight),
                      null,
                      sg.OrderByDescending(e => e.Weight).ToList()))
                  .OrderByDescending(s => s.Weight)
                  .ToList(),
                null))
            .OrderByDescending(r => r.Weight)
            .ToList();

        // 2. Proportional Layout Calculation (Slice-and-Dice)
        LayoutHierarchical(canvas, new SKRect(0, 0, (float)_operationBounds.Width, (float)_operationBounds.Height), grouped, true);
    }

    private void LayoutHierarchical(SKCanvas canvas, SKRect rect, List<GroupInfo> groups, bool horizontal)
    {
        float totalWeight = groups.Sum(g => g.Weight);
        if (totalWeight <= 0 || rect.Width <= 0 || rect.Height <= 0) return;

        float currentOffset = horizontal ? rect.Left : rect.Top;

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            float ratio = g.Weight / totalWeight;
            SKRect slice;

            if (horizontal)
            {
                float w = rect.Width * ratio;
                slice = new SKRect(currentOffset, rect.Top, currentOffset + w, rect.Bottom);
                currentOffset += w;
            }
            else
            {
                float h = rect.Height * ratio;
                slice = new SKRect(rect.Left, currentOffset, rect.Right, currentOffset + h);
                currentOffset += h;
            }

            if (g.SubGroups != null)
            {
                // Recursive call for next level (vertical)
                LayoutHierarchical(canvas, slice, g.SubGroups, !horizontal);
                
                // Draw Region boundary
                if (!string.IsNullOrEmpty(g.Title))
                {
                    canvas.DrawRect(slice, _groupBorderPaint);
                }
            }
            else if (g.Tickers != null)
            {
                // Bottom level (Tickers)
                LayoutTickers(canvas, slice, g.Tickers, !horizontal);
            }
        }
    }

    private void LayoutTickers(SKCanvas canvas, SKRect rect, List<HeatmapEntry> tickers, bool horizontal)
    {
        float totalWeight = tickers.Sum(t => (float)t.Weight);
        if (totalWeight <= 0) return;

        float currentOffset = horizontal ? rect.Left : rect.Top;

        for (int i = 0; i < tickers.Count; i++)
        {
            var t = tickers[i];
            float ratio = (float)t.Weight / totalWeight;
            SKRect tile;

            if (horizontal)
            {
                float w = rect.Width * ratio;
                tile = new SKRect(currentOffset, rect.Top, currentOffset + w, rect.Bottom);
                currentOffset += w;
            }
            else
            {
                float h = rect.Height * ratio;
                tile = new SKRect(rect.Left, currentOffset, rect.Right, currentOffset + h);
                currentOffset += h;
            }

            DrawTile(canvas, tile, t);
        }
    }

    private void DrawTile(SKCanvas canvas, SKRect rect, HeatmapEntry entry)
    {
        // Add to hit map
        _hitMap.Add(new TileBounds(rect, entry.Ticker));

        // 1. Fill based on Return
        float ret = (float)entry.Return;
        int lutIndex = Math.Clamp((int)((ret + 0.05f) * 1000), 0, 100);
        canvas.DrawRect(rect, _fillLut[lutIndex]);
        canvas.DrawRect(rect, _borderPaint);

        // 2. Draw Text (Clipped)
        if (rect.Width > 35 && rect.Height > 25)
        {
            canvas.Save();
            canvas.ClipRect(rect);
            canvas.DrawText(entry.Ticker, rect.Left + 4, rect.Top + 13, _largeTextPaint);
            canvas.DrawText($"{entry.Return:P2}", rect.Left + 4, rect.Top + 24, _textPaint);
            canvas.Restore();
        }
    }

    private class GroupInfo
    {
        public string? Title { get; }
        public float Weight { get; }
        public List<GroupInfo>? SubGroups { get; }
        public List<HeatmapEntry>? Tickers { get; }

        public GroupInfo(string? title, float weight, List<GroupInfo>? subGroups, List<HeatmapEntry>? tickers)
        {
            Title = title;
            Weight = weight;
            SubGroups = subGroups;
            Tickers = tickers;
        }
    }
}
