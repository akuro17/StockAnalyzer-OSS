using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
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
        registry.Register(new AnchoredVwapSettingsPanelDefinition());
        registry.Register(new BarPatternSettingsPanelDefinition());
        registry.Register(new GeometricPatternSettingsPanelDefinition());
        registry.Register(new HarmonicPatternSettingsPanelDefinition());
        registry.Register(new AutoElliottWaveSettingsPanelDefinition());
        registry.Register(new LongShortPositionSettingsPanelDefinition());
        registry.Register(new PolylineSettingsPanelDefinition());
        registry.Register(new RangeSplineSettingsPanelDefinition());
        registry.Register(new DtwProjectionSettingsPanelDefinition());
        registry.Register(new KalmanFilterProjectionSettingsPanelDefinition());
        registry.Register(new FftProjectionSettingsPanelDefinition());
        registry.Register(new HmmProjectionSettingsPanelDefinition());
        registry.Register(new PearsonProjectionSettingsPanelDefinition());
        registry.Register(new FrechetProjectionSettingsPanelDefinition());
        registry.Register(new SsaProjectionSettingsPanelDefinition());
        registry.Register(new CatenaryCurveSettingsPanelDefinition());
        registry.Register(new EllipseSettingsPanelDefinition());
        registry.Register(new EllipseAnnulusSettingsPanelDefinition());
        registry.Register(new IconSettingsPanelDefinition());
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

            Assert.Equal(50, borderColorPicker!.Width);
            Assert.Equal(50, backgroundColorPicker!.Width);
            Assert.Equal(28, borderColorPicker.Height);
            Assert.Equal(28, backgroundColorPicker.Height);
        }
        finally
        {
            dialog.Close();
            callout.Dispose();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_DtwProjectionObject_OpenAndClose()
    {
        var dtw = new StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject();
        dtw.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        dtw.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));
        dtw.ProjectedPath.Add(new StockAnalyzer.Core.Models.Point(1000, 105.0));
        dtw.MatchedStartTime = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        dtw.MatchedEndTime = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);

        var manager = new ChartObjectManager();
        manager.AddObject(dtw);
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(dtw, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_FftProjectionObject_OpenAndClose()
    {
        var fft = new StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject();
        fft.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        fft.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var manager = new ChartObjectManager();
        manager.AddObject(fft);
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(fft, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_HmmProjectionObject_OpenAndClose()
    {
        var hmm = new StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject();
        hmm.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        hmm.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var manager = new ChartObjectManager();
        manager.AddObject(hmm);
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(hmm, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_PearsonProjectionObject_OpenAndClose()
    {
        var pearson = new StockAnalyzer.Avalonia.Drawing.Objects.PearsonProjectionObject();
        pearson.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        pearson.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var manager = new ChartObjectManager();
        manager.AddObject(pearson);
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(pearson, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_SsaProjectionObject_OpenAndClose()
    {
        var ssa = new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject();
        ssa.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        ssa.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var manager = new ChartObjectManager();
        manager.AddObject(ssa);
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(ssa, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_Dtw_WithFftPresent_OpenAndClose()
    {
        var dtw = new StockAnalyzer.Avalonia.Drawing.Objects.DtwProjectionObject();
        dtw.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        dtw.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var fft = new StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject();
        fft.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        fft.Points.Add(new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 110m));

        var manager = new ChartObjectManager();
        manager.AddObject(dtw);
        manager.AddObject(fft);

        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(new DrawingDocumentKey("TEST", TimeframeType.Daily));
        using var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        var dialog = new DrawingSettingsDialog(dtw, CreateRegistry(), onApply: null, coordinator: coordinator);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dialog.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
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
            IsTransparent = true
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

            var transparentCheck = dialog.FindControl<CheckBox>("LineTextTransparentCheck");
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

            Assert.True(transparentCheck?.IsChecked);

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

            Assert.Equal(50, genericColorPicker!.Width);
            Assert.Equal(50, fillColorPicker!.Width);
            Assert.Equal(28, genericColorPicker.Height);
            Assert.Equal(28, fillColorPicker.Height);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForFftProjectionObject()
    {
        var fftObj = new StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject();
        fftObj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        fftObj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(fftObj, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var genericColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var fillColorPicker = dialog.FindControl<ColorPicker>("FftFillColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(genericColorPicker);
            Assert.NotNull(fillColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.Equal(50, genericColorPicker!.Width);
            Assert.Equal(50, fillColorPicker!.Width);
            Assert.Equal(28, genericColorPicker.Height);
            Assert.Equal(28, fillColorPicker.Height);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForHmmProjectionObject()
    {
        var hmmObj = new StockAnalyzer.Avalonia.Drawing.Objects.HmmProjectionObject();
        hmmObj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        hmmObj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(hmmObj, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var genericColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var fillColorPicker = dialog.FindControl<ColorPicker>("HmmFillColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(genericColorPicker);
            Assert.NotNull(fillColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.Equal(50, genericColorPicker!.Width);
            Assert.Equal(50, fillColorPicker!.Width);
            Assert.Equal(28, genericColorPicker.Height);
            Assert.Equal(28, fillColorPicker.Height);
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

    [AvaloniaFact]
    public void ScrollViewer_UsesSidebarScrollViewerTheme_And_AllowAutoHideIsFalse()
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

            var scrollViewer = dialog.FindControl<StackPanel>("SettingsContentPanel")?
                .GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()
                ?? dialog.GetVisualDescendants().OfType<ScrollViewer>().First();

            Assert.NotNull(scrollViewer);
            var expectedTheme = (ControlTheme)global::Avalonia.Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            Assert.Same(expectedTheme, scrollViewer.Theme);
            Assert.False(scrollViewer.AllowAutoHide);
        }
        finally
        {
            dialog.Close();
            trend.Dispose();
        }
    }

    [AvaloniaFact]
    public void FftProjectionPanel_BindsPriceSource_UsingPriceTypeOptions()
    {
        var fftObj = new StockAnalyzer.Avalonia.Drawing.Objects.FftProjectionObject
        {
            PriceSource = StockAnalyzer.Core.Models.PriceType.Typical
        };
        fftObj.Points.Add(new ChartPoint(DateTime.Now, 100m));
        fftObj.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(fftObj, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var priceCombo = dialog.FindControl<ComboBox>("FftProjectionPriceFieldCombo");
            Assert.NotNull(priceCombo);
            // PriceType.Typical index in PriceTypeOptions (Open=0, High=1, Low=2, Close=3, Median=4, Midpoint=5, Typical=6)
            Assert.Equal(6, priceCombo.SelectedIndex);

            // Change to Weighted (index 7) and simulate apply/commit
            priceCombo.SelectedIndex = 7;
            var definition = new FftProjectionSettingsPanelDefinition();
            definition.Commit(dialog, fftObj);

            Assert.Equal(StockAnalyzer.Core.Models.PriceType.Weighted, fftObj.PriceSource);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void RangeSplinePanel_BindsPriceSource_UsingPriceTypeOptions()
    {
        var spline = new RangeSplineObject
        {
            PriceSource = StockAnalyzer.Core.Models.PriceType.High
        };
        spline.Points.Add(new ChartPoint(DateTime.Now, 100m));
        spline.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var registry = CreateRegistry();
        registry.Register(new RangeSplineSettingsPanelDefinition());
        var dialog = new DrawingSettingsDialog(spline, registry);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var priceCombo = dialog.FindControl<ComboBox>("RangeSplinePriceFieldCombo");
            Assert.NotNull(priceCombo);
            // PriceType.High index in PriceTypeOptions (Open=0, High=1)
            Assert.Equal(1, priceCombo.SelectedIndex);

            // Change to Median (index 4) and simulate commit
            priceCombo.SelectedIndex = 4;
            var definition = new RangeSplineSettingsPanelDefinition();
            definition.Commit(dialog, spline);

            Assert.Equal(StockAnalyzer.Core.Models.PriceType.Median, spline.PriceSource);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void FftSpectrumPanel_BindsPriceSource_UsingPriceTypeOptions()
    {
        var fft = new FftSpectrumObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(1), 110m))
        {
            PriceSource = StockAnalyzer.Core.Models.PriceType.Low
        };

        var registry = CreateRegistry();
        registry.Register(new FftSpectrumSettingsPanelDefinition());
        var dialog = new DrawingSettingsDialog(fft, registry);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var priceCombo = dialog.FindControl<ComboBox>("FftPriceFieldCombo");
            Assert.NotNull(priceCombo);
            // PriceType.Low index in PriceTypeOptions (Open=0, High=1, Low=2)
            Assert.Equal(2, priceCombo.SelectedIndex);

            // Change to HeikinAshiClose (index 12) and simulate commit
            priceCombo.SelectedIndex = 12;
            var definition = new FftSpectrumSettingsPanelDefinition();
            definition.Commit(dialog, fft);
            Assert.Equal(StockAnalyzer.Core.Models.PriceType.HeikinAshiClose, fft.PriceSource);

            // Change to TrueHigh (index 13) and simulate commit
            priceCombo.SelectedIndex = 13;
            definition.Commit(dialog, fft);
            Assert.Equal(StockAnalyzer.Core.Models.PriceType.TrueHigh, fft.PriceSource);
        }
        finally
        {
            dialog.Close();
            fft.Dispose();
        }
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForFrechetProjectionObject()
    {
        var frechet = new StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject();
        frechet.Points.Add(new ChartPoint(DateTime.Now, 100m));
        frechet.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(frechet, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var genericColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var unmatchedPicker = dialog.FindControl<ColorPicker>("FrechetUnmatchedColorPicker");
            var fillColorPicker = dialog.FindControl<ColorPicker>("FrechetFillColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(genericColorPicker);
            Assert.NotNull(unmatchedPicker);
            Assert.NotNull(fillColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.Equal(50, genericColorPicker!.Width);
            Assert.Equal(50, unmatchedPicker!.Width);
            Assert.Equal(50, fillColorPicker!.Width);
            Assert.Equal(28, genericColorPicker.Height);
            Assert.Equal(28, unmatchedPicker.Height);
            Assert.Equal(28, fillColorPicker.Height);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_BindsAndCommits_FrechetProjectionObject()
    {
        var frechet = new StockAnalyzer.Avalonia.Drawing.Objects.FrechetProjectionObject
        {
            PriceSource = StockAnalyzer.Core.Models.PriceType.Close,
            FutureSteps = 25,
            FillOpacity = 30,
            ShowConfidenceBand = true,
            ShowMatchHighlight = true
        };
        frechet.Points.Add(new ChartPoint(DateTime.Now, 100m));
        frechet.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(frechet, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var priceCombo = dialog.FindControl<ComboBox>("FrechetProjectionPriceFieldCombo");
            var maxDistanceSpin = dialog.FindControl<NumericUpDown>("FrechetMaxDistanceSpin");
            var futureStepsSpin = dialog.FindControl<NumericUpDown>("FrechetFutureStepsSpin");
            var fillOpacitySpin = dialog.FindControl<NumericUpDown>("FrechetFillOpacitySpin");
            var showConfidenceBandCheck = dialog.FindControl<CheckBox>("FrechetShowConfidenceBandCheck");
            var confidenceMultiplierSpin = dialog.FindControl<NumericUpDown>("FrechetConfidenceMultiplierSpin");
            var showMatchHighlightCheck = dialog.FindControl<CheckBox>("FrechetShowMatchHighlightCheck");

            Assert.NotNull(priceCombo);
            Assert.NotNull(maxDistanceSpin);
            Assert.NotNull(futureStepsSpin);
            Assert.NotNull(fillOpacitySpin);
            Assert.NotNull(showConfidenceBandCheck);
            Assert.NotNull(confidenceMultiplierSpin);
            Assert.NotNull(showMatchHighlightCheck);

            Assert.Equal(3, priceCombo.SelectedIndex); // Close is index 3
            Assert.Equal(0.00m, maxDistanceSpin.Value);
            Assert.Equal(25, futureStepsSpin.Value);
            Assert.Equal(30, fillOpacitySpin.Value);
            Assert.True(showConfidenceBandCheck.IsChecked);
            Assert.Equal(2.0m, confidenceMultiplierSpin.Value);
            Assert.True(showMatchHighlightCheck.IsChecked);

            // Modify values
            priceCombo.SelectedIndex = 1; // High
            maxDistanceSpin.Value = 1.50m;
            futureStepsSpin.Value = 35;
            fillOpacitySpin.Value = 50;
            showConfidenceBandCheck.IsChecked = false;
            confidenceMultiplierSpin.Value = 2.50m;
            showMatchHighlightCheck.IsChecked = false;

            var definition = new FrechetProjectionSettingsPanelDefinition();
            definition.Commit(dialog, frechet);

            Assert.Equal(StockAnalyzer.Core.Models.PriceType.High, frechet.PriceSource);
            Assert.Equal(1.50, frechet.MaxDistance);
            Assert.Equal(35, frechet.FutureSteps);
            Assert.Equal(50, frechet.FillOpacity);
            Assert.False(frechet.ShowConfidenceBand);
            Assert.Equal(2.50m, frechet.ConfidenceMultiplier);
            Assert.False(frechet.ShowMatchHighlight);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ColorPickers_StretchToFullContentWidth_ForSsaProjectionObject()
    {
        var ssa = new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject();
        ssa.Points.Add(new ChartPoint(DateTime.Now, 100m));
        ssa.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(ssa, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var genericColorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var fillColorPicker = dialog.FindControl<ColorPicker>("SsaFillColorPicker");

            Assert.NotNull(contentPanel);
            Assert.NotNull(genericColorPicker);
            Assert.NotNull(fillColorPicker);
            Assert.True(contentPanel!.Bounds.Width > 0);

            Assert.Equal(50, genericColorPicker!.Width);
            Assert.Equal(50, fillColorPicker!.Width);
            Assert.Equal(28, genericColorPicker.Height);
            Assert.Equal(28, fillColorPicker.Height);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SsaProjectionSettingsPanelDefinition_Populates_And_Commits_Correctly()
    {
        var ssa = new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject
        {
            EmbeddingDimension = 15,
            NumComponents = 3,
            PriceSource = StockAnalyzer.Core.Models.PriceType.Median,
            DetrendMethod = StockAnalyzer.Core.Analysis.SsaDetrendMode.LeastSquaresLinear,
            ShowReconstructedPath = true,
            FutureSteps = 25,
            FillOpacity = 20,
            ShowConfidenceBand = true,
            ConfidenceMultiplier = 1.8m
        };
        ssa.Points.Add(new ChartPoint(DateTime.Now, 100m));
        ssa.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(ssa, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var ssaPanel = dialog.FindControl<StackPanel>("SsaProjectionPanel");
            var fillOpacityPanel = dialog.FindControl<StackPanel>("SsaFillOpacityPanel");
            var embeddingDimensionSpin = dialog.FindControl<NumericUpDown>("SsaEmbeddingDimensionSpin");
            var numComponentsSpin = dialog.FindControl<NumericUpDown>("SsaNumComponentsSpin");
            var priceCombo = dialog.FindControl<ComboBox>("SsaProjectionPriceFieldCombo");
            var detrendMethodCombo = dialog.FindControl<ComboBox>("SsaProjectionDetrendMethodCombo");
            var showReconstructedCheck = dialog.FindControl<CheckBox>("SsaShowReconstructedPathCheck");
            var futureStepsSpin = dialog.FindControl<NumericUpDown>("SsaFutureStepsSpin");
            var fillOpacitySpin = dialog.FindControl<NumericUpDown>("SsaFillOpacitySpin");
            var showConfidenceBandCheck = dialog.FindControl<CheckBox>("SsaShowConfidenceBandCheck");
            var confidenceMultiplierSpin = dialog.FindControl<NumericUpDown>("SsaConfidenceMultiplierSpin");

            Assert.NotNull(ssaPanel);
            Assert.NotNull(fillOpacityPanel);
            Assert.True(ssaPanel.IsVisible);
            Assert.True(fillOpacityPanel.IsVisible);

            Assert.NotNull(embeddingDimensionSpin);
            Assert.NotNull(numComponentsSpin);
            Assert.NotNull(priceCombo);
            Assert.NotNull(detrendMethodCombo);
            Assert.NotNull(showReconstructedCheck);
            Assert.NotNull(futureStepsSpin);
            Assert.NotNull(fillOpacitySpin);
            Assert.NotNull(showConfidenceBandCheck);
            Assert.NotNull(confidenceMultiplierSpin);

            Assert.Equal(15, (int)embeddingDimensionSpin.Value!);
            Assert.Equal(3, (int)numComponentsSpin.Value!);
            Assert.Equal(0, detrendMethodCombo.SelectedIndex);
            Assert.True(showReconstructedCheck.IsChecked);
            Assert.Equal(25, (int)futureStepsSpin.Value!);
            Assert.Equal(20, (int)fillOpacitySpin.Value!);
            Assert.True(showConfidenceBandCheck.IsChecked);
            Assert.Equal(1.8m, confidenceMultiplierSpin.Value);

            // Modify values
            embeddingDimensionSpin.Value = 20;
            numComponentsSpin.Value = 4;
            priceCombo.SelectedIndex = 3; // Close
            detrendMethodCombo.SelectedIndex = 2; // None
            showReconstructedCheck.IsChecked = false;
            futureStepsSpin.Value = 30;
            fillOpacitySpin.Value = 40;
            showConfidenceBandCheck.IsChecked = false;
            confidenceMultiplierSpin.Value = 2.5m;

            var definition = new SsaProjectionSettingsPanelDefinition();
            definition.Commit(dialog, ssa);

            Assert.Equal(20, ssa.EmbeddingDimension);
            Assert.Equal(4, ssa.NumComponents);
            Assert.Equal(StockAnalyzer.Core.Models.PriceType.Close, ssa.PriceSource);
            Assert.Equal(StockAnalyzer.Core.Analysis.SsaDetrendMode.None, ssa.DetrendMethod);
            Assert.False(ssa.ApplyDetrend);
            Assert.False(ssa.ShowReconstructedPath);
            Assert.Equal(30, ssa.FutureSteps);
            Assert.Equal(40, ssa.FillOpacity);
            Assert.False(ssa.ShowConfidenceBand);
            Assert.Equal(2.5m, ssa.ConfidenceMultiplier);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_OverFocusedNumericUpDown_ScrollsTheForm_WithoutChangingTheValue()
    {
        // Regression test for the reported bug: Avalonia's NumericUpDown consumes PointerWheelChanged
        // for value stepping while focused, so the wheel never reached the dialog's ScrollViewer and
        // the form would not scroll (it would silently change the number instead) whenever the pointer
        // rested over a spin -- which, right after editing a field, is almost always the case.
        // WheelScrollRedirectBehavior on the root ScrollViewer must now scroll the form and leave the
        // NumericUpDown's value untouched.
        var ssa = new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject
        {
            EmbeddingDimension = 15
        };
        ssa.Points.Add(new ChartPoint(DateTime.Now, 100m));
        ssa.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(ssa, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var categoryListBox = dialog.FindControl<ListBox>("CategoryListBox");
            if (categoryListBox != null)
            {
                categoryListBox.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
            }

            var scrollViewer = dialog.GetVisualDescendants().OfType<ScrollViewer>().First();
            // Sanity: the SSA projection panel overflows the fixed-height dialog, so there is
            // something to scroll.
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

            var embeddingDimensionSpin = dialog.FindControl<NumericUpDown>("SsaEmbeddingDimensionSpin");
            Assert.NotNull(embeddingDimensionSpin);
            embeddingDimensionSpin!.Focus();
            Dispatcher.UIThread.RunJobs();

            var valueBefore = embeddingDimensionSpin.Value;
            var offsetBefore = scrollViewer.Offset.Y;

            var wheelPoint = embeddingDimensionSpin.TranslatePoint(
                new global::Avalonia.Point(
                    embeddingDimensionSpin.Bounds.Width / 2,
                    embeddingDimensionSpin.Bounds.Height / 2),
                dialog) ?? default;

            // Negative Delta.Y == wheel down == scroll toward the bottom (Offset.Y increases).
            dialog.MouseWheel(wheelPoint, new global::Avalonia.Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > offsetBefore); // the form scrolled
            Assert.Equal(valueBefore, embeddingDimensionSpin.Value); // the spin did not step
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_OverBlankGapBetweenControls_AlsoScrollsTheForm()
    {
        // Follow-up regression: the scoped SidebarScrollViewerTheme template used not to paint the
        // ScrollViewer's background, so a wheel over the blank spacing between controls routed
        // outside the ScrollViewer subtree entirely and scrolled nothing. The theme now binds
        // Background="{TemplateBinding Background}" (Transparent), making the whole ScrollViewer
        // hit-testable so WheelScrollRedirectBehavior sees the gesture over blank areas too.
        var ssa = new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject();
        ssa.Points.Add(new ChartPoint(DateTime.Now, 100m));
        ssa.Points.Add(new ChartPoint(DateTime.Now.AddDays(1), 110m));

        var dialog = new DrawingSettingsDialog(ssa, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var categoryListBox = dialog.FindControl<ListBox>("CategoryListBox");
            if (categoryListBox != null)
            {
                categoryListBox.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
            }

            var scrollViewer = dialog.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection")!;
            var firstChild = specificSection.Children
                .OfType<Control>()
                .First(c => c.IsVisible && c.Bounds.Height > 0);

            // A point squarely inside the 12px Spacing gap below the first control: no child sits here.
            var gapPoint = specificSection.TranslatePoint(
                new global::Avalonia.Point(firstChild.Bounds.X + 8, firstChild.Bounds.Bottom + 6),
                dialog) ?? default;

            var offsetBefore = scrollViewer.Offset.Y;
            dialog.MouseWheel(gapPoint, new global::Avalonia.Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > offsetBefore);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("CyclicLines")]
    [InlineData("GannFan")]
    [InlineData("FibArc")]
    public void DrawingSettingsDialog_ShowsColorAndThickness_AndCommits_ForUnspecializedTools(string toolType)
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 10), 200m);

        IChartObject drawing = toolType switch
        {
            "CyclicLines" => new CyclicLinesObject(p1, p2) { Color = Colors.Red, Thickness = 2.0 },
            "GannFan" => new GannFanObject(p1, p2) { Color = Colors.Green, Thickness = 3.0 },
            "FibArc" => new FibonacciArcObject(p1, p2) { Color = Colors.Blue, Thickness = 1.5 },
            _ => throw new ArgumentException($"Unknown tool type: {toolType}")
        };

        IChartObject? appliedTo = null;
        var dialog = new DrawingSettingsDialog(drawing, CreateRegistry(), obj => appliedTo = obj);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var colorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var thicknessSpin = dialog.FindControl<NumericUpDown>("ThicknessSpin");
            var applyButton = dialog.FindControl<Button>("ApplyButton");

            Assert.NotNull(genericColorPanel);
            Assert.True(genericColorPanel!.IsVisible);
            Assert.NotNull(thicknessPanel);
            Assert.True(thicknessPanel!.IsVisible);

            Assert.NotNull(colorPicker);
            Assert.Equal(drawing.Color, colorPicker!.Color);

            Assert.NotNull(thicknessSpin);
            Assert.Equal((decimal)drawing.Thickness, thicknessSpin!.Value);

            // Change values and apply
            colorPicker.Color = Colors.Yellow;
            thicknessSpin.Value = 7m;

            applyButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(Colors.Yellow, drawing.Color);
            Assert.Equal(7.0, drawing.Thickness);
            Assert.Same(drawing, appliedTo);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void AnchoredVwapSettingsPanel_PopulatesAndCommits_PriceSource()
    {
        var anchor = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var vwap = new AnchoredVwapObject(anchor) { PriceSource = PriceType.Typical };

        var dialog = new DrawingSettingsDialog(vwap, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var panel = dialog.FindControl<StackPanel>("AnchoredVwapPanel");
            var priceCombo = dialog.FindControl<ComboBox>("AnchoredVwapPriceFieldCombo");

            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);
            Assert.NotNull(priceCombo);
            Assert.Equal(6, priceCombo!.SelectedIndex); // Typical is index 6

            // Change to Close (index 3)
            priceCombo.SelectedIndex = 3;
            new AnchoredVwapSettingsPanelDefinition().Commit(dialog, vwap);

            Assert.Equal(PriceType.Close, vwap.PriceSource);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void VolumeProfileSettingsPanel_PopulatesCommitsAndRetainsValues()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 10), 200m);
        var frvp = new FixedRangeVolumeProfileObject(p1, p2);

        var registry = CreateRegistry();
        var dialog = new DrawingSettingsDialog(frvp, registry);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var vpPanel = dialog.FindControl<StackPanel>("VolumeProfilePanel");
            Assert.NotNull(vpPanel);
            Assert.True(vpPanel!.IsVisible);

            Assert.Equal(Color.Parse("#FFEB3B"), frvp.ValueAreaColor);
            Assert.Equal(Color.Parse("#90A4AE"), frvp.ProfileColor);

            var colorPicker = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var thicknessSpin = dialog.FindControl<NumericUpDown>("ThicknessSpin");
            var vaColorPicker = dialog.FindControl<ColorPicker>("ValueAreaColorPicker");
            var profileColorPicker = dialog.FindControl<ColorPicker>("ProfileColorPicker");
            var opacitySpin = dialog.FindControl<NumericUpDown>("OpacitySpin");
            var fillColorPicker = dialog.FindControl<ColorPicker>("VpFillColorPicker");
            var fillOpacitySpin = dialog.FindControl<NumericUpDown>("VpFillOpacitySpin");
            var repeatModeCombo = dialog.FindControl<ComboBox>("VpRepeatModeCombo");
            var lockRangeCheck = dialog.FindControl<CheckBox>("VpLockRangeCheck");
            var rangeBarsSpin = dialog.FindControl<NumericUpDown>("VpRangeBarsSpin");
            var okButton = dialog.FindControl<Button>("OkButton");

            Assert.NotNull(colorPicker);
            Assert.NotNull(thicknessSpin);
            Assert.NotNull(vaColorPicker);
            Assert.NotNull(profileColorPicker);
            Assert.NotNull(opacitySpin);
            Assert.NotNull(fillColorPicker);
            Assert.NotNull(fillOpacitySpin);
            Assert.NotNull(repeatModeCombo);
            Assert.NotNull(lockRangeCheck);
            Assert.NotNull(rangeBarsSpin);
            Assert.NotNull(okButton);
            Assert.Equal(0, repeatModeCombo!.SelectedIndex);

            // Change values
            colorPicker!.Color = Colors.Red;
            thicknessSpin!.Value = 3m;
            vaColorPicker!.Color = Colors.Magenta;
            profileColorPicker!.Color = Colors.Cyan;
            opacitySpin!.Value = 70m;
            fillColorPicker!.Color = Colors.Yellow;
            fillOpacitySpin!.Value = 40m;
            repeatModeCombo.SelectedIndex = 1;
            lockRangeCheck!.IsChecked = true;
            rangeBarsSpin!.Value = 25m;

            okButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Colors.Red, frvp.Color);
            Assert.Equal(3.0, frvp.Thickness);
            Assert.Equal(Colors.Magenta, frvp.ValueAreaColor);
            Assert.Equal(Colors.Cyan, frvp.ProfileColor);
            Assert.Equal(0.7, frvp.Opacity);
            Assert.Equal(Colors.Yellow, frvp.FillColor);
            Assert.Equal(40, frvp.FillOpacity);
            Assert.Equal(VolumeProfileRepeatMode.RangeBar, frvp.RepeatMode);
            Assert.True(frvp.LockRange);
            Assert.Equal(25, frvp.RangeBars);
        }
        finally
        {
            dialog.Close();
            frvp.Dispose();
        }
    }

    [AvaloniaFact]
    public void VolumeProfileSettingsPanel_WithCoordinator_CommitSucceeds()
    {
        var testKey = new StockAnalyzer.Core.Models.Drawing.DrawingDocumentKey("7203", TimeframeType.Daily);
        var manager = new ChartObjectManager();
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(testKey);
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(testKey);
        var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        using (coordinator)
        {
            var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
            var p2 = new ChartPoint(new DateTime(2025, 1, 10), 200m);
            var frvp = new FixedRangeVolumeProfileObject(p1, p2);
            manager.AddObject(frvp);

            var registry = CreateRegistry();
            var dialog = new DrawingSettingsDialog(frvp, registry, onApply: null, coordinator: coordinator);
            try
            {
                dialog.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var okButton = dialog.FindControl<Button>("OkButton");
                var vaColorPicker = dialog.FindControl<ColorPicker>("ValueAreaColorPicker");
                var profileColorPicker = dialog.FindControl<ColorPicker>("ProfileColorPicker");
                vaColorPicker!.Color = Colors.Magenta;
                profileColorPicker!.Color = Colors.Lime;

                okButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(Colors.Magenta, frvp.ValueAreaColor);
                Assert.Equal(Colors.Lime, frvp.ProfileColor);
            }
            finally
            {
                dialog.Close();
                frvp.Dispose();
            }
        }
    }

    /// <summary>
    /// F10 regression: previously, changing RangeBars moved Points only inside
    /// FixedRangeVolumeProfileObject.Recalculate(), which ran AFTER DrawingEditCoordinator.Commit()
    /// had already snapshotted the object's field values for history/session persistence -- so the
    /// persisted "after" state carried the new RangeBars paired with the still-stale (pre-move)
    /// Points. Passing `candles` into the dialog lets ApplyCurrentSettingsToModel() recalculate
    /// BEFORE TakeSnapshot()/Commit(), so the two stay consistent in whatever gets persisted. Undo
    /// then Redo restores the object purely from that persisted state (no live Recalculate call),
    /// so it directly exposes whether the persisted Points ever went stale.
    /// See sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F10.
    /// </summary>
    [AvaloniaFact]
    public void VolumeProfileSettingsPanel_F10_RangeBarsChangeCommitsConsistentPoints()
    {
        var testKey = new StockAnalyzer.Core.Models.Drawing.DrawingDocumentKey("7203", TimeframeType.Daily);
        var manager = new ChartObjectManager();
        var history = new StockAnalyzer.Avalonia.Services.Drawing.DrawingHistoryService(testKey);
        var session = new StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSession(testKey);
        var coordinator = new StockAnalyzer.Avalonia.Services.Drawing.DrawingEditCoordinator(manager, history, session);

        using (coordinator)
        {
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 30; i++)
            {
                var t = new DateTime(2025, 1, 1).AddDays(i);
                candles.Add(new CoreCandleData(t, 100m, 110m, 90m, 105m, 1000 + i));
            }

            var p1 = new ChartPoint(candles[0].Timestamp, 100m);
            var p2 = new ChartPoint(candles[4].Timestamp, 200m);
            var frvp = new FixedRangeVolumeProfileObject(p1, p2);
            manager.AddObject(frvp);

            var registry = CreateRegistry();
            var dialog = new DrawingSettingsDialog(frvp, registry, onApply: null, coordinator: coordinator, candles: candles);
            try
            {
                dialog.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var rangeBarsSpin = dialog.FindControl<NumericUpDown>("VpRangeBarsSpin");
                var okButton = dialog.FindControl<Button>("OkButton");
                rangeBarsSpin!.Value = 10m;

                okButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(10, frvp.RangeBars);
                Assert.Equal(candles[9].Timestamp, frvp.Points[1].Time);

                var undoResult = coordinator.Undo();
                Assert.True(undoResult.IsSuccess);
                var redoResult = coordinator.Redo();
                Assert.True(redoResult.IsSuccess);

                var restored = manager.Objects.OfType<FixedRangeVolumeProfileObject>().Single();
                Assert.Equal(candles[0].Timestamp, restored.Points[0].Time);
                Assert.Equal(candles[9].Timestamp, restored.Points[1].Time);
                Assert.Equal(10, restored.RangeBars);
            }
            finally
            {
                dialog.Close();
                frvp.Dispose();
            }
        }
    }

    [AvaloniaFact]
    public void VolumeProfileSettingsPanel_RepeatMode_SelectWeeklyAndMonthlyCommitsCorrectly()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 5), 200m);
        var frvp = new FixedRangeVolumeProfileObject(p1, p2);

        var registry = CreateRegistry();
        var dialog = new DrawingSettingsDialog(frvp, registry, onApply: null);
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var combo = dialog.FindControl<ComboBox>("VpRepeatModeCombo");
            var rangeBarsSpin = dialog.FindControl<NumericUpDown>("VpRangeBarsSpin");
            var lockRangeCheck = dialog.FindControl<CheckBox>("VpLockRangeCheck");
            var okButton = dialog.FindControl<Button>("OkButton");

            Assert.NotNull(combo);
            Assert.NotNull(rangeBarsSpin);
            Assert.NotNull(lockRangeCheck);
            Assert.Equal(4, combo!.ItemCount);
            Assert.Equal(0, combo.SelectedIndex); // Default
            Assert.True(rangeBarsSpin!.IsEnabled);
            Assert.True(lockRangeCheck!.IsEnabled);

            // Select Weekly (Index 2) -> RangeBars and LockRange should disable
            combo.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.False(rangeBarsSpin.IsEnabled);
            Assert.False(lockRangeCheck.IsEnabled);

            // Select RangeBar (Index 1) -> should re-enable
            combo.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(rangeBarsSpin.IsEnabled);
            Assert.True(lockRangeCheck.IsEnabled);

            // Select Weekly (Index 2) and Commit
            combo.SelectedIndex = 2;
            okButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(VolumeProfileRepeatMode.Weekly, frvp.RepeatMode);
        }
        finally
        {
            dialog.Close();
            frvp.Dispose();
        }

        // Now test Monthly (Index 3)
        var frvp2 = new FixedRangeVolumeProfileObject(p1, p2);
        var dialog2 = new DrawingSettingsDialog(frvp2, registry, onApply: null);
        try
        {
            dialog2.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var combo = dialog2.FindControl<ComboBox>("VpRepeatModeCombo");
            var rangeBarsSpin = dialog2.FindControl<NumericUpDown>("VpRangeBarsSpin");
            var lockRangeCheck = dialog2.FindControl<CheckBox>("VpLockRangeCheck");
            var okButton = dialog2.FindControl<Button>("OkButton");

            combo!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.False(rangeBarsSpin!.IsEnabled);
            Assert.False(lockRangeCheck!.IsEnabled);

            okButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(VolumeProfileRepeatMode.Monthly, frvp2.RepeatMode);
        }
        finally
        {
            dialog2.Close();
            frvp2.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_OrdersCommonControlsAboveToolSpecificPanels_ForKalmanProjection()
    {
        var kalman = new StockAnalyzer.Avalonia.Drawing.Objects.KalmanFilterProjectionObject();
        kalman.Points.Add(new ChartPoint(DateTime.Now, 100m));
        kalman.Points.Add(new ChartPoint(DateTime.Now, 110m));

        var dialog = new DrawingSettingsDialog(kalman, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var generalSection = dialog.FindControl<StackPanel>("GeneralSettingsSection");
            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection");
            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var kalmanPanel = dialog.FindControl<StackPanel>("KalmanFilterProjectionPanel");
            var fillPanel = dialog.FindControl<StackPanel>("KalmanFillOpacityPanel");

            Assert.NotNull(contentPanel);
            Assert.NotNull(generalSection);
            Assert.NotNull(specificSection);
            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(kalmanPanel);
            Assert.NotNull(fillPanel);

            Assert.True(genericColorPanel!.IsVisible);
            Assert.True(thicknessPanel!.IsVisible);
            Assert.True(kalmanPanel!.IsVisible);
            Assert.True(fillPanel!.IsVisible);

            int colorIndex = generalSection!.Children.IndexOf(genericColorPanel);
            int thicknessIndex = generalSection.Children.IndexOf(thicknessPanel);
            int kalmanIndex = specificSection!.Children.IndexOf(kalmanPanel);
            int fillIndex = specificSection.Children.IndexOf(fillPanel);

            // Common controls (Color and Thickness) appear consecutively in General, and tool settings in Specific
            Assert.True(colorIndex < thicknessIndex);
            Assert.True(kalmanIndex < fillIndex);
            int generalSectionIndex = contentPanel!.Children.IndexOf(generalSection);
            int specificSectionIndex = contentPanel.Children.IndexOf(specificSection);
            Assert.True(generalSectionIndex < specificSectionIndex);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_OrdersCommonControlsAboveDynamicSettings_ForFallbackTriangleObject()
    {
        var triangle = new TriangleObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now, 110m),
            new ChartPoint(DateTime.Now, 95m));

        var dialog = new DrawingSettingsDialog(triangle, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var contentPanel = dialog.FindControl<StackPanel>("SettingsContentPanel");
            var generalSection = dialog.FindControl<StackPanel>("GeneralSettingsSection");
            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection");
            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var dynamicView = dialog.FindControl<StockAnalyzer.Avalonia.Views.Controls.DynamicDrawingSettingsView>("DynamicSettingsView");

            Assert.NotNull(contentPanel);
            Assert.NotNull(generalSection);
            Assert.NotNull(specificSection);
            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(dynamicView);

            Assert.True(genericColorPanel!.IsVisible);
            Assert.True(thicknessPanel!.IsVisible);
            Assert.True(dynamicView!.IsVisible);

            int colorIndex = generalSection!.Children.IndexOf(genericColorPanel);
            int thicknessIndex = generalSection.Children.IndexOf(thicknessPanel);
            int dynamicIndex = specificSection!.Children.IndexOf(dynamicView);

            // Common controls (Color and Thickness) appear in General, dynamic settings in Specific
            Assert.True(colorIndex < thicknessIndex);
            int generalSectionIndex = contentPanel!.Children.IndexOf(generalSection);
            int specificSectionIndex = contentPanel.Children.IndexOf(specificSection);
            Assert.True(generalSectionIndex < specificSectionIndex);
        }
        finally
        {
            dialog.Close();
            triangle.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_RespectsSupportsStrokeColorAndThickness_ForIconObject()
    {
        var icon = new IconObject();

        var dialog = new DrawingSettingsDialog(icon, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var iconPanel = dialog.FindControl<StackPanel>("IconSettingsPanel");

            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(iconPanel);

            Assert.False(genericColorPanel!.IsVisible);
            Assert.False(thicknessPanel!.IsVisible);
            Assert.True(iconPanel!.IsVisible);
        }
        finally
        {
            dialog.Close();
            icon.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_RespectsSupportsStrokeColorAndThickness_ForGeometricPatternObject()
    {
        var geom = new StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject();

        var dialog = new DrawingSettingsDialog(geom, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var geomPanel = dialog.FindControl<StackPanel>("GeometricPatternPanel");

            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(geomPanel);

            Assert.False(genericColorPanel!.IsVisible);
            Assert.False(thicknessPanel!.IsVisible);
            Assert.True(geomPanel!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_RespectsSupportsStrokeColorAndThickness_ForLongShortPositionObject()
    {
        var ls = new LongShortPositionObject();

        var dialog = new DrawingSettingsDialog(ls, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var lsPanel = dialog.FindControl<StackPanel>("LongShortPanel");

            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(lsPanel);

            Assert.False(genericColorPanel!.IsVisible);
            Assert.False(thicknessPanel!.IsVisible);
            Assert.True(lsPanel!.IsVisible);
        }
        finally
        {
            dialog.Close();
            ls.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_CategorySelection_SwitchesBetweenGeneralAndSpecificSections()
    {
        var triangle = new TriangleObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now, 110m),
            new ChartPoint(DateTime.Now, 95m));

        var dialog = new DrawingSettingsDialog(triangle, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var categoryListBox = dialog.FindControl<ListBox>("CategoryListBox");
            var generalSection = dialog.FindControl<StackPanel>("GeneralSettingsSection");
            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection");

            Assert.NotNull(categoryListBox);
            Assert.NotNull(generalSection);
            Assert.NotNull(specificSection);

            // Default selection is General (index 0)
            Assert.Equal(0, categoryListBox!.SelectedIndex);
            Assert.True(generalSection!.IsVisible);
            Assert.False(specificSection!.IsVisible);

            // Switch to Specific (index 1)
            categoryListBox.SelectedIndex = 1;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.False(generalSection.IsVisible);
            Assert.True(specificSection.IsVisible);

            // Switch back to General (index 0)
            categoryListBox.SelectedIndex = 0;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(generalSection.IsVisible);
            Assert.False(specificSection.IsVisible);
        }
        finally
        {
            dialog.Close();
            triangle.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_LineTextObject_SplitsGeneralAndSpecificPanelsCorrectly()
    {
        var lineText = new LineTextObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now, 110m));

        var dialog = new DrawingSettingsDialog(lineText, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var categoryListBox = dialog.FindControl<ListBox>("CategoryListBox");
            var generalSection = dialog.FindControl<StackPanel>("GeneralSettingsSection");
            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection");
            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var lineTextCommonPanel = dialog.FindControl<StackPanel>("LineTextCommonPanel");
            var lineTextPanel = dialog.FindControl<StackPanel>("LineTextPanel");
            var lineTextTopColumnPanel = dialog.FindControl<StackPanel>("LineTextTopColumnPanel");
            var lineTextBottomColumnPanel = dialog.FindControl<StackPanel>("LineTextBottomColumnPanel");

            Assert.NotNull(categoryListBox);
            Assert.NotNull(generalSection);
            Assert.NotNull(specificSection);
            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(lineTextCommonPanel);
            Assert.NotNull(lineTextPanel);
            Assert.NotNull(lineTextTopColumnPanel);
            Assert.NotNull(lineTextBottomColumnPanel);

            // 1. In General view (index 0):
            Assert.Equal(0, categoryListBox!.SelectedIndex);
            Assert.True(generalSection!.IsVisible);
            Assert.False(specificSection!.IsVisible);

            // Common controls & transparent checkbox are visible
            Assert.True(genericColorPanel!.IsVisible);
            Assert.True(thicknessPanel!.IsVisible);
            Assert.True(lineTextCommonPanel!.IsVisible);

            // 2. Switch to Specific view (index 1):
            categoryListBox.SelectedIndex = 1;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.False(generalSection.IsVisible);
            Assert.True(specificSection.IsVisible);

            // Specific controls (Top and Bottom annotation cards) are visible
            Assert.True(lineTextPanel!.IsVisible);
            Assert.True(lineTextTopColumnPanel!.IsVisible);
            Assert.True(lineTextBottomColumnPanel!.IsVisible);
        }
        finally
        {
            dialog.Close();
            lineText.Dispose();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_CurveLineTextObject_SplitsGeneralAndSpecificPanelsCorrectly()
    {
        var curveLineText = new CurveLineTextObject();

        var dialog = new DrawingSettingsDialog(curveLineText, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var categoryListBox = dialog.FindControl<ListBox>("CategoryListBox");
            var generalSection = dialog.FindControl<StackPanel>("GeneralSettingsSection");
            var specificSection = dialog.FindControl<StackPanel>("SpecificSettingsSection");
            var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
            var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
            var lineTextCommonPanel = dialog.FindControl<StackPanel>("LineTextCommonPanel");
            var lineTextPanel = dialog.FindControl<StackPanel>("LineTextPanel");
            var lineTextTopColumnPanel = dialog.FindControl<StackPanel>("LineTextTopColumnPanel");
            var lineTextBottomColumnPanel = dialog.FindControl<StackPanel>("LineTextBottomColumnPanel");

            Assert.NotNull(categoryListBox);
            Assert.NotNull(generalSection);
            Assert.NotNull(specificSection);
            Assert.NotNull(genericColorPanel);
            Assert.NotNull(thicknessPanel);
            Assert.NotNull(lineTextCommonPanel);
            Assert.NotNull(lineTextPanel);
            Assert.NotNull(lineTextTopColumnPanel);
            Assert.NotNull(lineTextBottomColumnPanel);

            // 1. In General view (index 0):
            Assert.Equal(0, categoryListBox!.SelectedIndex);
            Assert.True(generalSection!.IsVisible);
            Assert.False(specificSection!.IsVisible);

            // Common controls & transparent checkbox are visible
            Assert.True(genericColorPanel!.IsVisible);
            Assert.True(thicknessPanel!.IsVisible);
            Assert.True(lineTextCommonPanel!.IsVisible);

            // 2. Switch to Specific view (index 1):
            categoryListBox.SelectedIndex = 1;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.False(generalSection.IsVisible);
            Assert.True(specificSection.IsVisible);

            // Specific controls (Top and Bottom annotation cards) are visible
            Assert.True(lineTextPanel!.IsVisible);
            Assert.True(lineTextTopColumnPanel!.IsVisible);
            Assert.True(lineTextBottomColumnPanel!.IsVisible);
        }
        finally
        {
            dialog.Close();
            curveLineText.Dispose();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_LayoutStandardization_WidthAndRowLayout_Verified()
    {
        var ellipse = new EllipseObject(
            new ChartPoint(DateTime.Now, 100m),
            new ChartPoint(DateTime.Now.AddDays(5), 120m));

        var dialog = new DrawingSettingsDialog(ellipse, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // 1. Verify Transparent label is updated
            var transparentCheck = dialog.FindControl<CheckBox>("LineTextTransparentCheck");
            Assert.NotNull(transparentCheck);
            var transparentGrid = Assert.IsType<Grid>(transparentCheck.Parent);
            var transparentText = Assert.Single(transparentGrid.Children.OfType<TextBlock>());
            Assert.Equal("Transparent", transparentText.Text);

            // 2. Verify ThicknessSpin width is 120 (4+ digits)
            var thicknessSpin = dialog.FindControl<NumericUpDown>("ThicknessSpin");
            Assert.NotNull(thicknessSpin);
            Assert.Equal(120, thicknessSpin.Width);

            // 3. Verify Ellipse AspectRatioSpin width is 120 and in 1-row layout (*, Auto)
            var aspectRatioSpin = dialog.FindControl<NumericUpDown>("EllipseAspectRatioSpin");
            Assert.NotNull(aspectRatioSpin);
            Assert.Equal(120, aspectRatioSpin.Width);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Right, aspectRatioSpin.HorizontalAlignment);

            var parentGrid = Assert.IsType<Grid>(aspectRatioSpin.Parent);
            Assert.Equal(2, parentGrid.ColumnDefinitions.Count);
            Assert.Equal(GridLength.Star, parentGrid.ColumnDefinitions[0].Width);
            Assert.Equal(GridLength.Auto, parentGrid.ColumnDefinitions[1].Width);
            Assert.Contains(parentGrid.Children, c => c is TextBlock);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_DynamicTargetPriceProjection_StandardizedLayout_Verified()
    {
        var targetProjection = new TargetPriceProjectionObject();

        var dialog = new DrawingSettingsDialog(targetProjection, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var dynamicView = dialog.FindControl<StockAnalyzer.Avalonia.Views.Controls.DynamicDrawingSettingsView>("DynamicSettingsView");
            Assert.NotNull(dynamicView);
            Assert.True(dynamicView.IsVisible);

            var stackPanel = Assert.IsType<StackPanel>(dynamicView.Content);
            var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
            Assert.Equal(2, cards.Count);

            // Verify no vertical cell divider borders or header borders exist
            var allDescendantBorders = cards
                .Select(b => b.Child)
                .OfType<StackPanel>()
                .SelectMany(p => p.Children)
                .OfType<Border>()
                .ToList();
            Assert.Empty(allDescendantBorders);

            // Checkboxes are unified
            var checkboxes = cards
                .Select(b => b.Child)
                .OfType<StackPanel>()
                .SelectMany(p => p.Children.OfType<CheckBox>())
                .ToList();
            Assert.Equal(5, checkboxes.Count);
        }
        finally
        {
            dialog.Close();
            targetProjection.Dispose();
        }
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_ColorPicker_RightEdgeAlignedWithNumericUpDown_AndMinWidthZero()
    {
        using var line = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));
        var dialog = new DrawingSettingsDialog(line, CreateRegistry());
        try
        {
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var cp = dialog.FindControl<ColorPicker>("ColorPickerControl");
            var nud = dialog.FindControl<NumericUpDown>("ThicknessSpin");

            Assert.NotNull(cp);
            Assert.NotNull(nud);

            // MinWidth must be 0 to prevent Fluent Theme's default MinWidth=64 from adding 14px extra invisible right gap
            Assert.Equal(0.0, cp.MinWidth);
            Assert.Equal(50.0, cp.Width);
            Assert.Equal(50.0, cp.Bounds.Width);

            // ColorPicker's right edge must match NumericUpDown's right edge
            Assert.Equal(nud.Bounds.Right, cp.Bounds.Right);
        }
        finally
        {
            dialog.Close();
        }
    }
}



