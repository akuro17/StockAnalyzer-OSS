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
    private string _toolDisplayName = "Drawing Settings";

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
        var sidebarTitleText = this.FindControl<TextBlock>("SidebarTitleText");
        if (sidebarTitleText != null)
        {
            sidebarTitleText.Text = displayName;
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

            // Initialize Stroke Style
            var strokeProp = _drawing.GetType().GetProperty("StrokeStyle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var strokeStylePanel = this.FindControl<StackPanel>("StrokeStylePanel");
            var strokeStyleCombo = this.FindControl<ComboBox>("StrokeStyleCombo");
            if (strokeStylePanel != null)
            {
                strokeStylePanel.IsVisible = strokeProp != null;
            }
            if (strokeProp != null && strokeStyleCombo != null)
            {
                var currentStyle = (DrawingStrokeStyle)strokeProp.GetValue(_drawing)!;
                strokeStyleCombo.SelectedIndex = currentStyle switch
                {
                    DrawingStrokeStyle.Solid => 0,
                    DrawingStrokeStyle.Dash => 1,
                    DrawingStrokeStyle.Dot => 2,
                    _ => 0
                };
            }

            // Initialize Opacity / Alpha
            var opacityPanel = this.FindControl<StackPanel>("GenericOpacityPanel");
            var opacitySpin = this.FindControl<NumericUpDown>("GenericOpacitySpin");
            var alphaProp = _drawing.GetType().GetProperty("Alpha", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            bool supportsOpacity = alphaProp != null || _drawing is RelativeGeometricRenderer;
            if (opacityPanel != null)
            {
                opacityPanel.IsVisible = supportsOpacity;
            }
            if (opacitySpin != null && supportsOpacity)
            {
                if (alphaProp != null)
                {
                    var rawAlpha = Convert.ToDouble(alphaProp.GetValue(_drawing));
                    opacitySpin.Value = (decimal)Math.Clamp(Math.Round(rawAlpha / 2.55), 1.0, 100.0);
                }
                else
                {
                    opacitySpin.Value = (decimal)Math.Clamp(Math.Round((double)_drawing.Color.A / 2.55), 1.0, 100.0);
                }
            }
        }

        var settingsContentPanel = this.FindControl<StackPanel>("SettingsContentPanel");
        var dynamicSettingsView = this.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");
        var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");

        if (_panelDefinition != null)
        {
            if (genericColorPanel != null) genericColorPanel.IsVisible = _panelDefinition.SupportsStrokeColor;
            if (thicknessPanel != null) thicknessPanel.IsVisible = _panelDefinition.SupportsStrokeThickness;

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

            if (genericColorPanel != null) genericColorPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        }

        _toolDisplayName = displayName;

        if (_drawing != null)
        {
            _panelDefinition?.Populate(this, _drawing);
        }

        UpdateGeneralSettingsVisibility();
        UpdateSpecificSettingsVisibility();

        var categoryListBox = this.FindControl<ListBox>("CategoryListBox");
        if (categoryListBox != null)
        {
            categoryListBox.SelectionChanged += OnCategorySelectionChanged;
            UpdateCategoryView(categoryListBox.SelectedIndex >= 0 ? categoryListBox.SelectedIndex : 0);
        }
        else
        {
            UpdateCategoryView(0);
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

    private void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var listBox = sender as ListBox;
        if (listBox == null) return;
        UpdateCategoryView(listBox.SelectedIndex >= 0 ? listBox.SelectedIndex : 0);
    }

    private void UpdateCategoryView(int selectedIndex)
    {
        var generalSection = this.FindControl<StackPanel>("GeneralSettingsSection");
        var specificSection = this.FindControl<StackPanel>("SpecificSettingsSection");
        var scrollViewer = this.FindControl<ScrollViewer>("SettingsScrollViewer");
        var headerTitleText = this.FindControl<TextBlock>("HeaderTitleText");

        if (generalSection == null || specificSection == null) return;

        bool isGeneral = (selectedIndex == 0);
        generalSection.IsVisible = isGeneral;
        specificSection.IsVisible = !isGeneral;

        if (headerTitleText != null)
        {
            headerTitleText.Text = _toolDisplayName;
        }

        scrollViewer?.ScrollToHome();
    }

    private void UpdateGeneralSettingsVisibility()
    {
        var noGeneralCard = this.FindControl<Border>("NoGeneralSettingsCard");
        if (noGeneralCard == null) return;

        var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
        var lineTextCommonPanel = this.FindControl<StackPanel>("LineTextCommonPanel");

        bool hasGeneral = (genericColorPanel?.IsVisible == true) ||
                          (thicknessPanel?.IsVisible == true) ||
                          (lineTextCommonPanel?.IsVisible == true);

        noGeneralCard.IsVisible = !hasGeneral;
    }

    private void UpdateSpecificSettingsVisibility()
    {
        var noSpecificCard = this.FindControl<Border>("NoSpecificSettingsCard");
        if (noSpecificCard == null) return;

        var specificSection = this.FindControl<StackPanel>("SpecificSettingsSection");
        var dynamicSettingsView = this.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");

        bool hasSpecific = false;
        if (dynamicSettingsView?.IsVisible == true)
        {
            hasSpecific = true;
        }
        else if (specificSection != null)
        {
            foreach (var child in specificSection.Children)
            {
                if (child is Control c && c.IsVisible)
                {
                    string? name = c.Name;
                    if (!string.IsNullOrEmpty(name) &&
                        name != "NoSpecificSettingsCard" &&
                        name != "DynamicSettingsView")
                    {
                        hasSpecific = true;
                        break;
                    }
                }
            }
        }

        noSpecificCard.IsVisible = !hasSpecific;
    }

    /// <summary>
    /// Commits every UI control's current value onto <see cref="_drawing"/>. Shared by OK (commit
    /// then close) and Apply (commit without closing, so the change previews live on the chart).
    /// </summary>
    private void ApplyCurrentSettingsToModel()
    {
        if (_drawing == null) return;

        // Apply generic color & opacity
        var colorPicker = this.FindControl<ColorPicker>("ColorPickerControl");
        var opacitySpin = this.FindControl<NumericUpDown>("GenericOpacitySpin");
        byte? appliedAlpha = null;
        if (opacitySpin?.Value != null)
        {
            double pct = (double)opacitySpin.Value;
            appliedAlpha = (byte)Math.Clamp((int)Math.Round(pct * 2.55), 1, 255);

            var alphaProp = _drawing.GetType().GetProperty("Alpha", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (alphaProp != null && alphaProp.CanWrite)
            {
                if (alphaProp.PropertyType == typeof(byte))
                {
                    alphaProp.SetValue(_drawing, (byte)Math.Clamp((int)appliedAlpha.Value, 20, 200));
                }
                else if (alphaProp.PropertyType == typeof(int))
                {
                    alphaProp.SetValue(_drawing, (int)Math.Clamp((int)appliedAlpha.Value, 20, 200));
                }
            }
        }

        if (colorPicker != null && (_panelDefinition?.SupportsStrokeColor ?? true))
        {
            var col = colorPicker.Color;
            if (appliedAlpha.HasValue)
            {
                _drawing.Color = global::Avalonia.Media.Color.FromArgb(appliedAlpha.Value, col.R, col.G, col.B);
            }
            else
            {
                _drawing.Color = col;
            }
        }

        var thicknessSpin = this.FindControl<NumericUpDown>("ThicknessSpin");
        if (thicknessSpin?.Value != null && (_panelDefinition?.SupportsStrokeThickness ?? true))
        {
            _drawing.Thickness = (double)thicknessSpin.Value;
        }

        // Apply Stroke Style
        var strokeProp = _drawing.GetType().GetProperty("StrokeStyle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var strokeStyleCombo = this.FindControl<ComboBox>("StrokeStyleCombo");
        if (strokeProp != null && strokeStyleCombo != null && strokeStyleCombo.SelectedIndex >= 0)
        {
            DrawingStrokeStyle selectedStyle = strokeStyleCombo.SelectedIndex switch
            {
                0 => DrawingStrokeStyle.Solid,
                1 => DrawingStrokeStyle.Dash,
                2 => DrawingStrokeStyle.Dot,
                _ => DrawingStrokeStyle.Solid
            };
            strokeProp.SetValue(_drawing, selectedStyle);
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
        if ((_drawing is FixedRangeVolumeProfileObject || _drawing is TimeAtPriceObject) && _candles != null)
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

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        DrawingThemeContext.ResetDefaultColor();
        var defaultColor = DrawingThemeContext.DefaultColor;

        var colorPicker = this.FindControl<ColorPicker>("ColorPickerControl");
        if (colorPicker != null)
        {
            colorPicker.Color = defaultColor;
        }

        var thicknessSpin = this.FindControl<NumericUpDown>("ThicknessSpin");
        if (thicknessSpin != null)
        {
            thicknessSpin.Value = (decimal)DrawingThemeContext.DefaultStrokeThickness;
        }

        var strokeProp = _drawing?.GetType().GetProperty("StrokeStyle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var strokeStyleCombo = this.FindControl<ComboBox>("StrokeStyleCombo");
        if (strokeStyleCombo != null)
        {
            strokeStyleCombo.SelectedIndex = 0; // Solid
        }

        var opacitySpin = this.FindControl<NumericUpDown>("GenericOpacitySpin");
        var alphaProp = _drawing?.GetType().GetProperty("Alpha", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (_drawing != null)
        {
            _drawing.Color = defaultColor;
            _drawing.Thickness = DrawingThemeContext.DefaultStrokeThickness;
            if (strokeProp != null && strokeProp.CanWrite)
            {
                strokeProp.SetValue(_drawing, DrawingStrokeStyle.Solid);
            }

            if (alphaProp != null && alphaProp.CanWrite)
            {
                if (alphaProp.PropertyType == typeof(byte)) alphaProp.SetValue(_drawing, (byte)100);
                else if (alphaProp.PropertyType == typeof(int)) alphaProp.SetValue(_drawing, 100);

                if (opacitySpin != null)
                {
                    opacitySpin.Value = (decimal)Math.Clamp(Math.Round(100.0 / 2.55), 1.0, 100.0);
                }
            }
            else if (opacitySpin != null)
            {
                opacitySpin.Value = 100m;
            }

            _onApply?.Invoke(_drawing);
        }
        else if (opacitySpin != null)
        {
            opacitySpin.Value = 100m;
        }
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
