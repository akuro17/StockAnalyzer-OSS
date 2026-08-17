using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// Regression coverage for the narrow-ColorPicker fix request: ThicknessPanel (and thus
/// ThicknessSpin) is hidden for CalloutObject/TextObject, so the width-sync handler must not
/// depend on ThicknessSpin's Bounds to stretch ColorPickers to the dialog's full content width.
/// </summary>
public class DrawingSettingsDialogTests
{
    // Mirrors the production DI wiring in ServiceCollectionExtensions.AddCommonServices, so these
    // tests exercise the same registry-driven path production code uses for each migrated tool.
    private static IDrawingSettingsPanelRegistry CreateRegistry()
    {
        var registry = new DrawingSettingsPanelRegistry();
        registry.Register(new LineTextSettingsPanelDefinition());
        registry.Register(new TextSettingsPanelDefinition());
        registry.Register(new PriceLabelSettingsPanelDefinition());
        registry.Register(new HorizontalLineSettingsPanelDefinition());
        registry.Register(new TrendLineSettingsPanelDefinition());
        registry.Register(new NurbsTrendCurveSettingsPanelDefinition());
        registry.Register(new NurbsConicShapeSettingsPanelDefinition());
        registry.Register(new NurbsHyperbolaSettingsPanelDefinition());
        registry.Register(new FixedRangeVolumeProfileSettingsPanelDefinition());
        registry.Register(new BarPatternSettingsPanelDefinition());
        registry.Register(new GeometricPatternSettingsPanelDefinition());
        registry.Register(new HarmonicPatternSettingsPanelDefinition());
        registry.Register(new AutoElliottWaveSettingsPanelDefinition());
        registry.Register(new LongShortPositionSettingsPanelDefinition());
        registry.Register(new PolylineSettingsPanelDefinition());
        registry.Register(new RangeSplineSettingsPanelDefinition());
        registry.Register(new DtwProjectionSettingsPanelDefinition());
        registry.Register(new CatenaryCurveSettingsPanelDefinition());
        return registry;
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForCalloutObject_WithThicknessPanelHidden()
    {
        var callout = new CalloutObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now, 110m));

