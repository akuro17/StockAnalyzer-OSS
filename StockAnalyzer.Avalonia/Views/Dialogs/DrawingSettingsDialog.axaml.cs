using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using Avalonia.Media;
using System;

using System.Linq;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class DrawingSettingsDialog : Window
{
    private IChartObject? _drawing;

    public DrawingSettingsDialog() { }

    public DrawingSettingsDialog(IChartObject drawing)
    {
        InitializeComponent();
        _drawing = drawing;
        
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

        // Programmatic width synchronization to avoid compiled XAML binding errors
        if (thicknessSpin != null)
        {
            thicknessSpin.PropertyChanged += (s, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    double w = thicknessSpin.Bounds.Width;
                    if (w > 0)
                    {
                        var pickers = new[]
                        {
                            this.FindControl<ColorPicker>("ColorPickerControl"),
                            this.FindControl<ColorPicker>("ValueAreaColorPicker"),
                            this.FindControl<ColorPicker>("BarUpColorPicker"),
                            this.FindControl<ColorPicker>("BarDownColorPicker"),
                            this.FindControl<ColorPicker>("LsTargetColorPicker"),
                            this.FindControl<ColorPicker>("LsStopColorPicker")
                        };
                        foreach (var picker in pickers)
                        {
                            if (picker != null) picker.Width = w;
                        }
                    }
                }
            };
        }

        if (_drawing is StockAnalyzer.Avalonia.Drawing.PolylineObject poly)
        {
            var labelTypeCombo = this.FindControl<ComboBox>("LabelTypeCombo");
            var showLabelsCheck = this.FindControl<CheckBox>("ShowLabelsCheck");
            var fontSizeSpin = this.FindControl<NumericUpDown>("FontSizeSpin");
            var elliottPanel = this.FindControl<StackPanel>("ElliottPanel");

            if (elliottPanel != null) elliottPanel.IsVisible = true;
            if (labelTypeCombo != null) labelTypeCombo.SelectedIndex = (int)poly.LabelType;
            if (showLabelsCheck != null) showLabelsCheck.IsChecked = poly.ShowLabels;
            if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)poly.FontSize;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.HorizontalLineObject hLine)
        {
            var hPanel = this.FindControl<StackPanel>("HorizontalLinePanel");
            var priceSpin = this.FindControl<NumericUpDown>("PriceSpin");
            
            if (hPanel != null) hPanel.IsVisible = true;
            if (priceSpin != null && hLine.Points.Count > 0)
            {
                 priceSpin.Value = hLine.Points[0].Price;
            }
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.TextObject textObj)
        {
            var tPanel = this.FindControl<StackPanel>("TextPanel");
            var contentBox = this.FindControl<TextBox>("TextContentBox");
            var fontSizeSpin = this.FindControl<NumericUpDown>("TextFontSizeSpin");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            
            if (tPanel != null) tPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = false;

            if (contentBox != null) contentBox.Text = textObj.Text;
            if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)textObj.FontSize;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.PriceLabelObject priceObj)
        {
             var pPanel = this.FindControl<StackPanel>("PriceLabelPanel");
             var pfSpin = this.FindControl<NumericUpDown>("PriceFontSizeSpin");
             var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
             
             if (pPanel != null) pPanel.IsVisible = true;
             if (thicknessPanel != null) thicknessPanel.IsVisible = false;
             
             if (pfSpin != null) pfSpin.Value = (decimal)priceObj.FontSize;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.CalloutObject calloutObj)
        {
             var tPanel = this.FindControl<StackPanel>("TextPanel");
             var contentBox = this.FindControl<TextBox>("TextContentBox");
             var fontSizeSpin = this.FindControl<NumericUpDown>("TextFontSizeSpin");
             var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
             
             if (tPanel != null) tPanel.IsVisible = true;
             if (thicknessPanel != null) thicknessPanel.IsVisible = false;
             
             if (contentBox != null) contentBox.Text = calloutObj.Text;
             if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)calloutObj.FontSize;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.FixedRangeVolumeProfileObject frvp)
        {
             var vpPanel = this.FindControl<StackPanel>("VolumeProfilePanel");
             var opacitySpin = this.FindControl<NumericUpDown>("OpacitySpin");
             var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
             
             if (vpPanel != null) vpPanel.IsVisible = true;
             if (thicknessPanel != null) thicknessPanel.IsVisible = false;
             
             var valueAreaColorPicker = this.FindControl<ColorPicker>("ValueAreaColorPicker");
             if (valueAreaColorPicker != null)
             {
                 valueAreaColorPicker.Color = frvp.ValueAreaColor;
             }

             if (opacitySpin != null) opacitySpin.Value = (decimal)(frvp.Opacity * 100);
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.BarPatternObject barPattern)
        {
            var bpPanel = this.FindControl<StackPanel>("BarPatternPanel");
            var bpOpacitySpin = this.FindControl<NumericUpDown>("BarOpacitySpin");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel"); 
            
            if (bpPanel != null) bpPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = false;
            if (genericColorPanel != null) genericColorPanel.IsVisible = false;
            
            if (bpOpacitySpin != null) bpOpacitySpin.Value = barPattern.Transparency;
            
            var barUpColorPicker = this.FindControl<ColorPicker>("BarUpColorPicker");
            if (barUpColorPicker != null)
            {
                barUpColorPicker.Color = barPattern.UpColor;
            }
            var barDownColorPicker = this.FindControl<ColorPicker>("BarDownColorPicker");
            if (barDownColorPicker != null)
            {
                barDownColorPicker.Color = barPattern.DownColor;
            }
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.LongShortPositionObject lsObj)
        {
            var lsPanel = this.FindControl<StackPanel>("LongShortPanel");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel"); 
            var opacitySpin = this.FindControl<NumericUpDown>("LsOpacitySpin");

            var entrySpin = this.FindControl<NumericUpDown>("LsEntryPriceSpin");
            var targetSpin = this.FindControl<NumericUpDown>("LsTargetPriceSpin");
            var stopSpin = this.FindControl<NumericUpDown>("LsStopPriceSpin");

            if (lsPanel != null) lsPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = false;
            if (genericColorPanel != null) genericColorPanel.IsVisible = false;

            if (lsObj.Points.Count >= 3)
            {
                if (entrySpin != null) entrySpin.Value = lsObj.Points[0].Price;
                if (stopSpin != null) stopSpin.Value = lsObj.Points[1].Price;
                if (targetSpin != null) targetSpin.Value = lsObj.Points[2].Price;
            }

            if (opacitySpin != null) opacitySpin.Value = (decimal)(lsObj.AreaOpacity * 100);

            var lsTargetColorPicker = this.FindControl<ColorPicker>("LsTargetColorPicker");
            if (lsTargetColorPicker != null)
            {
                lsTargetColorPicker.Color = lsObj.TargetColor;
            }
            var lsStopColorPicker = this.FindControl<ColorPicker>("LsStopColorPicker");
            if (lsStopColorPicker != null)
            {
                lsStopColorPicker.Color = lsObj.StopColor;
            }
        }
        else if (_drawing is GeometricPatternObject geomPattern)
        {
            var patternPanel = this.FindControl<StackPanel>("GeometricPatternPanel");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel"); 
            
            if (patternPanel != null) patternPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = false;
            if (genericColorPanel != null) genericColorPanel.IsVisible = false;
            
            var showChannelsCheck = this.FindControl<CheckBox>("ShowChannelsCheck");
            var showWedgesCheck = this.FindControl<CheckBox>("ShowWedgesCheck");
            var showTrianglesCheck = this.FindControl<CheckBox>("ShowTrianglesCheck");
            var showPennantsCheck = this.FindControl<CheckBox>("ShowPennantsCheck");
            var showMegaphoneCheck = this.FindControl<CheckBox>("ShowMegaphoneCheck");
            var autoThresholdCheck = this.FindControl<CheckBox>("AutoZigZagCheck");
            var zigzagThresholdSpin = this.FindControl<NumericUpDown>("ZigZagThresholdSpin");

            if (showChannelsCheck != null) showChannelsCheck.IsChecked = geomPattern.ShowChannels;
            if (showWedgesCheck != null) showWedgesCheck.IsChecked = geomPattern.ShowWedges;
            if (showTrianglesCheck != null) showTrianglesCheck.IsChecked = geomPattern.ShowTriangles;
            if (showPennantsCheck != null) showPennantsCheck.IsChecked = geomPattern.ShowPennantsAndFlags;
            if (showMegaphoneCheck != null) showMegaphoneCheck.IsChecked = geomPattern.ShowMegaphone;
            
            if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !geomPattern.ZigZagThreshold.HasValue;
            if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = geomPattern.ZigZagThreshold ?? 2.0m;
        }
        else if (_drawing is HarmonicPatternObject harmonicPattern)
        {
            var patternPanel = this.FindControl<StackPanel>("HarmonicPatternPanel");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel"); 
            
            if (patternPanel != null) patternPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = false;
            if (genericColorPanel != null) genericColorPanel.IsVisible = false;
            
            var autoThresholdCheck = this.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
            var zigzagThresholdSpin = this.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
            var showPrzCheck = this.FindControl<CheckBox>("HarmonicShowPrzCheck");

            if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !harmonicPattern.ZigZagThreshold.HasValue;
            if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = harmonicPattern.ZigZagThreshold ?? StockAnalyzer.Core.ChartConstants.DefaultHarmonicZigZagThreshold;
            if (showPrzCheck != null) showPrzCheck.IsChecked = harmonicPattern.ShowPrz;
        }
        else if (_drawing is AutoElliottWaveObject autoElliottPattern)
        {
            var patternPanel = this.FindControl<StackPanel>("AutoElliottPanel");
            
            if (patternPanel != null) patternPanel.IsVisible = true;
            
            var autoThresholdCheck = this.FindControl<CheckBox>("AutoElliottAutoZigZagCheck");
            var zigzagThresholdSpin = this.FindControl<NumericUpDown>("AutoElliottZigZagThresholdSpin");

            if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !autoElliottPattern.ZigZagThreshold.HasValue;
            if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = autoElliottPattern.ZigZagThreshold ?? 2.0m;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwObj)
        {
            var dtwPanel = this.FindControl<StackPanel>("DtwProjectionPanel");
            var handleSizeSpin = this.FindControl<NumericUpDown>("DtwHandleSizeSpin");
            
            var genericColorPanel = this.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = this.FindControl<StackPanel>("ThicknessPanel");
            if (genericColorPanel != null) genericColorPanel.IsVisible = true;
            if (thicknessPanel != null) thicknessPanel.IsVisible = true;
            
            if (dtwPanel != null) dtwPanel.IsVisible = true;
            if (handleSizeSpin != null) handleSizeSpin.Value = (decimal)dtwObj.HandleSize;
        }
        else if (_drawing is StockAnalyzer.Avalonia.Drawing.TrendLineObject trend)
        {
            var trendPanel = this.FindControl<StackPanel>("TrendLinePanel");
            var showProjCheck = this.FindControl<CheckBox>("ShowProjectionCheck");
            var projColsSpin = this.FindControl<NumericUpDown>("ProjectionColumnsSpin");

            if (trendPanel != null) trendPanel.IsVisible = true;
            if (showProjCheck != null) showProjCheck.IsChecked = trend.ShowProjection;
            if (projColsSpin != null) projColsSpin.Value = trend.ProjectionColumns;
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

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_drawing == null)
        {
             Close((DrawingSettingsResult?)DrawingSettingsResult.None);
             return;
        }

        try
        {
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

            // Elliott Wave Settings
            if (_drawing is StockAnalyzer.Avalonia.Drawing.PolylineObject poly)
            {
                var labelTypeCombo = this.FindControl<ComboBox>("LabelTypeCombo");
                var showLabelsCheck = this.FindControl<CheckBox>("ShowLabelsCheck");
                var fontSizeSpin = this.FindControl<NumericUpDown>("FontSizeSpin");

                if (labelTypeCombo != null)
                {
                    poly.LabelType = (StockAnalyzer.Avalonia.Drawing.PolylineLabelType)labelTypeCombo.SelectedIndex;
                }
                if (showLabelsCheck?.IsChecked != null)
                {
                    poly.ShowLabels = showLabelsCheck.IsChecked.Value;
                }
                if (fontSizeSpin?.Value != null)
                {
                    poly.FontSize = (double)fontSizeSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.HorizontalLineObject hLine)
            {
                var priceSpin = this.FindControl<NumericUpDown>("PriceSpin");
                if (priceSpin?.Value != null && hLine.Points.Count > 0)
                {
                     hLine.Points[0] = new StockAnalyzer.Avalonia.Drawing.ChartPoint(hLine.Points[0].Time, (decimal)priceSpin.Value);
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.TextObject textObj)
            {
                var contentBox = this.FindControl<TextBox>("TextContentBox");
                var fontSizeSpin = this.FindControl<NumericUpDown>("TextFontSizeSpin");
                
                if (contentBox != null)
                {
                    textObj.Text = contentBox.Text ?? "";
                }
                if (fontSizeSpin?.Value != null)
                {
                    textObj.FontSize = (double)fontSizeSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.PriceLabelObject priceObj)
            {
                var pfSpin = this.FindControl<NumericUpDown>("PriceFontSizeSpin");
                if (pfSpin?.Value != null)
                {
                    priceObj.FontSize = (double)pfSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.CalloutObject calloutObj)
            {
                var contentBox = this.FindControl<TextBox>("TextContentBox");
                var fontSizeSpin = this.FindControl<NumericUpDown>("TextFontSizeSpin");
                
                if (contentBox != null)
                {
                    calloutObj.Text = contentBox.Text ?? "";
                }
                if (fontSizeSpin?.Value != null)
                {
                    calloutObj.FontSize = (double)fontSizeSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.FixedRangeVolumeProfileObject frvp)
            {
                var valueAreaColorPicker = this.FindControl<ColorPicker>("ValueAreaColorPicker");
                if (valueAreaColorPicker != null)
                {
                    frvp.ValueAreaColor = valueAreaColorPicker.Color;
                }

                var opacitySpin = this.FindControl<NumericUpDown>("OpacitySpin");
                if (opacitySpin?.Value != null)
                {
                    frvp.Opacity = (double)opacitySpin.Value / 100.0;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.BarPatternObject barPattern)
            {
                var barUpColorPicker = this.FindControl<ColorPicker>("BarUpColorPicker");
                if (barUpColorPicker != null)
                {
                    barPattern.UpColor = barUpColorPicker.Color;
                }
                var barDownColorPicker = this.FindControl<ColorPicker>("BarDownColorPicker");
                if (barDownColorPicker != null)
                {
                    barPattern.DownColor = barDownColorPicker.Color;
                }

                var bpOpacitySpin = this.FindControl<NumericUpDown>("BarOpacitySpin");
                if (bpOpacitySpin?.Value != null)
                {
                    barPattern.Transparency = (int)bpOpacitySpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.LongShortPositionObject lsObj)
            {
                var entrySpin = this.FindControl<NumericUpDown>("LsEntryPriceSpin");
                var targetSpin = this.FindControl<NumericUpDown>("LsTargetPriceSpin");
                var stopSpin = this.FindControl<NumericUpDown>("LsStopPriceSpin");
                var opacitySpin = this.FindControl<NumericUpDown>("LsOpacitySpin");

                if (lsObj.Points.Count >= 3)
                {
                    decimal entry = entrySpin?.Value ?? lsObj.Points[0].Price;
                    decimal stop = stopSpin?.Value ?? lsObj.Points[1].Price;
                    decimal target = targetSpin?.Value ?? lsObj.Points[2].Price;

                    // Apply constraints
                    if (lsObj.IsLong)
                    {
                        if (stop >= entry) stop = entry - 0.0001m;
                        if (target <= entry) target = entry + 0.0001m;
                    }
                    else
                    {
                        if (stop <= entry) stop = entry + 0.0001m;
                        if (target >= entry) target = entry - 0.0001m;
                    }

                    lsObj.Points[0] = new StockAnalyzer.Avalonia.Drawing.ChartPoint(lsObj.Points[0].Time, entry);
                    lsObj.Points[1] = new StockAnalyzer.Avalonia.Drawing.ChartPoint(lsObj.Points[1].Time, stop);
                    lsObj.Points[2] = new StockAnalyzer.Avalonia.Drawing.ChartPoint(lsObj.Points[2].Time, target);
                }

                var lsTargetColorPicker = this.FindControl<ColorPicker>("LsTargetColorPicker");
                if (lsTargetColorPicker != null)
                {
                    lsObj.TargetColor = lsTargetColorPicker.Color;
                }
                var lsStopColorPicker = this.FindControl<ColorPicker>("LsStopColorPicker");
                if (lsStopColorPicker != null)
                {
                    lsObj.StopColor = lsStopColorPicker.Color;
                }

                if (opacitySpin?.Value != null)
                {
                    lsObj.AreaOpacity = (double)opacitySpin.Value / 100.0;
                }
            }
            else if (_drawing is GeometricPatternObject geomPattern)
            {
                var showChannelsCheck = this.FindControl<CheckBox>("ShowChannelsCheck");
                var showWedgesCheck = this.FindControl<CheckBox>("ShowWedgesCheck");
                var showTrianglesCheck = this.FindControl<CheckBox>("ShowTrianglesCheck");
                var showPennantsCheck = this.FindControl<CheckBox>("ShowPennantsCheck");
                var showMegaphoneCheck = this.FindControl<CheckBox>("ShowMegaphoneCheck");
                var autoThresholdCheck = this.FindControl<CheckBox>("AutoZigZagCheck");
                var zigzagThresholdSpin = this.FindControl<NumericUpDown>("ZigZagThresholdSpin");

                if (showChannelsCheck?.IsChecked != null) geomPattern.ShowChannels = showChannelsCheck.IsChecked.Value;
                if (showWedgesCheck?.IsChecked != null) geomPattern.ShowWedges = showWedgesCheck.IsChecked.Value;
                if (showTrianglesCheck?.IsChecked != null) geomPattern.ShowTriangles = showTrianglesCheck.IsChecked.Value;
                if (showPennantsCheck?.IsChecked != null) geomPattern.ShowPennantsAndFlags = showPennantsCheck.IsChecked.Value;
                if (showMegaphoneCheck?.IsChecked != null) geomPattern.ShowMegaphone = showMegaphoneCheck.IsChecked.Value;
                
                if (autoThresholdCheck?.IsChecked == true)
                {
                    geomPattern.ZigZagThreshold = null;
                }
                else if (zigzagThresholdSpin?.Value != null)
                {
                    geomPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
                }
            }
            else if (_drawing is HarmonicPatternObject harmonicPattern)
            {
                var autoThresholdCheck = this.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
                var zigzagThresholdSpin = this.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
                var showPrzCheck = this.FindControl<CheckBox>("HarmonicShowPrzCheck");

                if (autoThresholdCheck?.IsChecked == true)
                {
                    harmonicPattern.ZigZagThreshold = null;
                }
                else if (zigzagThresholdSpin?.Value != null)
                {
                    harmonicPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
                }

                if (showPrzCheck?.IsChecked != null)
                {
                    harmonicPattern.ShowPrz = showPrzCheck.IsChecked.Value;
                }
            }
            else if (_drawing is AutoElliottWaveObject autoElliottPattern)
            {
                var autoThresholdCheck = this.FindControl<CheckBox>("AutoElliottAutoZigZagCheck");
                var zigzagThresholdSpin = this.FindControl<NumericUpDown>("AutoElliottZigZagThresholdSpin");

                if (autoThresholdCheck?.IsChecked == true)
                {
                    autoElliottPattern.ZigZagThreshold = null;
                }
                else if (zigzagThresholdSpin?.Value != null)
                {
                    autoElliottPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject dtwObj)
            {
                var handleSizeSpin = this.FindControl<NumericUpDown>("DtwHandleSizeSpin");
                if (handleSizeSpin?.Value != null)
                {
                    dtwObj.HandleSize = (double)handleSizeSpin.Value;
                }
            }
            else if (_drawing is StockAnalyzer.Avalonia.Drawing.TrendLineObject trend)
            {
                var showProjCheck = this.FindControl<CheckBox>("ShowProjectionCheck");
                var projColsSpin = this.FindControl<NumericUpDown>("ProjectionColumnsSpin");

                if (showProjCheck?.IsChecked != null)
                {
                    trend.ShowProjection = showProjCheck.IsChecked.Value;
                }
                if (projColsSpin?.Value != null)
                {
                    trend.ProjectionColumns = (int)projColsSpin.Value;
                }
            }
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
}
