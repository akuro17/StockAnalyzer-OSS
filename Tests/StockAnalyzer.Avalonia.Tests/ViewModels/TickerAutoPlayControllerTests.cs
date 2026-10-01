using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Ticker Auto Play, Y:\Temp\sa_implementation_plan_TickerAutoPlay.md Phase 1): the pure
/// controller, driven deterministically through a recording scheduler (no real time).</summary>
public class TickerAutoPlayControllerTests
{
    private sealed class FakeHandle : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Harness
    {
        public List<string> Ordered { get; set; } = new() { "AAA", "BBB", "CCC" };
        public string? Selected { get; set; }
        public TickerAutoPlaySettingsSnapshot Settings { get; set; } = new(5, false, false);
        public List<string> SelectCalls { get; } = new();
        public Func<string, bool> SelectBehavior { get; set; }
        public List<(TimeSpan Due, Action Callback, FakeHandle Handle)> Scheduled { get; } = new();
        public int RunningChangedCount { get; private set; }
        public TickerAutoPlayController Sut { get; }

        public Harness()
        {
            SelectBehavior = symbol =>
            {
                Selected = symbol;
                return true;
            };
            Sut = new TickerAutoPlayController(
                () => Ordered.ToList(),
                () => Selected,
                symbol =>
                {
                    SelectCalls.Add(symbol);
                    return SelectBehavior(symbol);
                },
                () => Settings,
                (due, callback) =>
                {
                    var handle = new FakeHandle();
                    Scheduled.Add((due, callback, handle));
                    return handle;
                },
                new SynchronousDispatcherService());
            Sut.IsRunningChanged += (_, _) => RunningChangedCount++;
        }

        public void FireLatest() => Scheduled[^1].Callback();
    }

    [Fact]
    public void Start_WithNoSelection_SelectsFirstImmediately_AndSchedulesInterval()
    {
        var h = new Harness();

        Assert.True(h.Sut.Start());

        Assert.True(h.Sut.IsRunning);
        Assert.Equal(new[] { "AAA" }, h.SelectCalls);
        Assert.Single(h.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(5), h.Scheduled[0].Due);
    }

    [Fact]
    public void Start_WithSelectionInList_SelectsNothing()
    {
        var h = new Harness { Selected = "BBB" };

        h.Sut.Start();

        Assert.Empty(h.SelectCalls);
        Assert.Single(h.Scheduled);
    }

    [Fact]
    public void Start_WithSelectionAbsentFromList_SelectsFirst()
    {
        var h = new Harness { Selected = "ZZZ" };

        h.Sut.Start();

        Assert.Equal(new[] { "AAA" }, h.SelectCalls);
    }

    [Fact]
    public void Start_WithEmptyList_IsRefused()
    {
        var h = new Harness { Ordered = new List<string>() };

        Assert.False(h.Sut.Start());

        Assert.False(h.Sut.IsRunning);
        Assert.Empty(h.Scheduled);
        Assert.Equal(0, h.RunningChangedCount);
    }

    [Fact]
    public void Tick_AdvancesToTheNextSymbol()
    {
        var h = new Harness { Selected = "AAA" };
        h.Sut.Start();

        h.FireLatest();

        Assert.Equal(new[] { "BBB" }, h.SelectCalls);
        Assert.Equal(2, h.Scheduled.Count);
    }

    [Fact]
    public void Tick_OnLast_WithoutStopAtListEnd_WrapsToFirst()
    {
        var h = new Harness { Selected = "CCC" };
        h.Sut.Start();

        h.FireLatest();

        Assert.Equal(new[] { "AAA" }, h.SelectCalls);
        Assert.True(h.Sut.IsRunning);
    }

    [Fact]
    public void Tick_OnLast_WithStopAtListEnd_Stops()
    {
        var h = new Harness { Selected = "CCC", Settings = new(5, true, false) };
        h.Sut.Start();
        var scheduledBefore = h.Scheduled.Count;

        h.FireLatest();

        Assert.False(h.Sut.IsRunning);
        Assert.Empty(h.SelectCalls);
        Assert.Equal(scheduledBefore, h.Scheduled.Count);
        Assert.Equal(2, h.RunningChangedCount); // started, stopped
        Assert.True(h.Scheduled[0].Handle.Disposed);
    }

    [Fact]
    public void Tick_SingleElementList_DoesNotSelect_AndStopsOnlyWithStopAtListEnd()
    {
        var wrap = new Harness { Ordered = new List<string> { "AAA" }, Selected = "AAA" };
        wrap.Sut.Start();
        wrap.FireLatest();
        Assert.Empty(wrap.SelectCalls);
        Assert.True(wrap.Sut.IsRunning);
        Assert.Equal(2, wrap.Scheduled.Count);

        var stop = new Harness { Ordered = new List<string> { "AAA" }, Selected = "AAA", Settings = new(5, true, false) };
        stop.Sut.Start();
        stop.FireLatest();
        Assert.False(stop.Sut.IsRunning);
    }

    [Fact]
    public void Tick_WithEmptyList_Stops()
    {
        var h = new Harness { Selected = "AAA" };
        h.Sut.Start();
        h.Ordered = new List<string>();

        h.FireLatest();

        Assert.False(h.Sut.IsRunning);
    }

    [Fact]
    public void Tick_WhenCurrentIsNoLongerInTheList_SelectsFirst()
    {
        var h = new Harness { Selected = "CCC" };
        h.Sut.Start();
        h.Ordered = new List<string> { "XXX", "YYY" };

        h.FireLatest();

        Assert.Equal(new[] { "XXX" }, h.SelectCalls);
    }

