using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FreehandSerializationTests
{
    private static DrawingDocumentKey CreateKey() =>
        new("TEST", TimeframeType.Daily);

    [Fact]
    public void RoundTrip_FreehandObject_SerializesAndMaterializesWithExactPoints()
    {
        var manager = new ChartObjectManager();
        var key = CreateKey();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var originalObj = new FreehandObject
        {
            Color = Colors.Red,
            Thickness = 2.5,
            PanelIndex = 0
        };

        originalObj.Points.Add(new ChartPoint(t0, 100m));
        originalObj.Points.Add(new ChartPoint(t0.AddDays(1), 105.5m));
        originalObj.Points.Add(new ChartPoint(t0.AddDays(2), 102.25m));
        originalObj.Points.Add(new ChartPoint(t0.AddDays(3), 115.75m));

        manager.AddObject(originalObj);

        // 1. Capture to document state DTO
        var capturedState = DrawingDocumentCodec.Capture(manager, key, revision: 1);
        var standardContext = Assert.Single(capturedState.Contexts.Where(c => c.ContextName == nameof(ChartDrawingContextType.Standard)));
        var record = Assert.Single(standardContext.Objects);
        Assert.Equal(nameof(FreehandObject), record.TypeName);
        Assert.Equal(4, record.Points.Count);

        // 2. Serialize to JSON
        var json = DrawingDocumentCodec.SerializeToJson(capturedState);
        Assert.Contains(nameof(FreehandObject), json);
        Assert.Contains("\"version\": 2", json);

        // 3. Deserialize from JSON
        var restoredState = DrawingDocumentCodec.DeserializeFromJson(json);
        Assert.Equal(capturedState.Version, restoredState.Version);
        Assert.Equal(capturedState.Revision, restoredState.Revision);

        // 4. Materialize to fresh runtime object instances
        using var materialized = DrawingDocumentCodec.Materialize(restoredState);
        var restoredObjects = materialized.Objects[ChartDrawingContextType.Standard];
        var restoredObj = Assert.IsType<FreehandObject>(Assert.Single(restoredObjects));

        // 5. Invariant verification
        Assert.Equal(originalObj.Thickness, restoredObj.Thickness);
        Assert.Equal(originalObj.Color, restoredObj.Color);
        Assert.Equal(originalObj.PanelIndex, restoredObj.PanelIndex);
        Assert.Equal(4, restoredObj.Points.Count);

        for (int i = 0; i < originalObj.Points.Count; i++)
        {
            Assert.Equal(originalObj.Points[i].Time, restoredObj.Points[i].Time);
            Assert.Equal(originalObj.Points[i].Price, restoredObj.Points[i].Price);
        }
    }

    [Fact]
    public void RoundTrip_SinglePointFreehandObject_SerializesAndMaterializes()
    {
        var manager = new ChartObjectManager();
        var key = CreateKey();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var originalObj = new FreehandObject
        {
            Color = Colors.Blue,
            Thickness = 3.0
        };
        originalObj.Points.Add(new ChartPoint(t0, 150m));
        manager.AddObject(originalObj);

        var capturedState = DrawingDocumentCodec.Capture(manager, key, revision: 1);
        var json = DrawingDocumentCodec.SerializeToJson(capturedState);
        var restoredState = DrawingDocumentCodec.DeserializeFromJson(json);

        using var materialized = DrawingDocumentCodec.Materialize(restoredState);
        var restoredObj = Assert.IsType<FreehandObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));

        Assert.Single(restoredObj.Points);
        Assert.Equal(t0, restoredObj.Points[0].Time);
        Assert.Equal(150m, restoredObj.Points[0].Price);
        Assert.Equal(3.0, restoredObj.Thickness);
        Assert.Equal(Colors.Blue, restoredObj.Color);
    }

    [Fact]
    public void RoundTrip_EmptyFreehandObject_SerializesAndMaterializes()
    {
        var manager = new ChartObjectManager();
        var key = CreateKey();

        var originalObj = new FreehandObject();
        manager.AddObject(originalObj);

        var capturedState = DrawingDocumentCodec.Capture(manager, key, revision: 1);
        var json = DrawingDocumentCodec.SerializeToJson(capturedState);
        var restoredState = DrawingDocumentCodec.DeserializeFromJson(json);

        using var materialized = DrawingDocumentCodec.Materialize(restoredState);
        var restoredObj = Assert.IsType<FreehandObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));

        Assert.Empty(restoredObj.Points);
    }
}
