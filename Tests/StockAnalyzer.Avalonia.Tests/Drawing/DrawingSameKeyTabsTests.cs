using System;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// AT09: two tabs (coordinators with their own object managers) on the same drawing key coordinate through one
/// <see cref="DrawingDocumentSession"/>; different keys never share history.
/// </summary>
public class DrawingSameKeyTabsTests
{
    private static readonly DrawingDocumentKey KeyA = new("7203", TimeframeType.Daily);
    private static readonly DrawingDocumentKey KeyB = new("9984", TimeframeType.Daily);

    private static TrendLineObject CreateLine(decimal price1 = 1000m, decimal price2 = 1200m) => new(
        new ChartPoint(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), price1),
        new ChartPoint(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), price2))
    {
        Color = Colors.Blue,
        Thickness = 2.0
    };

    private static (ChartObjectManager Manager, DrawingEditCoordinator Coordinator) CreateTab(
        DrawingDocumentSessionStore store, DrawingDocumentKey key)
    {
        var session = store.GetOrCreate(key);
        var manager = new ChartObjectManager();
        var history = session.GetOrCreateHistory(manager.CurrentContext);
        return (manager, new DrawingEditCoordinator(manager, history, session, sessionStore: store));
    }

    [Fact]
    public void SameKeyTabs_SecondEditIsBusyUntilTheFirstCommits()
    {
        var store = new DrawingDocumentSessionStore();
        var (managerA, tabA) = CreateTab(store, KeyA);
        var (_, tabB) = CreateTab(store, KeyA);
        using (tabA)
        using (tabB)
        {
            var beginA = tabA.BeginEdit(DrawingOperationKind.Add);
            Assert.True(beginA.IsSuccess);

            Assert.Equal(DrawingCommandStatus.Busy, tabB.BeginEdit(DrawingOperationKind.Add).Status);

            managerA.AddObject(CreateLine());
            Assert.True(tabA.Commit(beginA.Token).IsSuccess);

            var beginB = tabB.BeginEdit(DrawingOperationKind.Add);
            Assert.True(beginB.IsSuccess);
            Assert.True(tabB.Cancel(beginB.Token).IsSuccess);
        }
    }

    [Fact]
    public void SameKeyTabs_CancelReleasesTheDocumentOwnershipForTheOtherTab()
    {
        var store = new DrawingDocumentSessionStore();
        var (_, tabA) = CreateTab(store, KeyA);
        var (_, tabB) = CreateTab(store, KeyA);
        using (tabA)
        using (tabB)
        {
            var beginA = tabA.BeginEdit(DrawingOperationKind.Add);
            Assert.True(beginA.IsSuccess);
            Assert.Equal(DrawingCommandStatus.Busy, tabB.BeginEdit(DrawingOperationKind.Add).Status);

            Assert.True(tabA.Cancel(beginA.Token).IsSuccess);

            Assert.True(tabB.BeginEdit(DrawingOperationKind.Add).IsSuccess);
        }
    }

    [Fact]
    public void SameKeyTabs_ShareOneHistoryOfCommittedEdits()
    {
        var store = new DrawingDocumentSessionStore();
        var (managerA, tabA) = CreateTab(store, KeyA);
        var (_, tabB) = CreateTab(store, KeyA);
        using (tabA)
        using (tabB)
        {
            tabA.ExecuteEdit(DrawingOperationKind.Add, () => managerA.AddObject(CreateLine()));

            Assert.Equal(1, tabA.UndoCount);
            Assert.Equal(1, tabB.UndoCount);
            Assert.Same(store.GetOrCreate(KeyA), store.GetOrCreate(new DrawingDocumentKey("7203", TimeframeType.Daily)));
        }
    }

    [Fact]
    public void DifferentKeys_HaveIsolatedHistories()
    {
        var store = new DrawingDocumentSessionStore();
        var (managerA, tabA) = CreateTab(store, KeyA);
        var (managerB, tabB) = CreateTab(store, KeyB);
        using (tabA)
        using (tabB)
        {
            tabA.ExecuteEdit(DrawingOperationKind.Add, () => managerA.AddObject(CreateLine()));

            Assert.Equal(1, tabA.UndoCount);
            Assert.Equal(0, tabB.UndoCount);
            Assert.False(tabB.CanUndo);

            tabB.ExecuteEdit(DrawingOperationKind.Add, () => managerB.AddObject(CreateLine(2000m, 2200m)));
            Assert.True(tabA.Undo().IsSuccess);

            Assert.Equal(0, tabA.UndoCount);
            Assert.Equal(1, tabB.UndoCount);
            Assert.Single(managerB.Objects);
        }
    }

    [Fact]
    public void SwitchingKeysAndBack_RetainsTheOriginalKeyHistory()
    {
        var store = new DrawingDocumentSessionStore();
        var (managerA, tabA) = CreateTab(store, KeyA);
        tabA.ExecuteEdit(DrawingOperationKind.Add, () => managerA.AddObject(CreateLine()));
        tabA.Dispose(); // tab leaves key A (e.g. symbol switch); the shared session must survive

        var (_, tabOther) = CreateTab(store, KeyB);
        Assert.Equal(0, tabOther.UndoCount);
        tabOther.Dispose();

        var (_, tabBack) = CreateTab(store, KeyA);
        using (tabBack)
        {
            Assert.Equal(1, tabBack.UndoCount);
        }
    }
}