    [Fact]
    public void ListTransition_StopsWhenConfigured_ElseRestartsTheCountdown()
    {
        var stops = new Harness { Selected = "AAA", Settings = new(5, false, true) };
        stops.Sut.Start();
        stops.Sut.NotifyListTransition();
        Assert.False(stops.Sut.IsRunning);

        var continues = new Harness { Selected = "AAA" };
        continues.Sut.Start();
        var first = continues.Scheduled[0];

        continues.Sut.NotifyListTransition();

        Assert.True(continues.Sut.IsRunning);
        Assert.True(first.Handle.Disposed);
        Assert.Equal(2, continues.Scheduled.Count);
    }

    [Fact]
    public void ListTransition_WhenStopped_DoesNothing()
    {
        var h = new Harness();

        h.Sut.NotifyListTransition();

        Assert.Empty(h.Scheduled);
    }

    [Fact]
    public void ExternalSelection_RestartsCountdown_ButOwnSelectionDoesNot()
    {
        var h = new Harness { Selected = "AAA" };
        // The real owner notifies on every selected-ticker change, including the ones this controller causes.
        h.SelectBehavior = symbol =>
        {
            h.Selected = symbol;
            h.Sut.NotifyExternalSelection();
            return true;
        };
        h.Sut.Start();
        h.FireLatest(); // own selection of BBB: exactly one re-arm
        Assert.Equal(2, h.Scheduled.Count);

        h.Selected = "CCC";
        h.Sut.NotifyExternalSelection();

        Assert.Equal(3, h.Scheduled.Count);
        h.FireLatest();
        Assert.Equal(new[] { "BBB", "AAA" }, h.SelectCalls); // continued from the external selection (CCC -> wrap)
    }

    [Fact]
    public void StaleCallback_AfterReArmOrStop_IsIgnored()
    {
        var h = new Harness { Selected = "AAA" };
        h.Sut.Start();
        var stale = h.Scheduled[0].Callback;
        h.Sut.NotifyExternalSelection(); // re-arm: the first callback is now stale

        stale();
        Assert.Empty(h.SelectCalls);

        h.Sut.Stop();
        h.FireLatest();
        Assert.Empty(h.SelectCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSelection_IsSwallowed_KeepsPlaying_AndAdvancesPastTheFailedSymbol(bool throws)
    {
        var h = new Harness { Selected = "AAA" };
        h.SelectBehavior = symbol =>
        {
            if (symbol == "BBB")
            {
                if (throws) throw new System.Security.SecurityException("Invalid symbol format detected.");
                return false; // selection did not move
            }
            h.Selected = symbol;
            return true;
        };
        h.Sut.Start();

        h.FireLatest(); // BBB fails, selection stays on AAA
        Assert.True(h.Sut.IsRunning);
        Assert.Equal(2, h.Scheduled.Count);

        h.FireLatest(); // continues past the failed symbol instead of retrying it

        Assert.Equal(new[] { "BBB", "CCC" }, h.SelectCalls);
    }

    [Fact]
    public void SymbolLookup_IsOrdinalAndCaseSensitive()
    {
        var h = new Harness { Selected = "aaa" };

        h.Sut.Start();

        Assert.Equal(new[] { "AAA" }, h.SelectCalls); // "aaa" is not "AAA": treated as not in the list
    }

    [Fact]
    public void Dispose_StopsAndIgnoresLaterCalls()
    {
        var h = new Harness { Selected = "AAA" };
        h.Sut.Start();
        var scheduled = h.Scheduled.Count;

        h.Sut.Dispose();
        h.FireLatest();
        h.Sut.NotifyExternalSelection();
        h.Sut.NotifyListTransition();

        Assert.False(h.Sut.IsRunning);
        Assert.False(h.Sut.Start());
        Assert.Empty(h.SelectCalls);
        Assert.Equal(scheduled, h.Scheduled.Count);
    }

    [Fact]
    public void IntervalChange_IsUsedAtTheNextArm()
    {
        var h = new Harness { Selected = "AAA" };
        h.Sut.Start();
        h.Settings = new(9, false, false);

        h.FireLatest();

        Assert.Equal(TimeSpan.FromSeconds(9), h.Scheduled[^1].Due);
    }

    [Fact]
    public void StartTwice_SchedulesOnce_AndStopTwice_RaisesOnce()
    {
        var h = new Harness { Selected = "AAA" };

        h.Sut.Start();
        h.Sut.Start();
        Assert.Single(h.Scheduled);

        h.Sut.Stop();
        h.Sut.Stop();
        Assert.Equal(2, h.RunningChangedCount);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new TickerAutoPlayController(
            null!, () => null, _ => true, () => new(1, false, false), (_, _) => new FakeHandle(), new SynchronousDispatcherService()));
        Assert.Throws<ArgumentNullException>(() => new TickerAutoPlayController(
            () => new List<string>(), () => null, _ => true, () => new(1, false, false), (_, _) => new FakeHandle(), null!));
    }

    [Fact]
    public void ScheduleWithThreadingTimer_FiresOnceAndCanBeDisposed()
    {
        using var fired = new System.Threading.ManualResetEventSlim(false);
        using var handle = TickerAutoPlayController.ScheduleWithThreadingTimer(TimeSpan.FromMilliseconds(10), () => fired.Set());

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));

        var neverFired = false;
        TickerAutoPlayController.ScheduleWithThreadingTimer(TimeSpan.FromMinutes(10), () => neverFired = true).Dispose();
        Assert.False(neverFired);
    }
}
