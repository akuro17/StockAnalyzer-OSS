using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Provides a ZeroAllocation cache for SKPaint objects mapped to SemanticRoles.
/// Managed by the ChartRenderPipeline and synchronized with the current application theme.
/// </summary>
public sealed class SemanticPaintCache : IDisposable
{
    private static readonly SemanticRole[] _allRoles = Enum.GetValues<SemanticRole>();
    private readonly Dictionary<SemanticRole, SKPaint> _strokePaints = new();
    private readonly Dictionary<SemanticRole, SKPaint> _fillPaints = new();
    private ThemeColors? _lastTheme;

    /// <summary>
    /// Updates the cache if the theme has changed.
    /// This should be called at the beginning of each render pass or when the theme changes.
    /// </summary>
    public void Update(ThemeColors theme)
    {
        // Reference equality check is sufficient if ThemeColors is replaced on change
        if (ReferenceEquals(_lastTheme, theme)) return;
        
        _lastTheme = theme;
        
        foreach (SemanticRole role in _allRoles)
        {
            var color = theme.GetSemanticColor(role);
            
            // Update Stroke Paint
            if (!_strokePaints.TryGetValue(role, out var strokePaint))
            {
                strokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.0f
                };
                _strokePaints[role] = strokePaint;
            }
            strokePaint.Color = color.ToSkColor();

            // Update Fill Paint (with lower alpha if it's a structural role like Support/Resistance)
            if (!_fillPaints.TryGetValue(role, out var fillPaint))
            {
                fillPaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };
                _fillPaints[role] = fillPaint;
            }
            
            // For fill, we might want to apply a default transparency for certain roles
            var fillColor = (role == SemanticRole.Support || role == SemanticRole.Resistance || role == SemanticRole.Neutral)
                ? color.ToSkColor().WithAlpha(30) // Subtle tint
                : color.ToSkColor();
            fillPaint.Color = fillColor;
        }
    }

    /// <summary>
    /// Gets a cached stroke paint for the specified role.
    /// DO NOT dispose the returned paint.
    /// </summary>
    public SKPaint GetStrokePaint(SemanticRole role) => _strokePaints[role];

    /// <summary>
    /// Gets a cached fill paint for the specified role.
    /// DO NOT dispose the returned paint.
    /// </summary>
    public SKPaint GetFillPaint(SemanticRole role) => _fillPaints[role];

    public void Dispose()
    {
        foreach (var paint in _strokePaints.Values) paint.Dispose();
        foreach (var paint in _fillPaints.Values) paint.Dispose();
        _strokePaints.Clear();
        _fillPaints.Clear();
    }
}
