using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingDocumentCodecTests
{
    [Fact]
    public void GetRegisteredTypes_ReturnsAllChartObjects()
    {
        var registeredTypes = ChartObjectTypeRegistry.GetRegisteredTypes();

        Assert.NotNull(registeredTypes);
        Assert.NotEmpty(registeredTypes);
        Assert.Contains(nameof(TrendLineObject), registeredTypes.Keys);

        foreach (var (name, type) in registeredTypes)
        {
            Assert.True(typeof(IChartObject).IsAssignableFrom(type));
            Assert.False(type.IsAbstract);
            Assert.Equal(name, type.Name);
        }
    }

    [Fact]
    public void CaptureAndMaterialize_StandardContext_PreservesUtcAndPrecision()
    {
        var manager = new ChartObjectManager();
        var key = new DrawingDocumentKey("7203", TimeframeType.Daily);

        var utcTime1 = new DateTime(2024, 1, 15, 9, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        var utcTime2 = new DateTime(2024, 2, 20, 15, 0, 0, DateTimeKind.Utc);

        var line = new TrendLineObject(
            new ChartPoint(utcTime1, 1500.50m),
            new ChartPoint(utcTime2, 1750.75m))
        {
            Color = Colors.Green,
            Thickness = 2.0,
            ShowProjection = true,
            ProjectionColumns = 5
        };

        manager.AddObject(line);

        // Capture
        var state = DrawingDocumentCodec.Capture(manager, key, revision: 1);

        Assert.Equal(DrawingDocumentState.CurrentSchemaVersion, state.Version);
        Assert.Equal(key, state.Key);
        Assert.Equal(1, state.Revision);

        var standardCtx = Assert.Single(state.Contexts, c => c.ContextName == ChartDrawingContextType.Standard.ToString());
        var record = Assert.Single(standardCtx.Objects);

        Assert.Equal(nameof(TrendLineObject), record.TypeName);
        Assert.Equal(DrawingCoordinateKind.UtcTime, record.CoordinateKind);
        Assert.Equal(2, record.Points.Count);

        Assert.Equal(utcTime1, record.Points[0].UtcTime);
        Assert.Null(record.Points[0].XValue);
        Assert.Equal(1500.50m, record.Points[0].YValue);

        // Materialize
        using var materialized = DrawingDocumentCodec.Materialize(state);
        Assert.True(materialized.Objects.ContainsKey(ChartDrawingContextType.Standard));

        var restoredList = materialized.Objects[ChartDrawingContextType.Standard];
        var restoredLine = Assert.IsType<TrendLineObject>(Assert.Single(restoredList));

        Assert.Equal(utcTime1, restoredLine.Points[0].Time);
        Assert.Equal(1500.50m, restoredLine.Points[0].Price);
        Assert.Equal(utcTime2, restoredLine.Points[1].Time);
        Assert.Equal(1750.75m, restoredLine.Points[1].Price);

        Assert.Equal(Colors.Green, restoredLine.Color);
        Assert.Equal(2.0, restoredLine.Thickness);
        Assert.True(restoredLine.ShowProjection);
        Assert.Equal(5, restoredLine.ProjectionColumns);

        // Verify ID mapping
        Assert.True(materialized.PersistentToRuntimeIds.ContainsKey(record.ObjectId));
        Assert.Equal(restoredLine.Id, materialized.PersistentToRuntimeIds[record.ObjectId]);
    }

    [Fact]
    public void CaptureAndMaterialize_NonTimeContexts_PreservesLogicalIndexAndVolume()
    {
        var manager = new ChartObjectManager();
        var key = new DrawingDocumentKey("AAPL", TimeframeType.Daily);

        // Renko context (Index-based)
        manager.SwitchContext(ChartDrawingContextType.Renko);
        var renkoLine = new TrendLineObject(
            new ChartPoint(new DateTime(42), 180.25m),
            new ChartPoint(new DateTime(100), 195.50m));
        manager.AddObject(renkoLine);

        // ReverseWatch context (Volume-based)
        manager.SwitchContext(ChartDrawingContextType.ReverseWatch);
        var volumeLine = new TrendLineObject(
            new ChartPoint(new DateTime(500000), 182.00m),
            new ChartPoint(new DateTime(800000), 190.00m));
        manager.AddObject(volumeLine);

        // Capture
        var state = DrawingDocumentCodec.Capture(manager, key, revision: 2);

        var renkoCtx = Assert.Single(state.Contexts, c => c.ContextName == ChartDrawingContextType.Renko.ToString());
        var renkoRecord = Assert.Single(renkoCtx.Objects);
        Assert.Equal(DrawingCoordinateKind.LogicalIndex, renkoRecord.CoordinateKind);
        Assert.Null(renkoRecord.Points[0].UtcTime);
        Assert.Equal(42m, renkoRecord.Points[0].XValue);

        var volumeCtx = Assert.Single(state.Contexts, c => c.ContextName == ChartDrawingContextType.ReverseWatch.ToString());
        var volumeRecord = Assert.Single(volumeCtx.Objects);
        Assert.Equal(DrawingCoordinateKind.VolumePosition, volumeRecord.CoordinateKind);
        Assert.Null(volumeRecord.Points[0].UtcTime);
        Assert.Equal(500000m, volumeRecord.Points[0].XValue);

        // Materialize
        using var materialized = DrawingDocumentCodec.Materialize(state);

        var restoredRenko = Assert.IsType<TrendLineObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Renko]));
        Assert.Equal(42, restoredRenko.Points[0].Time.Ticks);
        Assert.Equal(180.25m, restoredRenko.Points[0].Price);

        var restoredVolume = Assert.IsType<TrendLineObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.ReverseWatch]));
        Assert.Equal(500000, restoredVolume.Points[0].Time.Ticks);
        Assert.Equal(182.00m, restoredVolume.Points[0].Price);
    }

    [Fact]
    public void CoordinateMetadata_WithTimeMapOnIndexAxis_MapsToUtcTime()
    {
        var meta = new DrawingCoordinateMetadata(ChartAxisMode.Index, HasTimeMap: true);
        var kind = DrawingDocumentCodec.GetCoordinateKind(ChartDrawingContextType.Renko, meta);

        Assert.Equal(DrawingCoordinateKind.UtcTime, kind);

        var noTimeMapMeta = new DrawingCoordinateMetadata(ChartAxisMode.Index, HasTimeMap: false);
        var noTimeMapKind = DrawingDocumentCodec.GetCoordinateKind(ChartDrawingContextType.Renko, noTimeMapMeta);

        Assert.Equal(DrawingCoordinateKind.LogicalIndex, noTimeMapKind);
    }

    [Fact]
    public void LogarithmicSpiralObject_WithAllowDegenerateCurveAndZeroGrowthRate_RoundTripsSuccessfully()
    {
        // R10 test: LogarithmicSpiralObject requires AllowDegenerateCurve=true before GrowthRate=0 can be set.
        var spiral = new LogarithmicSpiralObject(
            new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new ChartPoint(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), 120m))
        {
            AllowDegenerateCurve = true,
            GrowthRate = 0.0 // Must not throw InvalidOperationException during ApplyParameters
        };

        var manager = new ChartObjectManager();
        manager.AddObject(spiral);

        var key = new DrawingDocumentKey("SPY", TimeframeType.Daily);
        var state = DrawingDocumentCodec.Capture(manager, key, 1);

        using var materialized = DrawingDocumentCodec.Materialize(state);
        var restored = Assert.IsType<LogarithmicSpiralObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));

        Assert.True(restored.AllowDegenerateCurve);
        Assert.Equal(0.0, restored.GrowthRate);
    }

    [Fact]
    public void BarPatternObject_CandlesCollection_ZeroReferenceSharingAfterCapture()
    {
        // R01 test: Verify deep defensive copying of Candles collection
        var anchor = new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m);
        var barPattern = new BarPatternObject(anchor);
        barPattern.Candles.Add(new GhostCandle
        {
            TicksOffset = 1000000,
            Open = 10m,
            High = 15m,
            Low = 5m,
            Close = 12m
        });

        var manager = new ChartObjectManager();
        manager.AddObject(barPattern);

        var key = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var state = DrawingDocumentCodec.Capture(manager, key, 1);

        // Mutate live candle after capture
        barPattern.Candles[0].Open = 9999m;
        barPattern.Candles.Clear();

        // Verify captured state is completely unaffected
        using var materialized = DrawingDocumentCodec.Materialize(state);
        var restored = Assert.IsType<BarPatternObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));

        Assert.Single(restored.Candles);
        Assert.Equal(10m, restored.Candles[0].Open);
        Assert.NotSame(barPattern.Candles, restored.Candles);
    }

    [Fact]
    public void Materialize_RelativeBarOverflow_ThrowsInvalidDataException()
    {
        // R12 test: Verify checked bounds validation prevents overflow
        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(BarPatternObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { DrawingStoredPoint.CreateUtc(DateTime.MaxValue, 100m) },
            new Dictionary<string, object?>
            {
                ["candles"] = new[]
                {
                    new GhostCandle { TicksOffset = 1000000000, Open = 1m, High = 2m, Low = 0m, Close = 1m }
                }
            }
        );

        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey("TEST", TimeframeType.Daily),
            1,
            new[] { new DrawingContextState(ChartDrawingContextType.Standard.ToString(), null, new[] { record }) }
        );

        Assert.Throws<InvalidDataException>(() => DrawingDocumentCodec.Materialize(state));
    }

    [Fact]
    public void Materialize_DuplicateObjectId_ThrowsInvalidDataException()
    {
        // R06 test: Verify duplicate ObjectId is strictly rejected
        var sharedId = Guid.NewGuid();
        var record1 = new DrawingObjectRecord(sharedId, nameof(TrendLineObject), PanelKey.Main, DrawingCoordinateKind.UtcTime, Array.Empty<DrawingStoredPoint>(), null);
        var record2 = new DrawingObjectRecord(sharedId, nameof(TrendLineObject), PanelKey.Main, DrawingCoordinateKind.UtcTime, Array.Empty<DrawingStoredPoint>(), null);

        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey("TEST", TimeframeType.Daily),
            1,
            new[] { new DrawingContextState(ChartDrawingContextType.Standard.ToString(), null, new[] { record1, record2 }) }
        );

        Assert.Throws<InvalidDataException>(() => DrawingDocumentCodec.Materialize(state));
    }

    [Fact]
    public void JsonSerialization_RoundTrip_MatchesExpectedV2Schema()
    {
        var key = new DrawingDocumentKey("9984", TimeframeType.Weekly);
        var points = new[]
        {
            DrawingStoredPoint.CreateUtc(new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc), 6500m),
            DrawingStoredPoint.CreateUtc(new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc), 7200m)
        };

        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            points,
            new Dictionary<string, object?>
            {
                ["color"] = "#ff0000ff",
                ["thickness"] = 1.5,
                ["showProjection"] = false
            }
        );

        var context = new DrawingContextState(
            ChartDrawingContextType.Standard.ToString(),
            new[] { new DrawingLayerRecord(Guid.NewGuid(), "Default", PanelKey.Main, true, false, new[] { record.ObjectId }) },
            new[] { record }
        );

        var originalState = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision: 5,
            new[] { context }
        );

        // Serialize
        var json = DrawingDocumentCodec.SerializeToJson(originalState);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"kind\": \"UtcTime\"", json);

        // Deserialize
        var restoredState = DrawingDocumentCodec.DeserializeFromJson(json);

        Assert.Equal(originalState.Version, restoredState.Version);
        Assert.Equal(originalState.Key, restoredState.Key);
        Assert.Equal(originalState.Revision, restoredState.Revision);
        Assert.Single(restoredState.Contexts);

        var restoredRecord = restoredState.Contexts[0].Objects[0];
        Assert.Equal(record.ObjectId, restoredRecord.ObjectId);
        Assert.Equal(record.TypeName, restoredRecord.TypeName);
        Assert.Equal(record.CoordinateKind, restoredRecord.CoordinateKind);
        Assert.Equal(points[0].UtcTime, restoredRecord.Points[0].UtcTime);
        Assert.Equal(points[0].YValue, restoredRecord.Points[0].YValue);

        // And Materialize from deserialized state
        using var materialized = DrawingDocumentCodec.Materialize(restoredState);
        var restoredObj = Assert.IsType<TrendLineObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));
        Assert.Equal(1.5, restoredObj.Thickness);
    }

    [Fact]
    public void Materialize_UnknownType_ThrowsNotSupportedException()
    {
        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            "NonExistentChartObject",
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            Array.Empty<DrawingStoredPoint>(),
            null
        );

        var context = new DrawingContextState(
            ChartDrawingContextType.Standard.ToString(),
            null,
            new[] { record }
        );

        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey("TEST", TimeframeType.Daily),
            1,
            new[] { context }
        );

        Assert.Throws<NotSupportedException>(() => DrawingDocumentCodec.Materialize(state));
    }

    [Fact]
    public void Materialize_UnknownProperty_ThrowsNotSupportedException()
    {
        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { DrawingStoredPoint.CreateUtc(DateTime.UtcNow, 100m) },
            new Dictionary<string, object?>
            {
                ["unsupportedNonExistentProperty"] = "SomeValue"
            }
        );

        var context = new DrawingContextState(
            ChartDrawingContextType.Standard.ToString(),
            null,
            new[] { record }
        );

        var state = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            new DrawingDocumentKey("TEST", TimeframeType.Daily),
            1,
            new[] { context }
        );

        Assert.Throws<NotSupportedException>(() => DrawingDocumentCodec.Materialize(state));
    }

    [Fact]
    public void Capture_DefensiveCopy_SourceMutationDoesNotAffectCapturedState()
    {
        var manager = new ChartObjectManager();
        var key = new DrawingDocumentKey("MSFT", TimeframeType.Daily);

        var line = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 400m),
            new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 405m));

        manager.AddObject(line);

        var state = DrawingDocumentCodec.Capture(manager, key, 1);

        // Mutate original live object after capture
        line.Points.Add(new ChartPoint(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), 410m));
        line.Color = Colors.Red;

        // Verify captured state is immune to mutations
        var capturedRecord = state.Contexts[0].Objects[0];
        Assert.Equal(2, capturedRecord.Points.Count);
    }

    [Fact]
    public void ConvertStoredToChartPoint_FractionalIndex_RoundsAndValidatesBounds()
    {
        // V04 test: Verify fractional XValue is safely rounded and bounds checked
        var ptDown = DrawingStoredPoint.CreateIndex(42.4m, 100m);
        var restoredDown = DrawingDocumentCodec.ConvertStoredToChartPoint(ptDown, DrawingCoordinateKind.LogicalIndex);
        Assert.Equal(42, restoredDown.Time.Ticks);

        var ptUp = DrawingStoredPoint.CreateIndex(42.6m, 100m);
        var restoredUp = DrawingDocumentCodec.ConvertStoredToChartPoint(ptUp, DrawingCoordinateKind.LogicalIndex);
        Assert.Equal(43, restoredUp.Time.Ticks);

        var ptNegative = DrawingStoredPoint.CreateIndex(-5m, 100m);
        Assert.Throws<InvalidDataException>(() =>
            DrawingDocumentCodec.ConvertStoredToChartPoint(ptNegative, DrawingCoordinateKind.LogicalIndex));
    }
}
