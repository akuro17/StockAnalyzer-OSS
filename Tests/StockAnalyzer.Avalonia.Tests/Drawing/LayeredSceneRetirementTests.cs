using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The live chart does not draw through <c>ChartObjectManager.RenderPanel</c> (which has the render scope): <c>ChartBaseControl</c> always hands its
/// <see cref="LayeredDrawingScene"/> to the pipeline, which draws through <c>LayeredDrawingScene.RenderPanel</c> (D-3, SA_RENDERING_PERFORMANCE). These tests
/// pin what must hold on that path: an object the manager retires is neither freed under a frame that is rendering it from the scene, nor drawn from a
/// scene array that was built before the removal; the scene is told before the manager decides about the disposal, on every removal path; the scope of a
/// frame is always left, also when the frame fails. Every case goes through a spy that records a render after the dispose and never forwards it, so a
/// missing protection fails the test instead of ending the test host with an AccessViolationException.
/// The scene is built the way <c>ChartBaseControl.UpdateLayeredScene</c> builds it (one layer of the main panel holding every object of the manager) and
/// registered the way <c>ChartBaseControl.OnViewModelChanged</c> registers it (<see cref="ChartObjectManager.AttachRetirementListener"/>); a frame is
/// what <c>ChartDrawOperation.Render</c> does (open a scope, then draw the scene).
/// </summary>
public sealed class LayeredSceneRetirementTests
{
    private const int Width = 200;
    private const int Height = 100;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>A scene built from the objects the manager holds now, registered with the manager until disposed.</summary>
    private sealed class AttachedScene : IDisposable
    {
        private readonly IDisposable _registration;

        public AttachedScene(ChartObjectManager manager)
        {
            IReadOnlyList<IChartObject> objects = manager.Objects;
            var byId = new Dictionary<Guid, IChartObject>(objects.Count);
            var ids = new List<Guid>(objects.Count);
            foreach (IChartObject chartObject in objects)
            {
                byId[chartObject.Id] = chartObject;
                ids.Add(chartObject.Id);
            }

            var layer = new DrawingLayerRecord(Guid.NewGuid(), "Layer", PanelKey.Main, isVisible: true, isEditLocked: false, ids);
            Scene = new LayeredDrawingScene();
            Scene.UpdateScene(manager.CurrentContext, 1, 1, new[] { layer }, byId);
            _registration = manager.AttachRetirementListener(Scene);
        }

        public LayeredDrawingScene Scene { get; }

        public void Dispose() => _registration.Dispose();
    }

    /// <summary>What <c>ChartDrawOperation.Render</c> does: the scope is entered before the scene arrays are read and left in the compiler's finally.</summary>
    private static void RenderFrame(ChartObjectManager manager, LayeredDrawingScene scene)
    {
        using var scope = manager.BeginRenderScope();
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        scene.RenderPanel(surface.Canvas, PanelKey.Main, new PixelCoordinateTransform());
    }

    /// <summary>
    /// A frame that draws the scene on its own thread and blocks inside its first object until released; the frame has read the scene arrays
    /// by then, so it holds the objects that were in the scene when it started.
    /// </summary>
    private sealed class BlockedSceneFrame : IDisposable
    {
        private readonly ChartObjectManager _manager;
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private Task? _frame;

        /// <summary>Adds the blocking object to <paramref name="manager"/> first, so it is drawn before every object the test adds later.</summary>
        public BlockedSceneFrame(ChartObjectManager manager)
        {
            _manager = manager;
            Blocker = new RenderLifetimeSpy(onRender: () =>
            {
                _entered.Set();
                if (!_release.Wait(WaitLimit)) throw new TimeoutException("The blocked frame was never released.");
            });
            Assert.True(manager.AddObject(Blocker));
        }

        public RenderLifetimeSpy Blocker { get; }

        public void Start(LayeredDrawingScene scene)
        {
            _frame = new Task(() => RenderFrame(_manager, scene), TaskCreationOptions.LongRunning);
            _frame.Start();
            Assert.True(_entered.Wait(WaitLimit), "The frame never reached the blocking object.");
        }

