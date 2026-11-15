using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Removing a drawing object while a frame is being rendered must not free the object under the renderer. The manager defers the
/// disposal to the end of the render scope (all <see cref="IDisposable"/> drawing objects are covered, current and future), the
/// render loop walks a snapshot so a shrinking list cannot be indexed out of range, and the scope counter always returns to zero.
/// The checks run through <see cref="ChartObjectManager"/> with a wrapper that never forwards a render after its dispose, so a missing
/// protection fails the test instead of ending the test host with an AccessViolationException.
/// </summary>
public sealed class ChartObjectManagerRenderScopeTests
{
    private const int Width = 200;
    private const int Height = 100;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private const int MutationCycles = 4000;

    public static IEnumerable<object[]> DisposableDrawingTypes()
        => ChartObjectTypeRegistry.GetRegisteredTypes()
            .Where(kv => typeof(IDisposable).IsAssignableFrom(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new object[] { kv.Key });

    [Fact]
    public void DiscoveryFindsDisposableDrawingTypes()
    {
        // Guards the theory below against silently testing nothing (the registry scan is the same one persistence uses).
        Assert.True(DisposableDrawingTypes().Count() >= 6, "Fewer disposable drawing types than the six that are known to exist.");
    }

    private static void RenderOnce(ChartObjectManager manager)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        manager.Render(surface.Canvas, new PixelCoordinateTransform());
    }

    /// <summary>Starts a frame whose first object blocks until released; returns once the frame is inside that object.</summary>
    private sealed class BlockedFrame : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Task _frame;
        private readonly Action? _beforeBlocking;

        public BlockedFrame(ChartObjectManager manager, Action? beforeBlocking = null, bool throwAfterRelease = false)
        {
            _beforeBlocking = beforeBlocking;
            Blocker = new RenderLifetimeSpy(onRender: () =>
            {
                _beforeBlocking?.Invoke();
                _entered.Set();
                if (!_release.Wait(WaitLimit)) throw new TimeoutException("The blocked frame was never released.");
                if (throwAfterRelease) throw new InvalidOperationException("render failure injected by the test");
            });
            Assert.True(manager.AddObject(Blocker));
            _frame = new Task(() => RenderOnce(manager), TaskCreationOptions.LongRunning);
        }

        public RenderLifetimeSpy Blocker { get; }

        public void Start()
        {
            _frame.Start();
            Assert.True(_entered.Wait(WaitLimit), "The frame never reached the blocking object.");
        }

        /// <summary>Releases the frame and returns the exception the render thread ended with (null when it finished normally).</summary>
        public Exception? Finish()
        {
            _release.Set();
            try
            {
                _frame.Wait(WaitLimit);
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

    // ---------------- 1. every IDisposable drawing type: disposal waits for the frame in flight ----------------

    [Theory]
    [MemberData(nameof(DisposableDrawingTypes))]
    public void RemoveObject_DuringAFrame_DefersTheDispose_ForEveryDisposableDrawingType(string typeName)
    {
        Type type = ChartObjectTypeRegistry.Resolve(typeName)!;
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy(ChartObjectInstanceFactory.Create(type));
        using var frame = new BlockedFrame(manager);
        Assert.True(manager.AddObject(target));
        frame.Start();

        Assert.True(manager.RemoveObject(target.Id));
        int disposesWhileTheFrameRuns = target.DisposeCount;
        Exception? renderFailure = frame.Finish();

        Assert.True(disposesWhileTheFrameRuns == 0, $"{typeName} was disposed while a frame was still rendering.");
        Assert.True(renderFailure is null, $"{typeName}: the frame failed after the removal: {renderFailure}");
        Assert.False(target.RenderedAfterDispose, $"{typeName} was rendered after it was disposed.");
        Assert.Equal(1, target.DisposeCount);
    }

    // ---------------- 2. every removal path defers; without a frame nothing changes ----------------

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void EveryRemovalPath_DefersTheDispose_UntilTheFrameEnds(string path)
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        using var frame = new BlockedFrame(manager);
        Assert.True(manager.AddObject(target));
        frame.Start();

        RetirementPaths.Retire(manager, path, target);
        int disposesWhileTheFrameRuns = target.DisposeCount;
        Exception? renderFailure = frame.Finish();

        Assert.True(disposesWhileTheFrameRuns == 0, $"{path}: the object was disposed while a frame was still rendering.");
        Assert.True(renderFailure is null, $"{path}: the frame failed: {renderFailure}");
        Assert.False(target.RenderedAfterDispose);
        Assert.Equal(1, target.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(RetirementPaths.All), MemberType = typeof(RetirementPaths))]
    public void EveryRemovalPath_DisposesImmediately_WhenNoFrameIsRendering(string path)
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        Assert.True(manager.AddObject(target));
        RenderOnce(manager);   // a finished frame must leave no pending scope behind

        RetirementPaths.Retire(manager, path, target);

        Assert.Equal(1, target.DisposeCount);
    }

