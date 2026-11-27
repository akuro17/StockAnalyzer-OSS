using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Guards the generic (dynamic) drawing settings dialog: a parameter tagged <c>Common</c> or <c>Geometry</c> is hidden there (<see cref="DrawingSettingsDialog.FallbackHiddenTags"/>), which is how the
/// GARCH cone fill opacity and the Regular Polygon parameters once went missing. The range-analysis tools, the interpolation curves and the Regular Polygon must show every parameter, label it and
/// localise it.
/// </summary>
[Collection("LocalizationSharedState")]
public class DrawingToolSettingsVisibilityTests
{
    public DrawingToolSettingsVisibilityTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    private sealed class NullableFillSample
    {
        [System.ComponentModel.DataAnnotations.Display(Order = 1)]
        [DisplayName("Fill Color")]
        public Color? FillColor { get; set; }
    }

    private static IReadOnlyList<Type> DynamicViewTools()
    {
        return typeof(RegularPolygonObject).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
            .Where(t => typeof(IRangeAnalysisDrawing).IsAssignableFrom(t) || typeof(InterpolationCurveObjectBase).IsAssignableFrom(t) || t == typeof(RegularPolygonObject))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsLocalised(string key) => LocalizationManager.Instance.Get(key) is { Length: > 0 } text && !(text.StartsWith('[') && text.EndsWith(']'));

    [Fact]
    public void FallbackHiddenTags_AreCommonAndGeometry()
    {
        Assert.Equal(new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry }, DrawingSettingsDialog.FallbackHiddenTags);
    }

    [Fact]
    public void DynamicViewTools_AreFound()
    {
        IReadOnlyList<Type> tools = DynamicViewTools();
        Assert.Contains(typeof(GarchVolatilityConeObject), tools);
        Assert.Contains(typeof(RegularPolygonObject), tools);
        Assert.Contains(typeof(PchipCurveObject), tools);
        Assert.True(tools.Count >= 10, "range-analysis and curve tools: " + tools.Count);
    }

