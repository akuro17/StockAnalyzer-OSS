using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Single source of truth for visibility: for every object the manager holds, <c>manager.IsVisible(id)</c> equals the object's own
/// <c>IsVisible</c>, after every kind of operation (add, toggle, show/hide all, remove, delete all, clear, load, context switch,
/// duplicate, undo/redo). An id the manager does not hold is never visible. The render path follows the same rule.
/// Permanent regression tests: they do not look at any internal field.
/// </summary>
public sealed class ChartObjectVisibilityInvariantTests
{
    private static readonly DrawingDocumentKey TestKey = new("7203", TimeframeType.Daily);
    private const int Width = 400;
    private const int Height = 300;

    private static TrendLineObject Line(bool visible = true, double y = 100)
        => new(PixelCoordinateTransform.Chart(100, y), PixelCoordinateTransform.Chart(300, y + 100))
        {
            IsVisible = visible,
            Color = global::Avalonia.Media.Colors.White,
            Thickness = 2
        };

    private static void AssertInvariant(ChartObjectManager manager)
    {
        foreach (IChartObject obj in manager.Objects.ToList())
        {
            Assert.True(manager.IsVisible(obj.Id) == obj.IsVisible,
                $"manager.IsVisible disagrees with the object (object IsVisible = {obj.IsVisible}, id {obj.Id}).");
        }
    }

    [Fact]
    public void AddObject_VisibleAndHidden_FollowTheObject()
    {
        var manager = new ChartObjectManager();
        var visible = Line();
        var hidden = Line(visible: false, y: 150);

        Assert.True(manager.AddObject(visible));
        Assert.True(manager.AddObject(hidden));

        AssertInvariant(manager);
        Assert.True(manager.IsVisible(visible.Id));
        Assert.False(manager.IsVisible(hidden.Id));
    }

    [Fact]
    public void ToggleVisibility_HideAll_ShowAll_KeepTheInvariant()
    {
        var manager = new ChartObjectManager();
        var a = Line();
        var b = Line(visible: false, y: 150);
        manager.AddObject(a);
        manager.AddObject(b);

        Assert.True(manager.ToggleVisibility(a.Id));
        AssertInvariant(manager);
        Assert.False(a.IsVisible);

        Assert.True(manager.ToggleVisibility(b.Id));
        AssertInvariant(manager);
        Assert.True(b.IsVisible);

        manager.HideAll();
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(a.Id));
        Assert.False(manager.IsVisible(b.Id));

        manager.ShowAll();
        AssertInvariant(manager);
        Assert.True(manager.IsVisible(a.Id));
        Assert.True(manager.IsVisible(b.Id));