    [Fact]
    public void AnObjectStillManaged_IsNeverDisposed_ByTheDeferredPath()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        using var frame = new BlockedFrame(manager);
        Assert.True(manager.AddObject(target));
        frame.Start();

        // Moved to another context's list and removed from the current one: it is still managed, so it must stay alive afterwards.
        var other = new Dictionary<ChartDrawingContextType, List<IChartObject>> { [ChartDrawingContextType.Standard] = new(), [ChartDrawingContextType.Linear] = new() { target } };
        manager.LoadSnapshot(other);
        frame.Finish();

        Assert.Equal(0, target.DisposeCount);
    }

    // ---------------- 3. the counter is released on every exit ----------------

    [Fact]
    public void AFailingRender_StillRunsTheDeferredDispose_AndReleasesTheScope()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        var later = new RenderLifetimeSpy();
        using var frame = new BlockedFrame(manager, throwAfterRelease: true);
        Assert.True(manager.AddObject(target));
        Assert.True(manager.AddObject(later));
        frame.Start();

        Assert.True(manager.RemoveObject(target.Id));
        Assert.Equal(0, target.DisposeCount);
        Exception? renderFailure = frame.Finish();

        Assert.IsType<InvalidOperationException>(renderFailure);
        Assert.Equal(1, target.DisposeCount);

        // A leaked scope would defer this removal forever.
        Assert.True(manager.RemoveObject(later.Id));
        Assert.Equal(1, later.DisposeCount);
    }

    [Fact]
    public void ARemovalInsideTheRenderCallback_IsDisposedWhenTheFrameEnds_NotWithinIt()
    {
        var manager = new ChartObjectManager();
        var target = new RenderLifetimeSpy();
        int disposesInsideTheCallback = -1;
        using var frame = new BlockedFrame(manager, beforeBlocking: () =>
        {
            manager.RemoveObject(target.Id);
            disposesInsideTheCallback = target.DisposeCount;
        });
        Assert.True(manager.AddObject(target));
        frame.Start();
        Exception? renderFailure = frame.Finish();

        Assert.Null(renderFailure);
        Assert.Equal(0, disposesInsideTheCallback);
        Assert.Equal(1, target.DisposeCount);
        Assert.False(target.RenderedAfterDispose);
    }

    // ---------------- 4. the list may shrink / reorder while it is rendered ----------------

    [Fact]
    public async Task ConcurrentListChanges_NeverBreakTheRender_AndNothingIsRenderedAfterItsDispose()
    {
        var manager = new ChartObjectManager();
        var removed = new List<RenderLifetimeSpy>();
        var keepers = Enumerable.Range(0, 6).Select(_ => new RenderLifetimeSpy()).ToList();
        foreach (RenderLifetimeSpy keeper in keepers) Assert.True(manager.AddObject(keeper));

        using var stop = new CancellationTokenSource();
        Exception? renderFailure = null;
        Task renderer = Task.Run(() =>
        {
            try
            {
                using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
                var transform = new PixelCoordinateTransform();
                while (!stop.IsCancellationRequested)
                {
                    manager.Render(surface.Canvas, transform);
                }
            }
            catch (Exception ex)
            {
                renderFailure = ex;
            }
        });

        for (int i = 0; i < MutationCycles && renderFailure is null; i++)
        {
            var transient = new RenderLifetimeSpy();
            removed.Add(transient);
            manager.AddObject(transient);
            manager.BringToFront(keepers[i % keepers.Count].Id);
            manager.SendToBack(keepers[(i + 1) % keepers.Count].Id);
            manager.RemoveObject(transient.Id);
        }

        stop.Cancel();
        await renderer;
        RenderOnce(manager);   // a frame after the last mutation releases anything still deferred

        Assert.Null(renderFailure);
        Assert.All(removed, spy =>
        {
            Assert.False(spy.RenderedAfterDispose);
            Assert.Equal(1, spy.DisposeCount);
        });
        Assert.All(keepers, spy => Assert.Equal(0, spy.DisposeCount));
    }
}
