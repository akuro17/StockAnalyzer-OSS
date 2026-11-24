using SkiaSharp;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Cached static <see cref="SKPathEffect"/> mappings for <see cref="CoreLineStyle"/>.
/// Solid, Step, and any unknown persisted enum ordinal resolve to <c>null</c> (solid stroke).
/// Zero per-frame allocation.
/// </summary>
internal static class CoreLineStyleExtensions
{
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0f);
    private static readonly SKPathEffect DotEffect = SKPathEffect.CreateDash(new float[] { 2f, 3f }, 0f);
    private static readonly SKPathEffect DashDotEffect = SKPathEffect.CreateDash(new float[] { 6f, 3f, 2f, 3f }, 0f);

    internal static SKPathEffect? ToSkPathEffect(this CoreLineStyle style) => style switch
    {
        CoreLineStyle.Dash => DashEffect,
        CoreLineStyle.Dot => DotEffect,
        CoreLineStyle.DashDot => DashDotEffect,
        CoreLineStyle.Solid => null,
        CoreLineStyle.Step => null,
        _ => null
    };
}
