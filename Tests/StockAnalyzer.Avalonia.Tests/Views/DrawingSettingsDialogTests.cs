using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
        registry.Register(new NurbsWeightedCurveSettingsPanelDefinition());
        registry.Register(new FixedRangeVolumeProfileSettingsPanelDefinition());
        registry.Register(new BarPatternSettingsPanelDefinition());
        registry.Register(new GeometricPatternSettingsPanelDefinition());
        registry.Register(new HarmonicPatternSettingsPanelDefinition());
        registry.Register(new AutoElliottWaveSettingsPanelDefinition());
        registry.Register(new LongShortPositionSettingsPanelDefinition());
        registry.Register(new PolylineSettingsPanelDefinition());
        registry.Register(new RangeSplineSettingsPanelDefinition());
        registry.Register(new DtwProjectionSettingsPanelDefinition());
        registry.Register(new KalmanFilterProjectionSettingsPanelDefinition());
        registry.Register(new CatenaryCurveSettingsPanelDefinition());
        registry.Register(new EllipseSettingsPanelDefinition());
        registry.Register(new EllipseAnnulusSettingsPanelDefinition());
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

    [AvaloniaFact]
    public void NurbsWeightedCurvePanel_BindsPerTypeLabelRangeAndValue_ForHyperbolaAndConicArc()
    {
        // NurbsHyperbolaObject and NurbsConicArcObject both implement INurbsWeightedCurveObject, so
        // they must reuse the identical NurbsWeightedCurvePanel/NurbsWeightedCurveWeightSpin
        // controls (DRY) while each still gets its own label text, Min/Max/Increment/FormatString,
        // and current Weight value populated dynamically from the concrete object.
        var start = new ChartPoint(DateTime.Now, 100m);
        var vertex = new ChartPoint(DateTime.Now.AddDays(1), 110m);
        var end = new ChartPoint(DateTime.Now.AddDays(2), 105m);

        var hyperbola = new NurbsHyperbolaObject(start, vertex, end) { Weight = 5.0 };
        var hyperbolaDialog = new DrawingSettingsDialog(hyperbola, CreateRegistry());
        try
        {
            hyperbolaDialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = hyperbolaDialog.FindControl<StackPanel>("NurbsWeightedCurvePanel");
            var label = hyperbolaDialog.FindControl<TextBlock>("NurbsWeightedCurveWeightLabel");
            var weightSpin = hyperbolaDialog.FindControl<NumericUpDown>("NurbsWeightedCurveWeightSpin");

            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);
            Assert.Equal(LocalizationManager.Instance["Setting_Nurbs_HyperbolaWeight"], label?.Text);
            Assert.Equal(1.01m, weightSpin?.Minimum);
            Assert.Equal(20.0m, weightSpin?.Maximum);
            Assert.Equal(0.1m, weightSpin?.Increment);
            Assert.Equal("0.0", weightSpin?.FormatString);
            Assert.Equal(5.0m, weightSpin?.Value);
        }
        finally
        {
            hyperbolaDialog.Close();
            hyperbola.Dispose();
        }

        var conicArc = new NurbsConicArcObject(start, vertex, end) { Weight = 0.3 };
        var conicArcDialog = new DrawingSettingsDialog(conicArc, CreateRegistry());
        try
        {
            conicArcDialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = conicArcDialog.FindControl<StackPanel>("NurbsWeightedCurvePanel");
            var label = conicArcDialog.FindControl<TextBlock>("NurbsWeightedCurveWeightLabel");
            var weightSpin = conicArcDialog.FindControl<NumericUpDown>("NurbsWeightedCurveWeightSpin");

            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);
            Assert.Equal(LocalizationManager.Instance["Setting_Nurbs_ConicArcWeight"], label?.Text);
            Assert.Equal(0.01m, weightSpin?.Minimum);
            Assert.Equal(0.99m, weightSpin?.Maximum);
            Assert.Equal(0.01m, weightSpin?.Increment);
            Assert.Equal("0.00", weightSpin?.FormatString);
            Assert.Equal(0.3m, weightSpin?.Value);
        }
        finally
        {
            conicArcDialog.Close();
            conicArc.Dispose();
        }
    }

    [AvaloniaFact]
    public void EllipseAnnulusPanel_BindsCircularCheckAndInnerRadiusSpinFromObject()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 11), 200m); // ratio ~0.98 (near outer edge)

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4) { IsCircular = true };
        var dialog = new DrawingSettingsDialog(ring, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = dialog.FindControl<StackPanel>("EllipseAnnulusPanel");
            var circularCheck = dialog.FindControl<CheckBox>("EllipseAnnulusCircularCheck");
            var innerRadiusSpin = dialog.FindControl<NumericUpDown>("EllipseAnnulusInnerRadiusSpin");

            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);
            Assert.True(circularCheck?.IsChecked);
            Assert.Equal((decimal)ring.InnerRadiusRatio, innerRadiusSpin?.Value);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_Commit_MigratesLegacyTwoPointObject_ToBoundaryProjectedAngleHandles()
    {
        // Regression test for a bug where enabling Arc mode made the circumference control points
        // invisible: a raw copy of the corner point (Points[1]) sits outside the ellipse boundary
        // (distance sqrt(Rx^2+Ry^2) from center), so handles placed there render stacked exactly on
        // top of the corner handle instead of appearing on the circumference. Points[2]/[3] now always
        // exist from construction (see EllipseObject's constructor), but objects persisted before that
        // model existed can still round-trip through JSON with only 2 points — Commit must migrate
        // those up to 4 points using the same boundary projection.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m); // rx=10 days, ry=100
        var ellipse = new EllipseObject(center, corner);
        ellipse.Points.RemoveRange(2, ellipse.Points.Count - 2); // simulate pre-migration legacy data

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(4, ellipse.Points.Count);
            Assert.NotEqual(corner, ellipse.Points[2]);
            Assert.NotEqual(corner, ellipse.Points[3]);
            Assert.Equal(ellipse.Points[2], ellipse.Points[3]); // still a degenerate zero-width start
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_RadiusLinesAndChordLineIndependently()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowRadiusLines = true, ShowChordLine = false };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var arcEnabledCheck = dialog.FindControl<CheckBox>("EllipseArcEnabledCheck");
            var radiusLinesCheck = dialog.FindControl<CheckBox>("EllipseShowRadiusLinesCheck");
            var chordLineCheck = dialog.FindControl<CheckBox>("EllipseShowChordLineCheck");

            Assert.True(arcEnabledCheck?.IsChecked);
            Assert.True(radiusLinesCheck?.IsChecked);
            Assert.False(chordLineCheck?.IsChecked);

            // Toggle both independently: Radius Lines off, Chord Line on.
            radiusLinesCheck!.IsChecked = false;
            chordLineCheck!.IsChecked = true;

            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.True(ellipse.IsArcEnabled);
            Assert.False(ellipse.ShowRadiusLines);
            Assert.True(ellipse.ShowChordLine);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_ShowTangentLines()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { ShowTangentLines = false };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var tangentLinesCheck = dialog.FindControl<CheckBox>("EllipseShowTangentLinesCheck");
            Assert.False(tangentLinesCheck?.IsChecked);

            tangentLinesCheck!.IsChecked = true;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.True(ellipse.ShowTangentLines);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_ExtendTangentLinesToChart()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { ExtendTangentLinesToChart = false };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var extendCheck = dialog.FindControl<CheckBox>("EllipseExtendTangentLinesToChartCheck");
            Assert.False(extendCheck?.IsChecked);

            extendCheck!.IsChecked = true;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.True(ellipse.ExtendTangentLinesToChart);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_AspectRatio()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { AspectRatio = 0.3 };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var aspectRatioSpin = dialog.FindControl<NumericUpDown>("EllipseAspectRatioSpin");
            Assert.Equal(0.3m, aspectRatioSpin?.Value);

            aspectRatioSpin!.Value = 0.7m;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(0.7, ellipse.AspectRatio);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_ControlPointColor()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { ControlPointColor = Colors.Orange };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var controlPointColorPicker = dialog.FindControl<ColorPicker>("EllipseControlPointColorPicker");
            Assert.Equal(Colors.Orange, controlPointColorPicker?.Color);

            controlPointColorPicker!.Color = Colors.Magenta;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(Colors.Magenta, ellipse.ControlPointColor);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_CircularCheckbox_MeaningIsInverted_UncheckedIsDefaultCircle()
    {
        // The checkbox now represents "Ellipse mode" (independent aspect ratio) rather than "Circle
        // mode": a freshly-created EllipseObject defaults to IsCircular=true (Circle), which must show
        // up as UNCHECKED, and checking it must flip IsCircular to false (Ellipse).
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner);
        Assert.True(ellipse.IsCircular); // sanity check on the default this test relies on

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var circularCheck = dialog.FindControl<CheckBox>("EllipseCircularCheck");
            Assert.False(circularCheck?.IsChecked); // Circle (default) => unchecked

            circularCheck!.IsChecked = true; // switch to Ellipse mode
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.False(ellipse.IsCircular);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_DynamicAspectRatioCheckbox_VisibleOnlyInEllipseMode_AndCommitsIndependently()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { IsCircular = true, AspectRatio = 0.25 };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var circularCheck = dialog.FindControl<CheckBox>("EllipseCircularCheck");
            var dynamicAspectRatioCheck = dialog.FindControl<CheckBox>("EllipseDynamicAspectRatioCheck");
            Assert.NotNull(dynamicAspectRatioCheck);

            // Circle mode (default here): the checkbox is only meaningful in Ellipse mode, so it starts hidden.
            Assert.False(dynamicAspectRatioCheck!.IsVisible);

            // Switching to Ellipse mode reveals it.
            circularCheck!.IsChecked = true;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(dynamicAspectRatioCheck.IsVisible);

            dynamicAspectRatioCheck.IsChecked = true;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.True(ellipse.DynamicAspectRatioByDistance);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_Commit_CapturesEquivalentAreaReferenceCorner_OnRisingEdgeOnly()
    {
        // Regression test: turning DynamicAspectRatioByDistance on must lock in a reference circle at
        // the EQUIVALENT-AREA radius (distance * sqrt(AspectRatio)), not a raw copy of the corner --
        // otherwise the shape would immediately snap to a circle (see the sibling AspectRatio=0.25 test
        // for the "does not visually jump" check). A second Commit (already on) must NOT re-capture --
        // otherwise dragging the corner while the feature is active would keep resetting its own
        // reference, defeating the "size stays fixed while dragging" point of the feature entirely.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { IsCircular = false, AspectRatio = 0.5 };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var dynamicAspectRatioCheck = dialog.FindControl<CheckBox>("EllipseDynamicAspectRatioCheck");
            dynamicAspectRatioCheck!.IsChecked = true;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            var expectedReference = EllipseArcGeometry.ScaleCornerInChartSpace(center, corner, Math.Sqrt(0.5));
            Assert.Equal(expectedReference, ellipse.DynamicAspectRatioReferenceCorner);

            // Simulate the corner being dragged elsewhere (as ChartInteractionController would do),
            // then Commit firing again (e.g. re-opening the dialog and clicking OK) while still on.
            var draggedCorner = new ChartPoint(new DateTime(2025, 1, 21), 500m);
            ellipse.Points[1] = draggedCorner;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(expectedReference, ellipse.DynamicAspectRatioReferenceCorner); // unchanged, not re-captured
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_Commit_TogglingDynamicAspectRatioOn_DoesNotSnapShapeToCircle()
    {
        // Regression test for the reported bug: turning "Dynamic Aspect Ratio" on must continue from
        // the ellipse's CURRENT appearance, not reset it to a circle.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { IsCircular = false, AspectRatio = 0.25 };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var dynamicAspectRatioCheck = dialog.FindControl<CheckBox>("EllipseDynamicAspectRatioCheck");
            dynamicAspectRatioCheck!.IsChecked = true;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.NotNull(ellipse.DynamicAspectRatioReferenceCorner);

            // The reference corner's offset from center must be scaled by sqrt(0.25) = 0.5 relative to
            // the raw corner -- NOT equal to the raw corner itself (scale 1.0), which would make Ry
            // snap to Rx (a circle) the instant this is turned on.
            var refCorner = ellipse.DynamicAspectRatioReferenceCorner!.Value;
            double originalOffsetTicks = corner.Time.Ticks - center.Time.Ticks;
            double originalOffsetPrice = (double)(corner.Price - center.Price);
            double actualTicksRatio = (refCorner.Time.Ticks - center.Time.Ticks) / originalOffsetTicks;
            double actualPriceRatio = (double)(refCorner.Price - center.Price) / originalOffsetPrice;

            Assert.Equal(0.5, actualTicksRatio, 2);
            Assert.Equal(0.5, actualPriceRatio, 2);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_PopulatesAndCommits_InnerRadiusRatio()
    {
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { InnerRadiusRatio = 0.3 };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var innerRadiusSpin = dialog.FindControl<NumericUpDown>("EllipseInnerRadiusSpin");
            Assert.Equal(0.3m, innerRadiusSpin?.Value);

            innerRadiusSpin!.Value = 0.6m;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(0.6, ellipse.InnerRadiusRatio);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EllipseSettingsPanel_Commit_CapturesEllipticityActivationCorner_OnRisingEdgeOnly()
    {
        // Regression test for the reported bug: checking "Ellipse Mode" (EllipseCircularCheck) must not
        // itself turn the circle into an ellipse -- it must keep looking like the same circle until the
        // corner is later dragged. Commit captures the corner's CURRENT position into
        // EllipticityActivationCorner on the rising edge (Circle -> Ellipse) only, matching the same
        // "only re-capture on rising edge" convention as DynamicAspectRatioReferenceCorner.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var ellipse = new EllipseObject(center, corner) { IsCircular = true };

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var circularCheck = dialog.FindControl<CheckBox>("EllipseCircularCheck");
            circularCheck!.IsChecked = true; // Ellipse Mode ON (inverse of IsCircular)
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.False(ellipse.IsCircular);
            Assert.Equal(corner, ellipse.EllipticityActivationCorner);

            // Simulate the corner being dragged elsewhere, then Commit firing again (e.g. re-opening the
            // dialog and clicking OK) while Ellipse Mode is already on -- must NOT re-capture, otherwise
            // every subsequent Commit would keep resetting the activation point to wherever the corner
            // currently is, permanently masking AspectRatio.
            var draggedCorner = new ChartPoint(new DateTime(2025, 1, 21), 500m);
            ellipse.Points[1] = draggedCorner;
            new EllipseSettingsPanelDefinition().Commit(dialog, ellipse);

            Assert.Equal(corner, ellipse.EllipticityActivationCorner); // unchanged, not re-captured
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForKalmanFilterProjectionObject()
    {
        var kalman = new StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject();
        kalman.Points.Add(new ChartPoint(DateTime.Now, 100m));
        kalman.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(kalman, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var genericColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var fillColorPicker = dialog.FindControl<ColorPicker>("KalmanFillColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(genericColorPicker);
            Assert.NotNull(fillColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.False(double.IsNaN(genericColorPicker!.Width));
            Assert.False(double.IsNaN(fillColorPicker!.Width));
            Assert.Equal(contentPanel.Bounds.Width, genericColorPicker.Width, 3);
            Assert.Equal(contentPanel.Bounds.Width, fillColorPicker.Width, 3);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Footer_HasFourEquallyWidthedThemeBoundButtons_WithDeleteSeparateFromOkCancelApply()
    {
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(trend, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var deleteButton = dialog.FindControl<Button>("DeleteButton");
            var okButton = dialog.FindControl<Button>("OkButton");
            var cancelButton = dialog.FindControl<Button>("CancelButton");
            var applyButton = dialog.FindControl<Button>("ApplyButton");

            foreach (var button in new[] { deleteButton, okButton, cancelButton, applyButton })
            {
                Assert.NotNull(button);
                Assert.True(button!.Classes.Contains("FooterBtn"));
            }

            // Equal width, per the requirement (Delete included).
            Assert.Equal(deleteButton!.Width, okButton!.Width);
            Assert.Equal(okButton.Width, cancelButton!.Width);
            Assert.Equal(cancelButton.Width, applyButton!.Width);

            // Delete sits in a separate Grid column from the OK/Cancel/Apply group, i.e. it is not
            // inside the same parent Panel as the other three.
            Assert.NotSame(deleteButton.Parent, okButton.Parent);
            Assert.Same(okButton.Parent, cancelButton.Parent);
            Assert.Same(cancelButton.Parent, applyButton.Parent);
        }
        finally
        {
            dialog.Close();
            trend.Dispose();
        }
    }

    [AvaloniaFact]
    public void ApplyClick_CommitsSettingsAndInvokesCallback_WithoutClosingTheDialog()
    {
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            Thickness = 1.0
        };

        IChartObject? appliedTo = null;
        var applyCallCount = 0;
        var dialog = new DrawingSettingsDialog(trend, CreateRegistry(), onApply: obj =>
        {
            appliedTo = obj;
            applyCallCount++;
        });
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var thicknessSpin = dialog.FindControl<NumericUpDown>("ThicknessSpin");
            thicknessSpin!.Value = 5m;

            var applyButton = dialog.FindControl<Button>("ApplyButton");
            applyButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(5.0, trend.Thickness);
            Assert.Same(trend, appliedTo);
            Assert.Equal(1, applyCallCount);
            Assert.True(dialog.IsVisible); // Apply must not close the dialog.

            // A second Apply with a different value must fire the callback again, confirming the
            // dialog stays fully interactive (not a one-shot handler).
            thicknessSpin.Value = 8m;
            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(8.0, trend.Thickness);
            Assert.Equal(2, applyCallCount);
        }
        finally
        {
            dialog.Close();
            trend.Dispose();
        }
    }

    [AvaloniaFact]
    public void Title_ReflectsCustomName_WhenSet()
    {
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            CustomName = "Support Zone"
        };

        var dialog = new DrawingSettingsDialog(trend, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var headerTitleText = dialog.FindControl<TextBlock>("HeaderTitleText");

            Assert.Equal("Support Zone", dialog.Title);
            Assert.Equal("Support Zone", headerTitleText?.Text);
        }
        finally
        {
            dialog.Close();
            trend.Dispose();
        }
    }

    [AvaloniaFact]
    public void Title_FallsBackToLocalizedTypeName_WhenCustomNameNotSet()
    {
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m));
        Assert.Null(trend.CustomName); // sanity check on the default this test relies on

        var expectedName = LocalizationManager.Instance["DrawTool_TrendLine"] ?? trend.Type.ToString();

        var dialog = new DrawingSettingsDialog(trend, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var headerTitleText = dialog.FindControl<TextBlock>("HeaderTitleText");

            Assert.Equal(expectedName, dialog.Title);
            Assert.Equal(expectedName, headerTitleText?.Text);
        }
        finally
        {
            dialog.Close();
            trend.Dispose();
        }
    }
}
