using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
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
    private Operation? _operation;
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
        var old = _operation; _operation = null;
        var presentation = ViewModel?.RlPresentation;
        global::Avalonia.Automation.AutomationProperties.SetName(this, presentation is null ? ""
            : string.Join(" · ", presentation.ModelId, presentation.PolicyId, presentation.Units, presentation.Legend, presentation.HudText));
        (_renderer ??= new RlQRenderer()).PreparePanel(ViewModel?.RlPresentation, (float)BaseFontSize, (float)HelperFontSize);
        if (ViewModel?.RlPresentation is { } p && TextBrush is ISolidColorBrush brush)
        {
            var c = brush.Color;
            _operation = new(_renderer ??= new RlQRenderer(), new Rect(Bounds.Size), p, (float)BaseFontSize, (float)HelperFontSize, new(c.R,c.G,c.B,c.A),
                Utilities.ColorHelper.ParseColorSafe(ViewModel.NeutralColor), Utilities.ColorHelper.ParseColorSafe(ViewModel.BullishColor), Utilities.ColorHelper.ParseColorSafe(ViewModel.BearishColor));
        }
        old?.Dispose(); InvalidateVisual();
    }
    public override void Render(DrawingContext context) { if (_operation is not null) context.Custom(_operation); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { if (ViewModel is not null) ViewModel.PropertyChanged -= Changed; _operation = null; _renderer?.Dispose(); _renderer = null; base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (ViewModel is not null) { ViewModel.PropertyChanged -= Changed; ViewModel.PropertyChanged += Changed; } Rebuild(); }
    private sealed class Operation(RlQRenderer renderer, Rect bounds, RlPresentation presentation, float baseSize, float helperSize, SKColor foreground, SKColor hold, SKColor buy, SKColor sell) : ICustomDrawOperation
    {

        public Rect Bounds => bounds;
        public bool HitTest(global::Avalonia.Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        // Lifetime belongs to the control; Avalonia may dispose scene references between frames.
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            renderer.DrawPanel(lease.SkCanvas, bounds, presentation, baseSize, helperSize, foreground, hold, buy, sell);
        }
    }
}