        var dialog = new DrawingSettingsDialog(callout, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var borderColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var backgroundColorPicker = dialog.FindControl<ColorPicker>("TextBackgroundColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(borderColorPicker);
            Assert.NotNull(backgroundColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.False(double.IsNaN(borderColorPicker!.Width));
            Assert.False(double.IsNaN(backgroundColorPicker!.Width));
            Assert.Equal(contentPanel.Bounds.Width, borderColorPicker.Width, 3);
            Assert.Equal(contentPanel.Bounds.Width, backgroundColorPicker.Width, 3);
        }
        finally
        {
            dialog.Close();
            callout.Dispose();
        }
    }

    [AvaloniaFact]
    public void LineTextPanel_BindsTopAndBottomGroupsIndependently_ForLineTextObject()
    {
        var lineText = new LineTextObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            TopText = "Resistance",
            TopFontSize = 18,
            TopAlignment = TextHorizontalAlignment.Left,
            TopOffsetPx = 15,
            TopTextColor = Colors.Red,
            TopRotationMode = TextRotationMode.FollowLine,
            TopTextReverseOrder = true,
            TopTextOrientationOverride = TextManualOrientation.Rotate180,
            TopPositionFixed = true,
            TopPositionLocked = true,
            TopExtendBeyondLine = true,
            BottomText = "-2.5%",
            BottomFontSize = 9,
            BottomAlignment = TextHorizontalAlignment.Right,
            BottomOffsetPx = 4,
            BottomTextColor = Colors.Blue,
            BottomRotationMode = TextRotationMode.AlwaysUpright,
            BottomTextReverseOrder = false,
            BottomTextOrientationOverride = TextManualOrientation.Mirror,
            BottomPositionFixed = false,
            BottomPositionLocked = false,
            BottomExtendBeyondLine = false,
            MatchBackgroundColor = true
        };

        var dialog = new DrawingSettingsDialog(lineText, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = dialog.FindControl<StackPanel>("LineTextPanel");
            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);

            var matchBgCheck = dialog.FindControl<CheckBox>("LineTextMatchBackgroundCheck");
            var topTextBox = dialog.FindControl<TextBox>("LineTextTopTextBox");
            var topFontSizeSpin = dialog.FindControl<NumericUpDown>("LineTextTopFontSizeSpin");
            var topAlignmentCombo = dialog.FindControl<ComboBox>("LineTextTopAlignmentCombo");
            var topOffsetSpin = dialog.FindControl<NumericUpDown>("LineTextTopOffsetSpin");
            var topColorPicker = dialog.FindControl<ColorPicker>("LineTextTopColorPicker");
            var topRotationCombo = dialog.FindControl<ComboBox>("LineTextTopRotationCombo");
            var topReverseOrderCheck = dialog.FindControl<CheckBox>("LineTextTopReverseOrderCheck");
            var topOrientationCombo = dialog.FindControl<ComboBox>("LineTextTopOrientationCombo");
            var topPositionFixedCheck = dialog.FindControl<CheckBox>("LineTextTopPositionFixedCheck");
            var topPositionLockedCheck = dialog.FindControl<CheckBox>("LineTextTopPositionLockedCheck");
            var topExtendBeyondLineCheck = dialog.FindControl<CheckBox>("LineTextTopExtendBeyondLineCheck");
            var bottomTextBox = dialog.FindControl<TextBox>("LineTextBottomTextBox");
            var bottomFontSizeSpin = dialog.FindControl<NumericUpDown>("LineTextBottomFontSizeSpin");
            var bottomAlignmentCombo = dialog.FindControl<ComboBox>("LineTextBottomAlignmentCombo");
            var bottomOffsetSpin = dialog.FindControl<NumericUpDown>("LineTextBottomOffsetSpin");
            var bottomColorPicker = dialog.FindControl<ColorPicker>("LineTextBottomColorPicker");
            var bottomRotationCombo = dialog.FindControl<ComboBox>("LineTextBottomRotationCombo");
            var bottomReverseOrderCheck = dialog.FindControl<CheckBox>("LineTextBottomReverseOrderCheck");
            var bottomOrientationCombo = dialog.FindControl<ComboBox>("LineTextBottomOrientationCombo");
            var bottomPositionFixedCheck = dialog.FindControl<CheckBox>("LineTextBottomPositionFixedCheck");
            var bottomPositionLockedCheck = dialog.FindControl<CheckBox>("LineTextBottomPositionLockedCheck");
            var bottomExtendBeyondLineCheck = dialog.FindControl<CheckBox>("LineTextBottomExtendBeyondLineCheck");

            Assert.True(matchBgCheck?.IsChecked);

            Assert.Equal("Resistance", topTextBox?.Text);
            Assert.Equal(18m, topFontSizeSpin?.Value);
            Assert.Equal((int)TextHorizontalAlignment.Left, topAlignmentCombo?.SelectedIndex);
            Assert.Equal(15m, topOffsetSpin?.Value);
            Assert.Equal(Colors.Red, topColorPicker?.Color);
            Assert.Equal((int)TextRotationMode.FollowLine, topRotationCombo?.SelectedIndex);
            Assert.True(topReverseOrderCheck?.IsChecked);
            Assert.Equal((int)TextManualOrientation.Rotate180, topOrientationCombo?.SelectedIndex);
            Assert.True(topPositionFixedCheck?.IsChecked);
            Assert.True(topPositionLockedCheck?.IsChecked);
            Assert.True(topExtendBeyondLineCheck?.IsChecked);

            Assert.Equal("-2.5%", bottomTextBox?.Text);
            Assert.Equal(9m, bottomFontSizeSpin?.Value);
            Assert.Equal((int)TextHorizontalAlignment.Right, bottomAlignmentCombo?.SelectedIndex);
            Assert.Equal(4m, bottomOffsetSpin?.Value);
            Assert.Equal(Colors.Blue, bottomColorPicker?.Color);
            Assert.Equal((int)TextRotationMode.AlwaysUpright, bottomRotationCombo?.SelectedIndex);
            Assert.False(bottomReverseOrderCheck?.IsChecked);
            Assert.Equal((int)TextManualOrientation.Mirror, bottomOrientationCombo?.SelectedIndex);
            Assert.False(bottomPositionFixedCheck?.IsChecked);
            Assert.False(bottomPositionLockedCheck?.IsChecked);
            Assert.False(bottomExtendBeyondLineCheck?.IsChecked);
        }
        finally
        {
            dialog.Close();
            lineText.Dispose();
        }
    }

    [AvaloniaFact]
    public void LineTextPanel_RotationCombos_OfferFollowLineAutoFlip_AsAThirdOption()
    {
        var lineText = new LineTextObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            TopRotationMode = TextRotationMode.FollowLineAutoFlip,
            BottomRotationMode = TextRotationMode.FollowLineAutoFlip
        };

        var dialog = new DrawingSettingsDialog(lineText, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var topRotationCombo = dialog.FindControl<ComboBox>("LineTextTopRotationCombo");
            var bottomRotationCombo = dialog.FindControl<ComboBox>("LineTextBottomRotationCombo");

            // The existing two options (Follow Line = 0, Always Upright = 1) must keep their index,
            // so saved shapes created before this option existed keep loading correctly.
            Assert.Equal(3, topRotationCombo?.Items.Count);
            Assert.Equal(3, bottomRotationCombo?.Items.Count);

            Assert.Equal((int)TextRotationMode.FollowLineAutoFlip, topRotationCombo?.SelectedIndex);
            Assert.Equal((int)TextRotationMode.FollowLineAutoFlip, bottomRotationCombo?.SelectedIndex);
        }
        finally
        {
            dialog.Close();
            lineText.Dispose();
        }
    }

    [AvaloniaFact]
    public void LineTextPanel_OrientationCombos_OfferThreeOptions_WithDefaultAtIndexZero()
    {
        var lineText = new LineTextObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            TopTextOrientationOverride = TextManualOrientation.Mirror,
            BottomTextOrientationOverride = TextManualOrientation.Rotate180
        };

        var dialog = new DrawingSettingsDialog(lineText, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var topOrientationCombo = dialog.FindControl<ComboBox>("LineTextTopOrientationCombo");
            var bottomOrientationCombo = dialog.FindControl<ComboBox>("LineTextBottomOrientationCombo");

            Assert.Equal(3, topOrientationCombo?.Items.Count);
            Assert.Equal(3, bottomOrientationCombo?.Items.Count);

            Assert.Equal((int)TextManualOrientation.Mirror, topOrientationCombo?.SelectedIndex);
            Assert.Equal((int)TextManualOrientation.Rotate180, bottomOrientationCombo?.SelectedIndex);
            Assert.Equal(0, (int)TextManualOrientation.Default);
        }
        finally
        {
            dialog.Close();
            lineText.Dispose();
        }
    }

    [AvaloniaFact]
    public void LineTextPanel_BindsTopAndBottomGroups_ForCurveLineTextObject_SharingTheSamePanel()
    {
        // CurveLineTextObject implements the same ILineTextAnnotatedObject contract as
        // LineTextObject, so it must reuse the identical LineTextPanel and controls (DRY) rather
        // than needing its own duplicate panel.
        var curve = new CurveLineTextObject(new[]
        {
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m),
            new ChartPoint(DateTime.Now.AddDays(2), 105m)
        })
        {
            TopText = "Breakout",
            TopFontSize = 20,
            BottomText = "Support",
            BottomFontSize = 11
        };

        var dialog = new DrawingSettingsDialog(curve, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = dialog.FindControl<StackPanel>("LineTextPanel");
            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);

            var topTextBox = dialog.FindControl<TextBox>("LineTextTopTextBox");
            var topFontSizeSpin = dialog.FindControl<NumericUpDown>("LineTextTopFontSizeSpin");
            var bottomTextBox = dialog.FindControl<TextBox>("LineTextBottomTextBox");
            var bottomFontSizeSpin = dialog.FindControl<NumericUpDown>("LineTextBottomFontSizeSpin");

            Assert.Equal("Breakout", topTextBox?.Text);
            Assert.Equal(20m, topFontSizeSpin?.Value);
            Assert.Equal("Support", bottomTextBox?.Text);
            Assert.Equal(11m, bottomFontSizeSpin?.Value);
        }
        finally
        {
            dialog.Close();
            curve.Dispose();
        }
    }
}
