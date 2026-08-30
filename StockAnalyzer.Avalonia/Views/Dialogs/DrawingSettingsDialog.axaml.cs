using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Drawing;
using Avalonia.Media;
using System;

using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class DrawingSettingsDialog : Window
{
    private IChartObject? _drawing;
    private IDrawingSettingsPanelDefinition? _panelDefinition;
    private readonly Action<IChartObject>? _onApply;

    public DrawingSettingsDialog() { }

    public DrawingSettingsDialog(IChartObject drawing) : this(drawing, null) { }

    public DrawingSettingsDialog(IChartObject drawing, IDrawingSettingsPanelRegistry? registry, Action<IChartObject>? onApply = null)
    {
        InitializeComponent();
        _drawing = drawing;
        _onApply = onApply;

        // Reflects the layer's current name (CustomName, falling back to the localized type name --
        // the same rule the Layers Panel uses) instead of a fixed "Drawing Settings" string.
        var displayName = DrawingObjectDisplayNameHelper.GetDisplayName(_drawing);
        Title = displayName;
        var headerTitleText = this.FindControl<TextBlock>("HeaderTitleText");
        if (headerTitleText != null)
        {
            headerTitleText.Text = displayName;
        }

        // Tool-specific window sizing (e.g. LineText/CurveLineText's wider 3-column layout) is
        // resolved via the registered IDrawingSettingsPanelDefinition instead of a hardcoded type
        // check here, so new tools can opt into a custom dialog size without editing this file.
        _panelDefinition = registry?.Resolve(_drawing);
        if (_panelDefinition?.WindowHint is { } hint)
        {
            if (hint.Width.HasValue) Width = hint.Width.Value;
            if (hint.MinWidth.HasValue) MinWidth = hint.MinWidth.Value;
            CanResize = hint.CanResize;
        }

        // Initialize UI with drawing properties
        var thicknessSpin = this.FindControl<NumericUpDown>("ThicknessSpin");
        if (thicknessSpin != null)
        {
            thicknessSpin.Value = (decimal)_drawing.Thickness;
        }

        // Initialize generic color
        var colorPicker = this.FindControl<ColorPicker>("ColorPickerControl");
        if (colorPicker != null)
        {
            colorPicker.Color = _drawing.Color;
        }

        var settingsContentPanel = this.FindControl<StackPanel>("SettingsContentPanel");

        _panelDefinition?.Activate(this);

        // Programmatic width synchronization to avoid compiled XAML binding errors.
        // Source must be a control that stays visible regardless of drawing type:
        // ThicknessSpin's panel is hidden for Text/Callout/PriceLabel/etc., which previously
        // starved this handler of a non-zero Bounds.Width and left their ColorPickers unstretched.
        // For LineText/CurveLineText, ColorPickerControl now lives in the narrower "Common" column
        // (see above), so it is synced separately below instead of to the full dialog width.
        if (settingsContentPanel != null)
        {
            settingsContentPanel.PropertyChanged += (s, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    double w = settingsContentPanel.Bounds.Width;
                    if (w > 0)
                    {
                        var pickers = new[]
                        {
                            _panelDefinition?.ManagesGenericColorPickerWidth == true ? null : this.FindControl<ColorPicker>("ColorPickerControl"),
                            this.FindControl<ColorPicker>("DtwUnmatchedColorPicker"),
                            this.FindControl<ColorPicker>("HarmonicFillColorPicker"),
                            this.FindControl<ColorPicker>("AutoElliottFillColorPicker"),
                            this.FindControl<ColorPicker>("DtwFillColorPicker"),
                            this.FindControl<ColorPicker>("KalmanFillColorPicker"),
                            this.FindControl<ColorPicker>("NurbsConicFillColorPicker"),
                            this.FindControl<ColorPicker>("ValueAreaColorPicker"),
                            this.FindControl<ColorPicker>("BarUpColorPicker"),
                            this.FindControl<ColorPicker>("BarDownColorPicker"),
                            this.FindControl<ColorPicker>("LsTargetColorPicker"),
                            this.FindControl<ColorPicker>("LsStopColorPicker"),
                            this.FindControl<ColorPicker>("TextBackgroundColorPicker"),
                            this.FindControl<ColorPicker>("EllipseControlPointColorPicker"),
                            this.FindControl<ColorPicker>("FftPeakColorPicker")
                        };
                        foreach (var picker in pickers)
                        {
                            if (picker != null) picker.Width = w;
                        }
                    }
                }
            };
        }

        _panelDefinition?.Populate(this, _drawing);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    /// <summary>
    /// Commits every UI control's current value onto <see cref="_drawing"/>. Shared by OK (commit
    /// then close) and Apply (commit without closing, so the change previews live on the chart).
    /// </summary>
    private void ApplyCurrentSettingsToModel()
    {
        if (_drawing == null) return;

        // Apply generic color
        var colorPicker = this.FindControl<ColorPicker>("ColorPickerControl");
        if (colorPicker != null)
        {
            _drawing.Color = colorPicker.Color;
        }

        var thicknessSpin = this.FindControl<NumericUpDown>("ThicknessSpin");
        if (thicknessSpin?.Value != null)
        {
            _drawing.Thickness = (double)thicknessSpin.Value;
        }

        _panelDefinition?.Commit(this, _drawing);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_drawing == null)
        {
             Close((DrawingSettingsResult?)DrawingSettingsResult.None);
             return;
        }

        try
        {
            ApplyCurrentSettingsToModel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DrawingSettingsDialog commit error: {ex.Message}");
        }
        finally
        {
            Close((DrawingSettingsResult?)DrawingSettingsResult.Changed);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close((DrawingSettingsResult?)DrawingSettingsResult.None);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        Close((DrawingSettingsResult?)DrawingSettingsResult.Deleted);
    }

    /// <summary>
    /// Commits the current settings without closing the dialog, then notifies the caller (via
    /// <see cref="_onApply"/>) so it can persist and redraw -- the same effect OK has, minus the
    /// close, so the user can preview several changes on the live chart before deciding OK/Cancel.
    /// </summary>
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_drawing == null) return;

        try
        {
            ApplyCurrentSettingsToModel();
            _onApply?.Invoke(_drawing);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DrawingSettingsDialog apply error: {ex.Message}");
        }
    }
}
