using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Moq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingInteractionSettingsTests
{
    private static readonly DrawingDocumentKey TestKey = new("7203", TimeframeType.Daily);

    private static FrozenContextState CreateState(decimal price)
    {
        var objectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var points = new List<DrawingStoredPoint>
        {
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), price),
            DrawingStoredPoint.CreateUtc(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), price + 50m)
        };
        var objects = new List<DrawingObjectRecord>
        {
            new(objectId, "TrendLineObject", PanelKey.Main, DrawingCoordinateKind.UtcTime, points,
                new Dictionary<string, object?> { ["color"] = "#FF0000", ["thickness"] = 2.0 })
        };
        var layers = new List<DrawingLayerRecord>
        {
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Default Layer", PanelKey.Main,
                isVisible: true, isEditLocked: false, objectIds: new[] { objectId })
        };
        return new FrozenContextState("Standard", layers, objects);
    }

    private static IStockAnalyzerSettings SettingsWith(DrawingInteractionSettings limits)
    {
        var mock = new Mock<IStockAnalyzerSettings>();
        mock.Setup(s => s.DrawingInteraction).Returns(limits);
        return mock.Object;
    }

    private static void CommitOneEntry(DrawingHistoryService history, decimal from, decimal to)
    {
        var begin = history.BeginEdit(DrawingOperationKind.Move, CreateState(from));
        Assert.True(begin.IsSuccess);
        Assert.True(history.Commit(begin.Token, CreateState(to)).IsSuccess);
    }

    [Fact]
    public void Defaults_AreTheDrawingInteractionLimitsConstants_AndTheShippedAppsettingsAgree()
    {
        var defaults = new DrawingInteractionSettings();
        var config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        var bound = new DrawingInteractionSettings();
        config.GetSection("DrawingInteraction").Bind(bound);

        Assert.Equal(DrawingInteractionLimits.MaxHistoryEntriesPerContext, defaults.MaxHistoryEntriesPerContext);
        Assert.Equal(DrawingInteractionLimits.MaxHistoryPayloadBytesPerContext, defaults.MaxHistoryPayloadBytesPerContext);
        Assert.Equal(DrawingInteractionLimits.MaxSessionHistoryPayloadBytes, defaults.MaxSessionHistoryPayloadBytes);
        Assert.Equal(DrawingInteractionLimits.MaxStrokeSamples, defaults.MaxStrokeSamples);

        Assert.Equal(defaults.MaxHistoryEntriesPerContext, bound.MaxHistoryEntriesPerContext);
        Assert.Equal(defaults.MaxHistoryPayloadBytesPerContext, bound.MaxHistoryPayloadBytesPerContext);
        Assert.Equal(defaults.MaxSessionHistoryPayloadBytes, bound.MaxSessionHistoryPayloadBytes);
        Assert.Equal(defaults.MaxStrokeSamples, bound.MaxStrokeSamples);
    }

    [Fact]
    public void InterfaceDefault_MatchesThePocoDefaults()
    {
        var viaMock = ((IStockAnalyzerSettings)new MockStockAnalyzerSettings()).DrawingInteraction;

        Assert.Equal(DrawingInteractionLimits.MaxStrokeSamples, viaMock.MaxStrokeSamples);
        Assert.Equal(DrawingInteractionLimits.MaxHistoryEntriesPerContext, viaMock.MaxHistoryEntriesPerContext);
    }

    [Theory]
    [InlineData(0, 1L, 1L, 1)]
    [InlineData(1, 0L, 1L, 1)]
    [InlineData(1, 1L, -5L, 1)]
    [InlineData(1, 1L, 1L, 0)]
    public void Validate_NonPositiveBound_IsRejected(int entries, long perContext, long session, int strokes)
    {
        var settings = new DrawingInteractionSettings
        {
            MaxHistoryEntriesPerContext = entries,
            MaxHistoryPayloadBytesPerContext = perContext,
            MaxSessionHistoryPayloadBytes = session,
            MaxStrokeSamples = strokes
        };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void SessionStore_HistoryEntryLimit_FollowsTheConfiguredValue()
    {
        var store = new DrawingDocumentSessionStore(SettingsWith(new DrawingInteractionSettings { MaxHistoryEntriesPerContext = 1 }));
        var history = store.GetOrCreate(TestKey).GetOrCreateHistory(ChartDrawingContextType.Standard);

        CommitOneEntry(history, 1000m, 1100m);
        CommitOneEntry(history, 1100m, 1200m);

        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void SessionStore_WithoutSettings_KeepsTheDefaultEntryLimit()
    {
        var store = new DrawingDocumentSessionStore();
        var history = store.GetOrCreate(TestKey).GetOrCreateHistory(ChartDrawingContextType.Standard);

        CommitOneEntry(history, 1000m, 1100m);
        CommitOneEntry(history, 1100m, 1200m);

        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void SessionStore_EnforceGlobalHistoryBudget_UsesTheConfiguredSessionBudget()
    {
        var store = new DrawingDocumentSessionStore(SettingsWith(new DrawingInteractionSettings { MaxSessionHistoryPayloadBytes = 1 }));
        var history = store.GetOrCreate(TestKey).GetOrCreateHistory(ChartDrawingContextType.Standard);
        CommitOneEntry(history, 1000m, 1100m);

        int evicted = store.EnforceGlobalHistoryBudget();

        Assert.Equal(1, evicted);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void FreehandAdapter_ControllerCapacity_FollowsTheConstructorCapacity()
    {
        Assert.Equal(10, new FreehandPointerAdapter(10).Controller.Capacity);
        Assert.Equal(DrawingInteractionLimits.MaxStrokeSamples, new FreehandPointerAdapter().Controller.Capacity);
    }
}