    [Fact]
    public void RangeAnalysisCurveAndPolygonTools_ShowEveryParameterInTheFallbackDialog()
    {
        var hidden = new List<string>();
        foreach (Type tool in DynamicViewTools())
        {
            foreach (DrawingParameterViewBuilder.PropertyMetadata meta in DrawingParameterViewBuilder.GetOrReflectMetadata(tool))
            {
                if (meta.Tags.Any(t => DrawingSettingsDialog.FallbackHiddenTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    hidden.Add(tool.Name + "." + meta.Prop.Name);
                }
            }
        }

        Assert.True(hidden.Count == 0, "hidden in the generic dialog: " + string.Join(", ", hidden));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void RangeAnalysisCurveAndPolygonTools_LabelAndLocaliseEveryParameter(string language)
    {
        LocalizationManager.Instance.Initialize(language);
        try
        {
            var problems = new List<string>();
            foreach (Type tool in DynamicViewTools())
            {
                foreach (DrawingParameterViewBuilder.PropertyMetadata meta in DrawingParameterViewBuilder.GetOrReflectMetadata(tool))
                {
                    string name = tool.Name + "." + meta.Prop.Name;
                    if (meta.Prop.GetCustomAttributes(typeof(DisplayNameAttribute), true).Length == 0)
                    {
                        problems.Add(name + ": no [DisplayName]");
                    }

                    if (!IsLocalised("Param_" + meta.Prop.Name))
                    {
                        problems.Add(name + ": no Param_" + meta.Prop.Name);
                    }

                    Type type = Nullable.GetUnderlyingType(meta.Prop.PropertyType) ?? meta.Prop.PropertyType;
                    if (type.IsEnum)
                    {
                        foreach (string value in Enum.GetNames(type))
                        {
                            if (!IsLocalised("Enum_" + type.Name + "_" + value))
                            {
                                problems.Add(name + ": no Enum_" + type.Name + "_" + value);
                            }
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, language + ": " + string.Join("; ", problems.Distinct()));
        }
        finally
        {
            LocalizationManager.Instance.Initialize("en");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void PriceTypeEnum_HasALabelForEveryMember(string language)
    {
        LocalizationManager.Instance.Initialize(language);
        try
        {
            string[] names = Enum.GetNames<PriceType>();
            Assert.Equal(15, names.Length);
            foreach (string value in names)
            {
                Assert.True(IsLocalised("Enum_PriceType_" + value), language + ": Enum_PriceType_" + value);
            }
        }
        finally
        {
            LocalizationManager.Instance.Initialize("en");
        }
    }

    [Fact]
    public void GarchFillOpacity_SitsInTheFillStyleCard()
    {
        DrawingParameterViewBuilder.PropertyMetadata meta = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(GarchVolatilityConeObject)).Single(m => m.Prop.Name == "FillOpacity");
        Assert.Equal("Fill Style", meta.Category);
        Assert.Contains(DrawingParameterTags.Fill, meta.Tags);
    }

    [Fact]
    public void PolygonParameters_AreUntaggedAndOrdered()
    {
        IReadOnlyList<DrawingParameterViewBuilder.PropertyMetadata> metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(RegularPolygonObject));
        string[] order = metadata.Where(m => m.Category == "Parameters").OrderBy(m => m.Order).Select(m => m.Prop.Name).ToArray();
        Assert.Equal(new[] { nameof(RegularPolygonObject.Sides), nameof(RegularPolygonObject.ShowCircumscribedCircle), nameof(RegularPolygonObject.ShowInscribedCircle) }, order);

        string[] fills = metadata.Where(m => m.Category == "Fill Style").OrderBy(m => m.Order).Select(m => m.Prop.Name).ToArray();
        Assert.Equal(
            new[]
            {
                nameof(RegularPolygonObject.PolygonFillColor), nameof(RegularPolygonObject.PolygonFillOpacity),
                nameof(RegularPolygonObject.CircumscribedFillColor), nameof(RegularPolygonObject.CircumscribedFillOpacity),
                nameof(RegularPolygonObject.InscribedFillColor), nameof(RegularPolygonObject.InscribedFillOpacity),
            },
            fills);
        Assert.All(metadata.Where(m => m.Category == "Fill Style"), m => Assert.Contains(StockAnalyzer.Core.Constants.DrawingParameterTags.Fill, m.Tags));
    }

    [AvaloniaFact]
    public void NullableColor_RoundTrip_NullThenBlackThenChosenColor()
    {
        var builder = new DrawingParameterViewBuilder();
        var sample = new NullableFillSample { FillColor = Colors.Red };

        var stack = Assert.IsType<StackPanel>(builder.Build(sample));
        StackPanel colorPanel = stack.Children.OfType<Border>()
            .Select(b => b.Child).OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Grid>())
            .SelectMany(g => g.Children.OfType<StackPanel>())
            .First(p => p.Children.OfType<CheckBox>().Any());
        CheckBox toggle = colorPanel.Children.OfType<CheckBox>().Single();
        ColorPicker picker = colorPanel.Children.OfType<ColorPicker>().Single();

        Assert.True(toggle.IsChecked);
        Assert.Equal(Colors.Red, sample.FillColor);

        toggle.IsChecked = false;
        Assert.Null(sample.FillColor);

        // Re-checking gives black (the shared converter does not remember the previous colour).
        toggle.IsChecked = true;
        Assert.Equal(Colors.Black, sample.FillColor);

        picker.Color = Colors.Green;
        Assert.Equal(Colors.Green, sample.FillColor);
    }

    [AvaloniaFact]
    public void FillColors_AreAlwaysEnabledColourPickers_WithoutAnEnableCheckbox_AndTheChosenColourReachesTheObject()
    {
        var builder = new DrawingParameterViewBuilder();
        using var polygon = new RegularPolygonObject(
            new ChartPoint(new DateTime(2026, 1, 1), 100m),
            new ChartPoint(new DateTime(2026, 1, 11), 110m));

        var stack = Assert.IsType<StackPanel>(builder.Build(polygon, DrawingSettingsDialog.FallbackHiddenTags));
        List<Grid> rows = stack.Children.OfType<Border>()
            .Select(b => b.Child).OfType<StackPanel>()
            .SelectMany(p => p.Children.OfType<Grid>())
            .ToList();

        foreach (string label in new[] { "Polygon Fill Color", "Circumscribed Circle Fill Color", "Inscribed Circle Fill Color" })
        {
            Grid row = rows.Single(g => g.Children.OfType<TextBlock>().Any(t => t.Text == label));
            ColorPicker picker = row.GetVisualDescendants().OfType<ColorPicker>().Single();
            Assert.True(picker.IsEnabled, label);
            Assert.Empty(row.GetVisualDescendants().OfType<CheckBox>());
            Assert.Equal(DrawingThemeContext.DefaultColor, picker.Color);
        }

        Grid polygonRow = rows.Single(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "Polygon Fill Color"));
        polygonRow.GetVisualDescendants().OfType<ColorPicker>().Single().Color = Colors.Green;
        Assert.Equal(Colors.Green, polygon.PolygonFillColor);
    }
}
