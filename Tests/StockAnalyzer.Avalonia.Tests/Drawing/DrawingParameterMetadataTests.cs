using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

[Collection("LocalizationSharedState")]
public class DrawingParameterMetadataTests
{
    public DrawingParameterMetadataTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    private class TestComplexDrawingObject : TrendLineObject
    {
        public TestComplexDrawingObject()
            : base(new ChartPoint(new DateTime(2025, 1, 1), 100m), new ChartPoint(new DateTime(2025, 1, 2), 110m))
        {
        }

        [Category("Style")]
        [DisplayName("Tags")]
        public List<string> Tags { get; set; } = new() { "Alpha", "Beta" };

        [Category("Style")]
        [DisplayName("Values")]
        public int[] Values { get; set; } = new[] { 10, 20, 30 };
    }

    private class OrderedParametersSample
    {
        [Category("Volume Profile")]
        [DisplayName("Volume Width")]
        public int VolumeWidth { get; set; } = 100;

        [Category("Style")]
        [DisplayName("First Item")]
        [Display(Order = 1)]
        public int First { get; set; } = 1;

        [Category("Style")]
        [DisplayName("Third Item")]
        [Display(Order = 10)]
        public int Third { get; set; } = 3;

        [Category("Style")]
        [DisplayName("Second Item")]
        [Display(Order = 5)]
        public int Second { get; set; } = 2;

        [Category("Typography")]
        [DisplayName("Font Family")]
        public string FontFamily { get; set; } = "Arial";
    }

    private class AdvancedTypeParametersSample
    {
        [Category("Style")]
        [DisplayName("Nullable Accent Color")]
        public Color? OptionalColor { get; set; } = Colors.Red;

        [Category("Typography")]
        [DisplayName("Multiline Description")]
        [DataType(DataType.MultilineText)]
        public string Description { get; set; } = "Line 1\nLine 2";
    }
    [Fact]
    public void DrawingParameterTags_Constants_AreDefinedAndUnique()
    {
        var tags = new[]
        {
            DrawingParameterTags.Common,
            DrawingParameterTags.Fill,
            DrawingParameterTags.Typography,
            DrawingParameterTags.Geometry,
            DrawingParameterTags.Projection,
            DrawingParameterTags.RiskReward,
            DrawingParameterTags.Analysis,
            DrawingParameterTags.VolumeProfile
        };

        Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(tags.Length, tags.Distinct().Count());
    }

    private class SampleDrawingParameters
    {
        [Category("Style")]
        [DisplayName("Main Color")]
        [ParameterTag(DrawingParameterTags.Common)]
        public Color LineColor { get; set; } = Colors.Red;

        [Category("Style")]
        [DisplayName("Line Thickness")]
        [Range(0.1, 10.0)]
        [ParameterTag(DrawingParameterTags.Common)]
        public double Thickness { get; set; } = 2.0;

        [Category("Fill")]
        [DisplayName("Fill Color")]
        [ParameterTag(DrawingParameterTags.Fill)]
        public SKColor FillColor { get; set; } = SKColors.Blue;

        [Category("Fill")]
        [DisplayName("Fill HSV")]
        [ParameterTag(DrawingParameterTags.Fill)]
        public HsvData FillHsv { get; set; } = new HsvData(1.0, 180.0, 0.5, 0.8);

        [Category("Options")]
        [DisplayName("Is Visible")]
        public bool IsVisible { get; set; } = true;

        [Category("Text")]
        [DisplayName("Note")]
        [ParameterTag(DrawingParameterTags.Typography)]
        public string NoteText { get; set; } = "Sample Annotation";

        // Unannotated or internal properties should be ignored
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_BuildsControls_ForDrawingTypes()
    {
        var builder = new DrawingParameterViewBuilder();
        var sample = new SampleDrawingParameters();

        var control = builder.Build(sample);
        Assert.NotNull(control);
        var stackPanel = Assert.IsType<StackPanel>(control);

        Assert.True(stackPanel.Children.Count > 0);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_BooleanRowsUseOneFullWidthCheckboxWithoutLabelDivider()
    {
        var builder = new DrawingParameterViewBuilder();
        var triangle = new TriangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m),
            new ChartPoint(new DateTime(2025, 1, 3), 105m));

        var stackPanel = Assert.IsType<StackPanel>(builder.Build(triangle));
        var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
        Assert.NotEmpty(cards);

        var booleanRows = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<CheckBox>())
            .ToList();