        /// <summary>Releases the frame and returns the exception the render thread ended with (null when it finished normally).</summary>
        public Exception? Finish()
        {
            _release.Set();
            try
            {
                _frame!.Wait(WaitLimit);
                return null;
            }
            catch (AggregateException ex)
            {
                return ex.GetBaseException();
            }
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class RecordingListener : IRenderSceneRetirementListener
    {
        private readonly List<string> _log;

        public RecordingListener(List<string> log) => _log = log;

        public void OnObjectsRetired(IReadOnlyList<IChartObject> retiredObjects) => _log.Add("retired");
    }

    // ---------------- 1. a frame that is drawing the scene holds the object: the manager must not free it meanwhile ----------------

    [Theory]
    [MemberData(nameof(ChartObjectManagerRenderScopeTests.DisposableDrawingTypes), MemberType = typeof(ChartObjectManagerRenderScopeTests))]
    public void RemoveObject_DuringASceneFrame_DefersTheDispose_ForEveryDisposableDrawingType(string typeName)
    {
        Type type = ChartObjectTypeRegistry.Resolve(typeName)!;
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy(ChartObjectInstanceFactory.Create(type));
        using var frame = new BlockedSceneFrame(manager);
        Assert.True(manager.AddObject(target));
        using var attached = new AttachedScene(manager);
        frame.Start(attached.Scene);

        Assert.True(manager.RemoveObject(target.Id));
        int disposesWhileTheFrameRuns = target.DisposeCount;
        Exception? renderFailure = frame.Finish();

        Assert.True(disposesWhileTheFrameRuns == 0, $"{typeName} was disposed while a scene frame was still rendering.");
        Assert.True(renderFailure is null, $"{typeName}: the frame failed after the removal: {renderFailure}");
        Assert.False(target.RenderedAfterDispose, $"{typeName} was rendered after it was disposed.");
        Assert.Equal(1, target.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void EveryRetirementPath_DefersTheDispose_UntilTheSceneFrameEnds(string path)
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        using var frame = new BlockedSceneFrame(manager);
        Assert.True(manager.AddObject(target));
        using var attached = new AttachedScene(manager);
        frame.Start(attached.Scene);

        RetirementPaths.Retire(manager, path, target);
        int disposesWhileTheFrameRuns = target.DisposeCount;
        Exception? renderFailure = frame.Finish();

        Assert.True(disposesWhileTheFrameRuns == 0, $"{path}: the object was disposed while a scene frame was still rendering.");
        Assert.True(renderFailure is null, $"{path}: the frame failed: {renderFailure}");
        Assert.False(target.RenderedAfterDispose, $"{path}: the object was rendered after it was disposed.");
        Assert.Equal(1, target.DisposeCount);
    }

    // ---------------- 2. the scene array may not outlive the removal: nothing is drawn from a scene built before it ----------------

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void AnObjectRetiredBeforeAFrame_IsNeverRenderedFromTheSceneBuiltBeforeTheRemoval(string path)
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(target));
        using var attached = new AttachedScene(manager);   // the control rebuilds it only on its next UpdateLayeredScene

        RetirementPaths.Retire(manager, path, target);     // no frame in flight: the object is disposed at once
        RenderFrame(manager, attached.Scene);

        Assert.False(target.RenderedAfterDispose, $"{path}: the scene drew an object the manager had already disposed.");
        Assert.Equal(0, target.RenderCount);
        Assert.Equal(1, target.DisposeCount);
    }

    [Fact]
    public void AnObjectThatLoadSnapshotPutsBack_StaysInTheScene()
    {
        var manager = new ChartObjectManager();
        var keeper = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(keeper));
        using var attached = new AttachedScene(manager);

        manager.LoadSnapshot(new Dictionary<ChartDrawingContextType, List<IChartObject>> { [ChartDrawingContextType.Standard] = new() { keeper } });
        RenderFrame(manager, attached.Scene);

        Assert.Equal(0, keeper.DisposeCount);
        Assert.Equal(1, keeper.RenderCount);
    }

    // ---------------- 3. the order on every path: the scene is told first, then the disposal is decided ----------------

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void EveryRetirementPath_TellsTheSceneBeforeItDisposes(string path)
    {
        var log = new List<string>();
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy(onDispose: () => log.Add("dispose"));
        Assert.True(manager.AddObject(target));
        using var registration = manager.AttachRetirementListener(new RecordingListener(log));

        RetirementPaths.Retire(manager, path, target);

        Assert.Equal(new[] { "retired", "dispose" }, log);
    }

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void EveryRetirementPath_TellsTheSceneAtOnce_AndDisposesOnlyWhenTheScopeEnds(string path)
    {
        var log = new List<string>();
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy(onDispose: () => log.Add("dispose"));
        Assert.True(manager.AddObject(target));
        using var registration = manager.AttachRetirementListener(new RecordingListener(log));

        using (manager.BeginRenderScope())
        {
            RetirementPaths.Retire(manager, path, target);
            Assert.Equal(new[] { "retired" }, log);
        }

        Assert.Equal(new[] { "retired", "dispose" }, log);
    }

    [Fact]
    public void ADetachedListener_IsNotToldAnyMore()
    {
        var log = new List<string>();
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(target));
        IDisposable registration = manager.AttachRetirementListener(new RecordingListener(log));

        registration.Dispose();
        registration.Dispose();   // idempotent
        Assert.True(manager.RemoveObject(target.Id));

        Assert.Empty(log);
        Assert.Equal(0, manager.RetirementListenerCount);
    }

    // ---------------- 4. the scope is always left ----------------

    [Fact]
    public void ARenderScopeLeftByAnException_StillRunsTheDeferredDisposal_AndLeavesNoScopeBehind()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(target));

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = manager.BeginRenderScope();
            Assert.True(manager.RemoveObject(target.Id));
            Assert.Equal(0, target.DisposeCount);
            throw new InvalidOperationException("render failure injected by the test");
        }));

        Assert.Equal(1, target.DisposeCount);
        var later = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(later));
        Assert.True(manager.RemoveObject(later.Id));
        Assert.Equal(1, later.DisposeCount);   // the counter is back at zero: nothing is deferred any more
    }

    [Fact]
    public void ADisposalThatFailsWhenTheLastScopeEnds_DoesNotThrowOutOfTheFrame()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy(onDispose: () => throw new InvalidOperationException("dispose failure injected by the test"));
        Assert.True(manager.AddObject(target));

        using (manager.BeginRenderScope())
        {
            Assert.True(manager.RemoveObject(target.Id));
        }   // would throw here if the failure were rethrown on the render thread

        Assert.Equal(1, target.DisposeCount);
    }

    [Fact]
    public void ARenderPanelInsideAHostScope_DoesNotLetTheDisposalRunBeforeTheHostScopeEnds()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(target));

        using (manager.BeginRenderScope())
        {
            using (var surface = SKSurface.Create(new SKImageInfo(Width, Height)))
            {
                manager.Render(surface.Canvas, new PixelCoordinateTransform());   // opens and leaves its own scope inside the host scope
            }

            Assert.True(manager.RemoveObject(target.Id));
            Assert.Equal(0, target.DisposeCount);
        }

        Assert.Equal(1, target.DisposeCount);
    }
}
