using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// GUI / MCP parity of the GARCH cone price source (settings expansion Run 3, DS-8): the chart object, the catalogue descriptor with
/// <see cref="DrawingValueOptions.GarchPriceSource"/> and the saved-drawing read path (<see cref="DrawingStoredOptions"/>) give the same values for a non-default price type.
/// </summary>
public sealed class GarchPriceSourceParityTests
{
    private static readonly IndicatorColor Gray = new(255, 90, 90, 90);

    private static IReadOnlyList<CoreCandleData> Candles => DrawingValueCatalogGuardTests.Candles;

    private static ChartPoint[] Anchors() => new[] { new ChartPoint(new DateTime(2025, 11, 20), 100m), new ChartPoint(new DateTime(2026, 3, 20), 150m) };

    private static IReadOnlyList<DrawingCalculatedValue> Gui(PriceType source)
    {
        ChartPoint[] points = Anchors();
        using var cone = new GarchVolatilityConeObject(points[0], points[1]) { PriceSource = source };
        cone.Recalculate(Candles);
        return cone.GetCalculatedValues(new DateTime(2026, 3, 20), null);
    }

    private static IReadOnlyList<DrawingCalculatedValue> Catalogue(PriceType source, DrawingValueOptions? stored = null)
    {
        Assert.True(DrawingValueCatalog.TryGet("GarchVolatilityCone", out DrawingValueDescriptor descriptor));
        ChartPoint[] points = Anchors();
        var request = new DrawingValueRequest(
            points.Select(p => new DrawingAnchor(p.Time, p.Price)).ToArray(),
            new DateTime(2026, 3, 20),
            null,
            Gray,
            stored ?? new DrawingValueOptions(GarchPriceSource: source),
            Candles);
        return descriptor.Calculate(request);
    }

    /// <summary>The parameters of a record as the reader sees them: after a trip through the JSON of the file.</summary>
    private static IReadOnlyDictionary<string, object?> AfterTheFile(DrawingObjectRecord record)
    {
        string json = JsonSerializer.Serialize(record.StyleAndParameters, DrawingDocumentCodec.SerializerOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    [Theory]
    [InlineData(PriceType.Median)]
    [InlineData(PriceType.HeikinAshiClose)]
    [InlineData(PriceType.TrueLow)]
    public void GuiObject_Catalogue_AndSavedDrawing_GiveTheSameValues_ForANonDefaultPrice(PriceType source)
    {
        IReadOnlyList<DrawingCalculatedValue> gui = Gui(source);
        IReadOnlyList<DrawingCalculatedValue> core = Catalogue(source);
        Assert.NotEmpty(gui);
        Assert.True(gui.Select(v => (v.Key, v.FormattedText)).SequenceEqual(core.Select(v => (v.Key, v.FormattedText))), "catalogue differs from the chart object");

        // The saved path: the object is saved, read back as the file gives it, and the stored options feed the same descriptor.
        ChartPoint[] points = Anchors();
        using var cone = new GarchVolatilityConeObject(points[0], points[1]) { PriceSource = source };
        var manager = new ChartObjectManager();
        manager.AddObject(cone);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("GARCH", TimeframeType.Daily), revision: 1L);
        DrawingObjectRecord record = state.Contexts.Single(c => c.ContextName == DrawingContextNames.Standard).Objects.Single(r => r.TypeName == nameof(GarchVolatilityConeObject));
        DrawingValueOptions options = DrawingStoredOptions.Read(AfterTheFile(record));
        Assert.Equal(source, options.GarchPriceSource);
        IReadOnlyList<DrawingCalculatedValue> saved = Catalogue(source, options);
        Assert.True(gui.Select(v => (v.Key, v.FormattedText)).SequenceEqual(saved.Select(v => (v.Key, v.FormattedText))), "saved-drawing path differs from the chart object");
    }

    [Theory]
    [InlineData(PriceType.Median)]
    [InlineData(PriceType.HeikinAshiClose)]
    [InlineData(PriceType.TrueLow)]
    public void ANonDefaultPrice_ChangesTheValues_SoTheParityIsMeaningful(PriceType source)
    {
        IReadOnlyList<DrawingCalculatedValue> close = Gui(PriceType.Close);
        IReadOnlyList<DrawingCalculatedValue> other = Gui(source);
        Assert.False(close.Select(v => v.FormattedText).SequenceEqual(other.Select(v => v.FormattedText)));
    }

    [Fact]
    public void DefaultPrice_IsTheClose_ForTheChartObject_TheOptions_AndAFileWithoutTheKey()
    {
        ChartPoint[] points = Anchors();
        using var cone = new GarchVolatilityConeObject(points[0], points[1]);
        Assert.Equal(PriceType.Close, cone.PriceSource);
        Assert.Equal(PriceType.Close, new DrawingValueOptions().GarchPriceSource);
        Assert.Equal(PriceType.Close, DrawingStoredOptions.Read(new Dictionary<string, object?>()).GarchPriceSource);

        // The other tools' defaults are untouched: the shared key "priceSource" still defaults to Typical (AnchoredVWAP) and Close (DistributionRange).
        DrawingValueOptions empty = DrawingStoredOptions.Read(new Dictionary<string, object?>());
        Assert.Equal(PriceType.Typical, empty.PriceSource);
        Assert.Equal(PriceType.Close, empty.DistributionPriceSource);
    }
}
