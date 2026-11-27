using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using AColor = global::Avalonia.Media.Color;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The fill colour of the GARCH cone, the Robust Regression Trend and the Regular Polygon is a non-null colour (settings follow-up Run 6). Files that an earlier build saved hold <c>null</c> for it, and
/// <c>DrawingDocumentCodec</c> turns a saved null into <c>default(Color)</c> (transparent black): the fill colour setting must read that as "the default colour", never as a black fill.
/// Both persistence paths are checked with a null, an absent key and a real colour.
/// </summary>
public sealed class FillColorDocumentCompatibilityTests
{
    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly AColor Red = AColor.FromRgb(255, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static readonly HashSet<string> FillColorKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fillColor", "polygonFillColor", "circumscribedFillColor", "inscribedFillColor",
    };

    private static ChartPoint P(int day, decimal price) => new(Start.AddDays(day), price);

    public static TheoryData<string> Tools() => new() { "Polygon", "Garch", "Robust" };

    private static IChartObject Create(string tool) => tool switch
    {
        "Polygon" => new RegularPolygonObject(P(10, 100m), P(20, 110m)),
        "Garch" => new GarchVolatilityConeObject(P(10, 100m), P(60, 110m)),
        _ => new RobustRegressionTrendObject(P(10, 100m), P(60, 110m)),
    };

    private static void SetColours(IChartObject obj, AColor colour)
    {
        switch (obj)
        {
            case RegularPolygonObject polygon:
                polygon.PolygonFillColor = colour;
                polygon.CircumscribedFillColor = colour;
                polygon.InscribedFillColor = colour;
                break;
            case GarchVolatilityConeObject cone:
                cone.FillColor = colour;
                break;
            case RobustRegressionTrendObject robust:
                robust.FillColor = colour;
                break;
        }
    }

    private static AColor[] Colours(IChartObject obj) => obj switch
    {
        RegularPolygonObject polygon => new[] { polygon.PolygonFillColor, polygon.CircumscribedFillColor, polygon.InscribedFillColor },
        GarchVolatilityConeObject cone => new[] { cone.FillColor },
        RobustRegressionTrendObject robust => new[] { robust.FillColor },
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Replaces every fill colour value of a JSON document by null (<paramref name="remove"/> false) or removes the key (true).</summary>
    private static void Rewrite(JsonNode? node, bool remove)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (FillColorKeys.Contains(key))
                    {
                        if (remove)
                        {
                            obj.Remove(key);
                        }
                        else
                        {
                            obj[key] = null;
                        }
                    }
                    else
                    {
                        Rewrite(obj[key], remove);
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Rewrite(item, remove);
                }

                break;
        }
    }

    private static IChartObject ThroughConverter(IChartObject source, Action<JsonNode>? rewrite)
    {
        string json = JsonSerializer.Serialize(source, JsonOptions);
        if (rewrite != null)
        {
            JsonNode node = JsonNode.Parse(json)!;
            rewrite(node);
            json = node.ToJsonString();
        }

        return JsonSerializer.Deserialize<IChartObject>(json, JsonOptions)!;
    }

    private static IChartObject ThroughCodec(IChartObject source, Action<JsonNode>? rewrite)
    {
        var manager = new ChartObjectManager();
        manager.AddObject(source);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("FILL", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        if (rewrite != null)
        {
            JsonNode node = JsonNode.Parse(json)!;
            rewrite(node);
            json = node.ToJsonString();
        }

        var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(json));
        return materialized.Objects[ChartDrawingContextType.Standard].Single();
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void NewObject_StartsWithTheDefaultDrawingColour(string tool)
    {
        IChartObject obj = Create(tool);
        try
        {
            Assert.All(Colours(obj), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));
        }
        finally
        {
            (obj as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ASavedNull_ReadsAsTheDefaultColour_OnTheConverterPath(string tool)
    {
        IChartObject source = Create(tool);
        SetColours(source, Red);
        IChartObject restored = ThroughConverter(source, node => Rewrite(node, remove: false));
        try
        {
            Assert.All(Colours(restored), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            (restored as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ASavedNull_ReadsAsTheDefaultColour_NotABlackFill_OnTheDocumentCodecPath(string tool)
    {
        IChartObject source = Create(tool);
        SetColours(source, Red);
        IChartObject restored = ThroughCodec(source, node => Rewrite(node, remove: false));
        try
        {
            Assert.All(Colours(restored), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            (restored as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void AnAbsentKey_ReadsAsTheDefaultColour_OnBothPaths(string tool)
    {
        IChartObject source = Create(tool);
        SetColours(source, Red);
        IChartObject viaConverter = ThroughConverter(source, node => Rewrite(node, remove: true));
        IChartObject viaCodec = ThroughCodec(source, node => Rewrite(node, remove: true));
        try
        {
            Assert.All(Colours(viaConverter), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));
            Assert.All(Colours(viaCodec), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            (viaConverter as IDisposable)?.Dispose();
            (viaCodec as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ARealColour_SurvivesBothPaths(string tool)
    {
        IChartObject source = Create(tool);
        SetColours(source, Red);
        IChartObject viaConverter = ThroughConverter(source, null);
        IChartObject viaCodec = ThroughCodec(source, null);
        try
        {
            Assert.All(Colours(viaConverter), c => Assert.Equal(Red, c));
            Assert.All(Colours(viaCodec), c => Assert.Equal(Red, c));
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            (viaConverter as IDisposable)?.Dispose();
            (viaCodec as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ATransparentColour_IsTheDefaultColour_TheAlphaOfAFillColourIsNotUsed(string tool)
    {
        IChartObject obj = Create(tool);
        try
        {
            SetColours(obj, default);   // A = 0: what a saved null becomes
            Assert.All(Colours(obj), c => Assert.Equal(DrawingThemeContext.DefaultColor, c));

            SetColours(obj, AColor.FromArgb(40, 10, 20, 30));   // a real colour keeps its channels; only the alpha of the opacity setting is drawn
            Assert.All(Colours(obj), c => Assert.Equal(AColor.FromArgb(40, 10, 20, 30), c));
        }
        finally
        {
            (obj as IDisposable)?.Dispose();
        }
    }
}