        Assert.True(manager.ToggleVisibility(a.Id));
        Assert.True(manager.ToggleVisibility(a.Id));
        AssertInvariant(manager);
    }

    [Fact]
    public void RemoveDeleteAllAndClear_KeepTheInvariant_AndRemovedIdsAreNotVisible()
    {
        var manager = new ChartObjectManager();
        var hiddenOne = Line(visible: false);
        var keeper = Line(y: 150);
        var locked = Line(y: 200);
        manager.AddObject(hiddenOne);
        manager.AddObject(keeper);
        manager.AddObject(locked);
        manager.ToggleLock(locked.Id);

        Assert.True(manager.RemoveObject(hiddenOne.Id));
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(hiddenOne.Id));

        manager.DeleteAll();   // the locked object survives
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(keeper.Id));
        Assert.True(manager.IsVisible(locked.Id));

        manager.Clear();
        Assert.Empty(manager.Objects);
        Assert.False(manager.IsVisible(locked.Id));
    }

    [Fact]
    public void IsVisible_ForAnIdTheManagerDoesNotHold_IsFalse()
    {
        var manager = new ChartObjectManager();
        var line = Line();
        manager.AddObject(line);

        Assert.False(manager.IsVisible(Guid.NewGuid()));
        Assert.False(manager.IsVisible(Guid.Empty));

        // An object that lives only in another context is not held by the current one.
        manager.SwitchContext(ChartDrawingContextType.Linear);
        Assert.False(manager.IsVisible(line.Id));
        manager.SwitchContext(ChartDrawingContextType.Standard);
        Assert.True(manager.IsVisible(line.Id));
    }

    [Fact]
    public void LoadSnapshot_MixedVisibility_FollowsTheObjects()
    {
        var manager = new ChartObjectManager();
        var shown = Line();
        var hidden = Line(visible: false, y: 150);
        manager.LoadSnapshot(new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new() { shown, hidden }
        });

        AssertInvariant(manager);
        Assert.True(manager.IsVisible(shown.Id));
        Assert.False(manager.IsVisible(hidden.Id));

        // Round trip through GetSnapshot keeps it.
        manager.ToggleVisibility(shown.Id);
        var snapshot = manager.GetSnapshot();
        manager.LoadSnapshot(snapshot);
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(shown.Id));
    }

    [Fact]
    public void SwitchContext_BackAndForth_KeepsEachContextsVisibility()
    {
        var manager = new ChartObjectManager();
        var standardHidden = Line(visible: false);
        var standardShown = Line(y: 150);
        manager.AddObject(standardHidden);
        manager.AddObject(standardShown);

        manager.SwitchContext(ChartDrawingContextType.Linear);
        var linearShown = Line();
        var linearHidden = Line(visible: false, y: 150);
        manager.AddObject(linearShown);
        manager.AddObject(linearHidden);
        AssertInvariant(manager);
        manager.ToggleVisibility(linearShown.Id);
        AssertInvariant(manager);

        manager.SwitchContext(ChartDrawingContextType.Standard);
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(standardHidden.Id));
        Assert.True(manager.IsVisible(standardShown.Id));

        manager.SwitchContext(ChartDrawingContextType.Linear);
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(linearShown.Id));
        Assert.False(manager.IsVisible(linearHidden.Id));
    }

    [Fact]
    public void DuplicatingAHiddenObject_GivesAVisibleClone()
    {
        var manager = new ChartObjectManager();
        var hidden = Line(visible: false);
        manager.AddObject(hidden);

        IChartObject? clone = manager.DuplicateObject(hidden.Id);
        Assert.NotNull(clone);
        AssertInvariant(manager);
        Assert.True(manager.IsVisible(clone!.Id));
        Assert.False(manager.IsVisible(hidden.Id));

        IReadOnlyList<IChartObject> clones = manager.DuplicateObjects(new[] { hidden.Id });
        Assert.Single(clones);
        AssertInvariant(manager);
        Assert.True(manager.IsVisible(clones[0].Id));
        Assert.False(manager.IsVisible(hidden.Id));
    }

    [Fact]
    public void UndoAndRedoOfAVisibilityToggle_KeepTheInvariant()
    {
        var manager = new ChartObjectManager();
        var history = new DrawingHistoryService(TestKey);
        var session = new DrawingDocumentSession(TestKey);
        var layerService = new DrawingLayerService();
        using var coordinator = new DrawingEditCoordinator(manager, history, session, layerService, null);

        var line = Line();
        var other = Line(visible: false, y: 150);
        manager.AddObject(line);
        manager.AddObject(other);

        coordinator.ExecuteEdit(DrawingOperationKind.VisibilityToggle, () => manager.ToggleVisibility(line.Id));
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(line.Id));

        // Undo / Redo materialize fresh runtime objects (law P3-04): look them up by position, not by the old instance.
        Assert.True(coordinator.Undo().IsSuccess);
        AssertInvariant(manager);
        Assert.Equal(2, manager.Count);
        Assert.True(manager.IsVisible(manager.Objects[0].Id));
        Assert.False(manager.IsVisible(manager.Objects[1].Id));

        Assert.True(coordinator.Redo().IsSuccess);
        AssertInvariant(manager);
        Assert.False(manager.IsVisible(manager.Objects[0].Id));
        Assert.False(manager.IsVisible(manager.Objects[1].Id));
    }

    // ---------------- the render path follows the same rule ----------------

    private static byte[] RenderHash(ChartObjectManager manager)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        manager.Render(surface.Canvas, new PixelCoordinateTransform());
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return SHA256.HashData(bitmap.GetPixelSpan());
    }

    [Fact]
    public void Render_DrawsOnlyVisibleObjects_AndFollowsToggleHideAllShowAllAndLoad()
    {
        var empty = RenderHash(new ChartObjectManager());

        var manager = new ChartObjectManager();
        var line = Line();
        manager.AddObject(line);
        byte[] visibleHash = RenderHash(manager);
        Assert.False(empty.AsSpan().SequenceEqual(visibleHash), "A visible line must change the frame.");

        manager.ToggleVisibility(line.Id);
        Assert.True(empty.AsSpan().SequenceEqual(RenderHash(manager)), "A hidden line must not be drawn.");

        manager.ToggleVisibility(line.Id);
        Assert.True(visibleHash.AsSpan().SequenceEqual(RenderHash(manager)));

        manager.HideAll();
        Assert.True(empty.AsSpan().SequenceEqual(RenderHash(manager)));
        manager.ShowAll();
        Assert.True(visibleHash.AsSpan().SequenceEqual(RenderHash(manager)));

        var loaded = new ChartObjectManager();
        loaded.LoadSnapshot(new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new() { Line(visible: false) }
        });
        Assert.True(empty.AsSpan().SequenceEqual(RenderHash(loaded)), "An object loaded as hidden must not be drawn.");
    }
}