        Assert.Equal(7, booleanRows.Count);
        foreach (var checkBox in booleanRows)
        {
            Assert.False(string.IsNullOrWhiteSpace(checkBox.Content?.ToString()));

            if (global::Avalonia.Application.Current?.FindResource("DetailFontSize") is double detailFontSize)
            {
                Assert.Equal(detailFontSize, checkBox.FontSize);
            }
        }

        // Verify Analysis category header band is not generated
        Assert.DoesNotContain(cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<TextBlock>()), textBlock => textBlock.Text == "Analysis");

        var sampleStack = Assert.IsType<StackPanel>(builder.Build(new SampleDrawingParameters()));
        var sampleCards = sampleStack.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
        var ordinaryCheckBox = Assert.Single(sampleCards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<CheckBox>()));

        Assert.Equal(LocalizationManager.Instance.Get("Param_IsVisible"), ordinaryCheckBox.Content?.ToString());
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_FiltersBy_HiddenTags()
    {
        var builder = new DrawingParameterViewBuilder();
        var sample = new SampleDrawingParameters();

        var control = builder.Build(sample, new[] { DrawingParameterTags.Fill });
        Assert.NotNull(control);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var labels = ExtractLabels(stackPanel);

        Assert.Contains("Main Color", labels);
        Assert.DoesNotContain("Fill Color", labels);
        Assert.DoesNotContain("Fill HSV", labels);
    }

    [AvaloniaFact]
    public void DrawingObjectSettingsViewModel_TracksModification_AndRollsBackOnCancel()
    {
        var line = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m))
        {
            Color = Colors.Red,
            Thickness = 1.0,
            ShowProjection = false
        };

        bool applied = false;
        DrawingSettingsResult? dialogResult = null;

        var vm = new DrawingObjectSettingsViewModel(line, obj => applied = true);
        vm.CloseRequested += res => dialogResult = res;

        Assert.False(vm.IsModified);
        Assert.Equal(Colors.Red, line.Color);

        // 1. Modify property
        line.Color = Colors.Green;
        line.Thickness = 3.5;
        line.ShowProjection = true;
        vm.UpdateIsModified();

        Assert.True(vm.IsModified);

        // 2. Test Apply
        vm.ApplyCommand.Execute(null);
        Assert.True(applied);
        Assert.False(vm.IsModified); // Snapshot refreshed
        Assert.Null(dialogResult); // Dialog remains open

        // 3. Modify again and Cancel (Rollback)
        line.Color = Colors.Blue;
        line.Thickness = 5.0;
        vm.UpdateIsModified();
        Assert.True(vm.IsModified);

        vm.CancelCommand.Execute(null);
        Assert.Equal(DrawingSettingsResult.None, dialogResult);
        Assert.Equal(Colors.Green, line.Color); // Restored to last snapshot
        Assert.Equal(3.5, line.Thickness);
        Assert.False(vm.IsModified);
    }

    [AvaloniaFact]
    public void DrawingObjectSettingsViewModel_OkCommand_RaisesChangedResult()
    {
        var line = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        DrawingSettingsResult? dialogResult = null;
        var vm = new DrawingObjectSettingsViewModel(line);
        vm.CloseRequested += res => dialogResult = res;

        vm.OkCommand.Execute(null);
        Assert.Equal(DrawingSettingsResult.Changed, dialogResult);
    }

    [AvaloniaFact]
    public void DrawingObjectSettingsViewModel_DeleteCommand_RaisesDeletedResult()
    {
        var line = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        DrawingSettingsResult? dialogResult = null;
        var vm = new DrawingObjectSettingsViewModel(line);
        vm.CloseRequested += res => dialogResult = res;

        vm.DeleteCommand.Execute(null);
        Assert.Equal(DrawingSettingsResult.Deleted, dialogResult);
    }

    [Fact]
    public void Localization_DrawingCategoriesAndParams_ExistInBothLocales()
    {
        var enPath = @"i:\stock\StockAnalyzer.Avalonia\Resources\Locales\en.json";
        var jaPath = @"i:\stock\StockAnalyzer.Avalonia\Resources\Locales\ja.json";

        var enJson = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(enPath)).RootElement;
        var jaJson = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(jaPath)).RootElement;

        var requiredKeys = new[]
        {
            "Category_Style",
            "Category_FillStyle",
            "Category_Typography",
            "Category_Coordinates",
            "Category_Geometry",
            "Category_Projection",
            "Category_RiskReward",
            "Category_Analysis",
            "Category_VolumeProfile",
            "Param_Color",
            "Param_Thickness",
            "Param_IsVisible",
            "Param_IsLocked",
            "Param_ZIndex",
            "Param_MoveAxisMode",
            "Param_IsFilled",
            "Param_FillColor",
            "Param_FillOpacity",
            "Param_TextColor",
            "Param_FontSize",
            "Param_Text",
            "Common_Enable",
            "Msg_NoParameters"
        };

        foreach (var key in requiredKeys)
        {
            Assert.True(enJson.TryGetProperty(key, out var enProp), $"Missing '{key}' in en.json");
            Assert.False(string.IsNullOrWhiteSpace(enProp.GetString()), $"Empty '{key}' in en.json");

            Assert.True(jaJson.TryGetProperty(key, out var jaProp), $"Missing '{key}' in ja.json");
            Assert.False(string.IsNullOrWhiteSpace(jaProp.GetString()), $"Empty '{key}' in ja.json");
        }
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_BuildsControls_ForConcreteDrawingObjects()
    {
        var builder = new DrawingParameterViewBuilder();

        // 1. TrendLineObject
        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        var trendControl = builder.Build(trendLine);
        Assert.NotNull(trendControl);
        var trendStack = Assert.IsType<StackPanel>(trendControl);
        var trendLabels = ExtractLabels(trendStack);

        Assert.Contains("Color", trendLabels);
        Assert.Contains(LocalizationManager.Instance.Get("Param_Thickness"), trendLabels);
        Assert.Contains("Visible", trendLabels);
        Assert.Contains("Locked", trendLabels);
        Assert.Contains("Z-Index", trendLabels);
        Assert.Contains("Show Projection", trendLabels);
        Assert.Contains("Projection Columns", trendLabels);

        // 2. RectangleObject
        var rect = new RectangleObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        var rectControl = builder.Build(rect);
        Assert.NotNull(rectControl);
        var rectStack = Assert.IsType<StackPanel>(rectControl);
        var rectLabels = ExtractLabels(rectStack);

        Assert.Contains("Color", rectLabels);
        Assert.Contains(LocalizationManager.Instance.Get("Param_IsFilled"), rectLabels);
        Assert.Contains("Blend Mode", rectLabels);

        // 3. TextObject
        var text = new TextObject(new ChartPoint(new DateTime(2025, 1, 1), 100m));
        var textControl = builder.Build(text);
        Assert.NotNull(textControl);
        var textStack = Assert.IsType<StackPanel>(textControl);
        var textLabels = ExtractLabels(textStack);

        Assert.Contains("Text", textLabels);
        Assert.Contains("Font Size", textLabels);
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_FallsBackTo_DynamicSettingsView_WhenNoPanelDefinition()
    {
        var line = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 110m));

        var emptyRegistry = new DrawingSettingsPanelRegistry();

        var dialog = new DrawingSettingsDialog(line, emptyRegistry);

        var dynamicView = dialog.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");
        Assert.NotNull(dynamicView);
        Assert.True(dynamicView.IsVisible);
        Assert.Same(line, dynamicView.ParameterObject);

        var genericColorPanel = dialog.FindControl<StackPanel>("GenericColorPanel");
        Assert.NotNull(genericColorPanel);
        Assert.True(genericColorPanel.IsVisible);

        var thicknessPanel = dialog.FindControl<StackPanel>("ThicknessPanel");
        Assert.NotNull(thicknessPanel);
        Assert.True(thicknessPanel.IsVisible);
    }

    [Fact]
    public void DrawingParameterViewBuilder_MetadataCache_ReusesCachedDescriptors()
    {
        var meta1 = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(SampleDrawingParameters));
        var meta2 = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(SampleDrawingParameters));

        Assert.NotNull(meta1);
        Assert.Same(meta1, meta2);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_DeterministicCategoryAndPropertyOrder()
    {
        var builder = new DrawingParameterViewBuilder();
        var sample = new OrderedParametersSample();

        var control = builder.Build(sample);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
        Assert.Equal(3, cards.Count);

        // Check property order by category rank and Display(Order):
        // Style (rank 0: First, Second, Third) -> Typography (rank 20: Font Family) -> Volume Profile (rank 70: Volume Width)
        var labels = ExtractLabels(stackPanel);
        Assert.Equal(new[] { "First Item", "Second Item", "Third Item", "Font Family", "Volume Width" }, labels);

        // Verify category headers are not generated as separate TextBlocks inside cards
        var allTextBlocks = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<TextBlock>())
            .Select(t => t.Text)
            .ToList();

        Assert.DoesNotContain("Style", allTextBlocks);
        Assert.DoesNotContain("Typography", allTextBlocks);
        Assert.DoesNotContain("Volume Profile", allTextBlocks);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_SupportsNullableColor_AndMultilineText()
    {
        var builder = new DrawingParameterViewBuilder();
        var sample = new AdvancedTypeParametersSample();

        var control = builder.Build(sample);
        var stackPanel = Assert.IsType<StackPanel>(control);

        var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();

        // Find Nullable Color control (StackPanel containing CheckBox and ColorPicker) in Grid row
        var colorRow = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Grid>())
            .FirstOrDefault(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "Nullable Accent Color"));

        Assert.NotNull(colorRow);
        var colorPanel = colorRow.Children.OfType<StackPanel>().FirstOrDefault();
        Assert.NotNull(colorPanel);
        Assert.Contains(colorPanel.Children, c => c is CheckBox);
        Assert.Contains(colorPanel.Children, c => c is ColorPicker);

        // Find Multiline TextBox (in StackPanel multi-line row)
        var textRow = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<StackPanel>())
            .FirstOrDefault(p => p.Children.OfType<TextBlock>().Any(t => t.Text == "Multiline Description"));

        Assert.NotNull(textRow);
        var textBox = textRow.Children.OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        Assert.True(textBox.AcceptsReturn);
        Assert.Equal(TextWrapping.Wrap, textBox.TextWrapping);
    }

    [AvaloniaFact]
    public void DrawingObjectSettingsViewModel_DeepClonesReferenceTypes_AndRollsBackOnCancel()
    {
        var obj = new TestComplexDrawingObject();
        var vm = new DrawingObjectSettingsViewModel(obj);

        Assert.False(vm.IsModified);

        // Mutate list elements in-place
        obj.Tags.Add("Gamma");
        vm.UpdateIsModified();
        Assert.True(vm.IsModified);

        // Rollback on Cancel
        vm.CancelCommand.Execute(null);
        Assert.Equal(new[] { "Alpha", "Beta" }, obj.Tags);
        Assert.False(vm.IsModified);

        // Mutate array elements
        obj.Values[0] = 999;
        vm.UpdateIsModified();
        Assert.True(vm.IsModified);

        // Rollback on Cancel
        vm.CancelCommand.Execute(null);
        Assert.Equal(new[] { 10, 20, 30 }, obj.Values);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void DrawingParameterTags_IgnoredPropertyNames_ContainsInfrastructureProperties()
    {
        var ignored = DrawingParameterTags.IgnoredPropertyNames;
        Assert.Contains("Id", ignored);
        Assert.Contains("Points", ignored);
        Assert.Contains("IsSelected", ignored);
        Assert.Contains("SkiaColor", ignored);
        Assert.Contains("id", ignored); // Case-insensitive
    }

    [AvaloniaFact]
    public void DrawingSettingsDialog_ParameterlessConstructor_InitializesSafelyWithoutThrowing()
    {
        var dialog = new DrawingSettingsDialog();
        Assert.NotNull(dialog);
        Assert.Equal("Drawing Settings", dialog.Title);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_TargetPriceProjection_UsesStandardCardsAndRowLayoutWithoutDividers()
    {
        var builder = new DrawingParameterViewBuilder();
        var targetProjection = new TargetPriceProjectionObject();

        var stackPanel = Assert.IsType<StackPanel>(builder.Build(targetProjection));
        var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
        Assert.Equal(2, cards.Count); // Appearance card & Projection Targets card

        // Verify no vertical cell divider borders or header borders exist anywhere in the view
        var allDescendantBorders = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children)
            .OfType<Border>()
            .ToList();
        Assert.Empty(allDescendantBorders);

        // 1. Projection Targets Card (Contains 5 CheckBoxes in standard single-row layout)
        var targetCheckboxes = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<CheckBox>())
            .ToList();

        Assert.Equal(5, targetCheckboxes.Count);
        foreach (var checkBox in targetCheckboxes)
        {
            Assert.False(string.IsNullOrWhiteSpace(checkBox.Content?.ToString()));
        }

        // 2. Appearance Card (Contains Font Size and Font Color in standard 1-row Grid layout)
        var appearanceRows = cards
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Grid>())
            .ToList();

        Assert.Equal(2, appearanceRows.Count);
        var fontSizeRow = appearanceRows.Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == "Font Size"));
        var fontSizeSpin = Assert.Single(fontSizeRow.Children.OfType<NumericUpDown>());
        Assert.Equal(120, fontSizeSpin.Width);
        Assert.Equal("0", fontSizeSpin.FormatString);
        Assert.Equal(1.0, (double)fontSizeSpin.Increment);

        var fontColorRow = appearanceRows.Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == "Font Color"));
        var fontColorPicker = Assert.Single(fontColorRow.Children.OfType<ColorPicker>());
        Assert.Equal(50, fontColorPicker.Width);
        Assert.Equal(0.0, fontColorPicker.MinWidth);
    }

    private static System.Collections.Generic.List<string> ExtractLabels(StackPanel stackPanel)
    {
        var labels = new System.Collections.Generic.List<string>();

        // Find all cards
        var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard"));
        foreach (var card in cards)
        {
            if (card.Child is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Grid grid)
                    {
                        var text = grid.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
                        if (!string.IsNullOrEmpty(text)) labels.Add(text);
                    }
                    else if (child is CheckBox cb)
                    {
                        var text = cb.Content?.ToString();
                        if (!string.IsNullOrEmpty(text)) labels.Add(text);
                    }
                    else if (child is StackPanel sp) // multiline row
                    {
                        var text = sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
                        if (!string.IsNullOrEmpty(text)) labels.Add(text);
                    }
                }
            }
        }

        return labels;
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_TargetPriceProjection_FontSize_IntegerFormatAndRange()
    {
        var builder = new DrawingParameterViewBuilder();
        var targetProjection = new TargetPriceProjectionObject();

        var stackPanel = Assert.IsType<StackPanel>(builder.Build(targetProjection));
        var window = new Window { Content = stackPanel };
        window.Show();
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var cards = stackPanel.Children.OfType<Border>().Where(b => b.Classes.Contains("SettingsCard")).ToList();
            var appearanceRows = cards
                .Select(b => b.Child)
                .OfType<StackPanel>()
                .SelectMany(p => p.Children.OfType<Grid>())
                .ToList();

            var fontSizeRow = appearanceRows.Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == "Font Size"));
            var fontSizeSpin = Assert.Single(fontSizeRow.Children.OfType<NumericUpDown>());

            Assert.Equal("0", fontSizeSpin.FormatString);
            Assert.Equal(1.0, (double)fontSizeSpin.Increment);
            Assert.Equal(8.0, (double)fontSizeSpin.Minimum);
            Assert.Equal(72.0, (double)fontSizeSpin.Maximum);

            fontSizeSpin.SetCurrentValue(NumericUpDown.ValueProperty, 16m);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(16.0, targetProjection.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_minimal_fix: the numeric editor's binding source (NumericParameterAdapter) is held only
    /// weakly by Avalonia, so once nothing else referenced it a GC made every later edit fail with
    /// "Non-static method requires a target" and the drawing parameter was silently not updated.
    /// This forces the collection deterministically instead of relying on GC timing.</summary>
    [AvaloniaFact]
    public void DrawingParameterViewBuilder_NumericEdit_StillWritesThrough_AfterGarbageCollection()
    {
        var builder = new DrawingParameterViewBuilder();
        var targetProjection = new TargetPriceProjectionObject();
        var stackPanel = Assert.IsType<StackPanel>(builder.Build(targetProjection));
        var window = new Window { Content = stackPanel };
        window.Show();
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var fontSizeLabel = LocalizationManager.Instance.Get("Param_FontSize");
            var fontSizeRow = stackPanel.Children.OfType<Border>()
                .Where(b => b.Classes.Contains("SettingsCard"))
                .Select(b => b.Child)
                .OfType<StackPanel>()
                .SelectMany(p => p.Children.OfType<Grid>())
                .Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == fontSizeLabel));
            var fontSizeSpin = Assert.Single(fontSizeRow.Children.OfType<NumericUpDown>());

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var newValue = fontSizeSpin.Maximum - 1m;
            fontSizeSpin.SetCurrentValue(NumericUpDown.ValueProperty, newValue);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal((double)newValue, targetProjection.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void TargetPriceProjection_Render_AppliesFontSizeAndFontColorToTextPaint()
    {
        using var obj = new TargetPriceProjectionObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 2), 200m),
            new ChartPoint(new DateTime(2025, 1, 3), 150m))
        {
            FontSize = 24.0,
            FontColor = Colors.DeepPink
        };

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        var transform = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 10),
            0m, 300m,
            800, 600);

        obj.Render(canvas, transform);

        Assert.Equal(24.0f, obj.TextPaintForTesting.TextSize);
        Assert.Equal(new SKColor(Colors.DeepPink.R, Colors.DeepPink.G, Colors.DeepPink.B, Colors.DeepPink.A), obj.TextPaintForTesting.Color);
    }
}
