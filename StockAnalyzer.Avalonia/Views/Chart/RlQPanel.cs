using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.Views.Chart;

public sealed class RlQPanel : Control
{
    public static readonly StyledProperty<ChartViewModel?> ViewModelProperty = AvaloniaProperty.Register<RlQPanel, ChartViewModel?>(nameof(ViewModel));
    public static readonly StyledProperty<double> BaseFontSizeProperty = AvaloniaProperty.Register<RlQPanel, double>(nameof(BaseFontSize));
    public static readonly StyledProperty<double> HelperFontSizeProperty = AvaloniaProperty.Register<RlQPanel, double>(nameof(HelperFontSize));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<RlQPanel, IBrush?>(nameof(TextBrush));
    public ChartViewModel? ViewModel { get => GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    public double BaseFontSize { get => GetValue(BaseFontSizeProperty); set => SetValue(BaseFontSizeProperty, value); }
    public double HelperFontSize { get => GetValue(HelperFontSizeProperty); set => SetValue(HelperFontSizeProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    private Scene? _scene;
    private RlQRenderer? _renderer;
    protected override global::Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new global::Avalonia.Automation.Peers.ControlAutomationPeer(this);
    public RlQPanel() { Height = (double)RlContract.PanelHeight; global::Avalonia.Automation.AutomationProperties.SetAutomationId(this, Common.RlUiConstants.QPanel); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is ChartViewModel old) old.PropertyChanged -= Changed;
            if (change.NewValue is ChartViewModel next) next.PropertyChanged += Changed;
        }
        if (change.Property == ViewModelProperty || change.Property == BoundsProperty || change.Property == BaseFontSizeProperty
            || change.Property == HelperFontSizeProperty || change.Property == TextBrushProperty) Rebuild();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(ChartViewModel.RlPresentation) or nameof(ChartViewModel.BullishColor) or nameof(ChartViewModel.BearishColor) or nameof(ChartViewModel.NeutralColor)) Rebuild(); }
    private void Rebuild()
    {
        _scene = null;
        // Native resources exist only while attached; OnAttachedToVisualTree rebuilds, so detached property changes create nothing.
        if (this.GetVisualRoot() is null) return;
        var presentation = ViewModel?.RlPresentation;
        global::Avalonia.Automation.AutomationProperties.SetName(this, presentation is null ? ""
            : string.Join(" · ", presentation.ModelId, presentation.PolicyId, presentation.Units, presentation.Legend, presentation.HudText));
        (_renderer ??= new RlQRenderer()).PreparePanel(ViewModel?.RlPresentation, (float)BaseFontSize, (float)HelperFontSize);
        if (ViewModel?.RlPresentation is { } p && TextBrush is ISolidColorBrush brush)
        {
            var c = brush.Color;
            _scene = new(p, (float)BaseFontSize, (float)HelperFontSize, new(c.R,c.G,c.B,c.A),
                Utilities.ColorHelper.ParseColorSafe(ViewModel.NeutralColor), Utilities.ColorHelper.ParseColorSafe(ViewModel.BullishColor), Utilities.ColorHelper.ParseColorSafe(ViewModel.BearishColor));
        }
        InvalidateVisual();
    }
    // A fresh lightweight operation per submission; expensive resources stay in the renderer and the immutable scene prepared in Rebuild.
    public override void Render(DrawingContext context)
    { if (_scene is { } scene && _renderer is { } renderer) context.Custom(new Operation(renderer, scene, new Rect(Bounds.Size))); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { if (ViewModel is not null) ViewModel.PropertyChanged -= Changed; _scene = null; _renderer?.Dispose(); _renderer = null; base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (ViewModel is not null) { ViewModel.PropertyChanged -= Changed; ViewModel.PropertyChanged += Changed; } Rebuild(); }
    private sealed record Scene(RlPresentation Presentation, float BaseSize, float HelperSize, SKColor Foreground, SKColor Hold, SKColor Buy, SKColor Sell);
    private sealed class Operation(RlQRenderer renderer, Scene scene, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(global::Avalonia.Point p) => false;
        // Each submission is a new operation that must be treated as changed; never equal to another submission.
        public bool Equals(ICustomDrawOperation? other) => false;
        // The renderer belongs to the control (its own gate makes a draw after disposal a no-op); the operation owns nothing native.
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            renderer.DrawPanel(lease.SkCanvas, bounds, scene.Presentation, scene.BaseSize, scene.HelperSize, scene.Foreground, scene.Hold, scene.Buy, scene.Sell);
        }
    }
}
