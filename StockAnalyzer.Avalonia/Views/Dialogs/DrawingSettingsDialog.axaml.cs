using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Drawing;
using Avalonia.Media;
using System;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class DrawingSettingsDialog : Window
{
    private static readonly string[] FallbackHiddenTags = [DrawingParameterTags.Common, DrawingParameterTags.Geometry];

    private IChartObject? _drawing;
    private IDrawingSettingsPanelDefinition? _panelDefinition;
    private readonly Action<IChartObject>? _onApply;
    private readonly StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? _coordinator;
    private readonly System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData>? _candles;
    private StockAnalyzer.Core.Models.Drawing.DrawingEditToken? _activeToken;
    private bool _isBusyOrUnavailable;
    private DrawingObjectSettingsViewModel? _viewModel;

    public DrawingSettingsDialog() : this(null!, null, null, null) { }

    public DrawingSettingsDialog(IChartObject drawing) : this(drawing, null, null, null) { }

    public DrawingSettingsDialog(IChartObject drawing, IDrawingSettingsPanelRegistry? registry, Action<IChartObject>? onApply = null)
        : this(drawing, registry, onApply, null) { }

    public DrawingSettingsDialog(
        IChartObject drawing,
        IDrawingSettingsPanelRegistry? registry,
        Action<IChartObject>? onApply,
        StockAnalyzer.Avalonia.Services.Drawing.IDrawingEditCoordinator? coordinator,
        System.Collections.Generic.IReadOnlyList<StockAnalyzer.Core.Models.CoreCandleData>? candles = null)
    {
        InitializeComponent();
        _drawing = drawing;
        _onApply = onApply;
        _coordinator = coordinator;
        _candles = candles;

        if (_coordinator != null)
        {
            var begin = _coordinator.BeginEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.SettingsApply);
            if (begin.IsSuccess)
            {
                _activeToken = begin.Token;
            }
            else
            {
                _isBusyOrUnavailable = true;
            }
        }

        string displayName = "Drawing Settings";
        if (_drawing != null)
        {
            _viewModel = new DrawingObjectSettingsViewModel(_drawing, _onApply);
            DataContext = _viewModel;
            displayName = DrawingObjectDisplayNameHelper.GetDisplayName(_drawing);
        }

        Title = displayName;
        var headerTitleText = this.FindControl<TextBlock>("HeaderTitleText");
        if (headerTitleText != null)
        {
            headerTitleText.Text = displayName;
        }

        // Tool-specific window sizing (e.g. LineText/CurveLineText's wider 3-column layout) is
        // resolved via the registered IDrawingSettingsPanelDefinition instead of a hardcoded type
        // check here, so new tools can opt into a custom dialog size without editing this file.
        _panelDefinition = _drawing != null ? registry?.Resolve(_drawing) : null;
        if (_panelDefinition?.WindowHint is { } hint)
        {
            if (hint.Width.HasValue) Width = hint.Width.Value;
            if (hint.MinWidth.HasValue) MinWidth = hint.MinWidth.Value;
            CanResize = hint.CanResize;
        }

        if (_drawing != null)
        {
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
        }

        var settingsContentPanel = this.FindControl<StackPanel>("SettingsContentPanel");
        var dynamicSettingsView = this.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");

        if (_panelDefinition != null)
        {
            if (dynamicSettingsView != null && _panelDefinition.UsesDynamicSettings && _drawing != null)
            {
                dynamicSettingsView.HiddenParameterTags = _panelDefinition.DynamicHiddenParameterTags;
                dynamicSettingsView.ParameterObject = _drawing;
                dynamicSettingsView.IsVisible = true;
            }
            _panelDefinition.Activate(this);
            if (dynamicSettingsView != null && !_panelDefinition.UsesDynamicSettings)
            {
                dynamicSettingsView.IsVisible = false;
            }
        }
        else
        {
            if (dynamicSettingsView != null && _drawing != null)
            {
                dynamicSettingsView.HiddenParameterTags = FallbackHiddenTags;
                dynamicSettingsView.ParameterObject = _drawing;
                dynamicSettingsView.IsVisible = true;
            }

            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel");
            if (genericColorPanel != null) genericColorPanel.IsVisible = true;

            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        }

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
                            this.FindControl<ColorPicker>("PearsonUnmatchedColorPicker"),
                            this.FindControl<ColorPicker>("FrechetUnmatchedColorPicker"),
                            this.FindControl<ColorPicker>("HarmonicFillColorPicker"),
                            this.FindControl<ColorPicker>("AutoElliottFillColorPicker"),
                            this.FindControl<ColorPicker>("DtwFillColorPicker"),
                            this.FindControl<ColorPicker>("KalmanFillColorPicker"),
                            this.FindControl<ColorPicker>("ArimaFillColorPicker"),
                            this.FindControl<ColorPicker>("FftFillColorPicker"),
                            this.FindControl<ColorPicker>("HmmFillColorPicker"),
                            this.FindControl<ColorPicker>("PearsonFillColorPicker"),
                            this.FindControl<ColorPicker>("FrechetFillColorPicker"),
                            this.FindControl<ColorPicker>("SsaFillColorPicker"),
                            this.FindControl<ColorPicker>("AutoTimeCycleFillColorPicker"),
                            this.FindControl<ColorPicker>("NurbsConicFillColorPicker"),
                            this.FindControl<ColorPicker>("ValueAreaColorPicker"),
                            this.FindControl<ColorPicker>("ProfileColorPicker"),
                            this.FindControl<ColorPicker>("VpFillColorPicker"),
                            this.FindControl<ColorPicker>("BarUpColorPicker"),
                            this.FindControl<ColorPicker>("BarDownColorPicker"),
                            this.FindControl<ColorPicker>("LsTargetColorPicker"),
                            this.FindControl<ColorPicker>("LsStopColorPicker"),
                            this.FindControl<ColorPicker>("TextBackgroundColorPicker"),
                            this.FindControl<ColorPicker>("EllipseControlPointColorPicker"),
                            this.FindControl<ColorPicker>("FftPeakColorPicker"),
                            this.FindControl<ColorPicker>("SsaMultiTrendColorPicker"),
                            this.FindControl<ColorPicker>("SsaMultiCycleColorPicker"),
                            this.FindControl<ColorPicker>("SsaMultiCompositeColorPicker"),
                            this.FindControl<ColorPicker>("SsaMultiNoiseColorPicker"),
                            this.FindControl<ColorPicker>("SsaSnrResistanceColorPicker"),
                            this.FindControl<ColorPicker>("SsaSnrSupportColorPicker"),
                            this.FindControl<ColorPicker>("SsaSnrCenterColorPicker"),
                            this.FindControl<ColorPicker>("SsaPivotsResistanceColorPicker"),
                            this.FindControl<ColorPicker>("SsaPivotsSupportColorPicker"),
                            this.FindControl<ColorPicker>("SsaPivotsCenterColorPicker"),
                            this.FindControl<ColorPicker>("SsaEnvelopesResistanceColorPicker"),
                            this.FindControl<ColorPicker>("SsaEnvelopesSupportColorPicker"),
                            this.FindControl<ColorPicker>("SsaEnvelopesCenterColorPicker"),
                            this.FindControl<ColorPicker>("SsaTargetsResistanceColorPicker"),
                            this.FindControl<ColorPicker>("SsaTargetsSupportColorPicker"),
                            this.FindControl<ColorPicker>("SsaTargetsCenterColorPicker"),
                            this.FindControl<ColorPicker>("SsaAnomalyBullishColorPicker"),
                            this.FindControl<ColorPicker>("SsaAnomalyBearishColorPicker"),
                            this.FindControl<ColorPicker>("SsaAnomalyStructuralColorPicker"),
                            this.FindControl<ColorPicker>("HoughAutoLinesTrendColorPicker"),
                            this.FindControl<ColorPicker>("HoughAutoLinesSupportColorPicker"),
                            this.FindControl<ColorPicker>("HoughAutoLinesResistanceColorPicker"),
                            this.FindControl<ColorPicker>("HoughKeyLevelsSupportColorPicker"),
                            this.FindControl<ColorPicker>("HoughKeyLevelsResistanceColorPicker"),
                            this.FindControl<ColorPicker>("InformationFillColorPicker"),
                            this.FindControl<ColorPicker>("InformationFontColorPicker"),
                            this.FindControl<ColorPicker>("IconFontColorPicker")
                        };
                        foreach (var picker in pickers)
                        {
                            if (picker != null) picker.Width = w;
                        }
                    }
                }
            };
        }

        if (_drawing != null)
        {
            _panelDefinition?.Populate(this, _drawing);
        }
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

        if (_panelDefinition != null)
        {
            _panelDefinition.Commit(this, _drawing);
        }

        // F10 fix: FixedRangeVolumeProfileObject's RangeBars setter only records a pending bar
        // count; the Points move (and profile recompute) is deferred to Recalculate(). Previously
        // that recalculation ran only after the coordinator had already Commit()-ed (which snapshots
        // the object's CURRENT field values for history/session persistence), so the persisted
        // state carried the new RangeBars paired with the still-stale Points. Recalculating here --
        // before TakeSnapshot()/Commit() -- guarantees RangeBars and Points are captured together in
        // a consistent state. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F10.
        if (_drawing is FixedRangeVolumeProfileObject && _candles != null)
        {
            StockAnalyzer.Avalonia.Drawing.DeferredComputationRecalculator.TryRecalculate(_drawing, _candles);
        }

        _viewModel?.TakeSnapshot();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_isBusyOrUnavailable)
        {
            Close((DrawingSettingsResult?)DrawingSettingsResult.None);
            return;
        }

        if (_drawing == null)
        {
             if (_coordinator != null && _activeToken.HasValue)
             {
                 _coordinator.Cancel(_activeToken.Value);
                 _activeToken = null;
             }
             Close((DrawingSettingsResult?)DrawingSettingsResult.None);
             return;
        }

        try
        {
            ApplyCurrentSettingsToModel();
            if (_coordinator != null && _activeToken.HasValue)
            {
                var commitResult = _coordinator.Commit(_activeToken.Value);
                _activeToken = null;
                if (!commitResult.IsSuccess && commitResult.Status != DrawingCommandStatus.NoChange)
                {
                    _viewModel?.Rollback();
                    Close((DrawingSettingsResult?)DrawingSettingsResult.None);
                    return;
                }
            }
            Close((DrawingSettingsResult?)DrawingSettingsResult.Changed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DrawingSettingsDialog commit error: {ex.Message}");
            _viewModel?.Rollback();
            Close((DrawingSettingsResult?)DrawingSettingsResult.None);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.Rollback();
        if (_coordinator != null && _activeToken.HasValue)
        {
            _coordinator.Cancel(_activeToken.Value);
            _activeToken = null;
        }
        Close((DrawingSettingsResult?)DrawingSettingsResult.None);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_isBusyOrUnavailable)
        {
            Close((DrawingSettingsResult?)DrawingSettingsResult.None);
            return;
        }

        if (_coordinator != null && _activeToken.HasValue)
        {
            _coordinator.Cancel(_activeToken.Value);
            _activeToken = null;
        }
        Close((DrawingSettingsResult?)DrawingSettingsResult.Deleted);
    }

    /// <summary>
    /// Commits the current settings without closing the dialog, then notifies the caller (via
    /// <see cref="_onApply"/>) so it can persist and redraw -- the same effect OK has, minus the
    /// close, so the user can preview several changes on the live chart before deciding OK/Cancel.
    /// </summary>
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_drawing == null || _isBusyOrUnavailable) return;

        try
        {
            ApplyCurrentSettingsToModel();
            if (_coordinator != null && _activeToken.HasValue)
            {
                var commitResult = _coordinator.Commit(_activeToken.Value);
                if (!commitResult.IsSuccess && commitResult.Status != DrawingCommandStatus.NoChange)
                {
                    _viewModel?.Rollback();
                    var retryBegin = _coordinator.BeginEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.SettingsApply);
                    _activeToken = retryBegin.IsSuccess ? retryBegin.Token : null;
                    if (!_activeToken.HasValue)
                    {
                        _isBusyOrUnavailable = true;
                    }
                    return;
                }
                var begin = _coordinator.BeginEdit(StockAnalyzer.Core.Models.Drawing.DrawingOperationKind.SettingsApply);
                _activeToken = begin.IsSuccess ? begin.Token : null;
                if (!_activeToken.HasValue)
                {
                    _isBusyOrUnavailable = true;
                }
            }
            _onApply?.Invoke(_drawing);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DrawingSettingsDialog apply error: {ex.Message}");
            _viewModel?.Rollback();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_coordinator != null && _activeToken.HasValue)
        {
            _coordinator.Cancel(_activeToken.Value);
            _activeToken = null;
        }
        base.OnClosed(e);
    }
}
