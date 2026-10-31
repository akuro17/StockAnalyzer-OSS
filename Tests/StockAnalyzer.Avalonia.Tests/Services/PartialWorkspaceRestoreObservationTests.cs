using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// IoHydrationWebAiReview P-2 (observation only, no production change): what a workspace restore that fails half-way
/// leaves behind. The coordinator applies the stages in a fixed order and, on any exception, applies the default layout
/// (<c>RestoreLayoutSettings(null)</c>); the test records which target calls ran before the failure, which stages never
/// ran, and what the fallback does and does not undo. It prints the trace; the assertions pin the facts the plan's
/// question to the owner (D-3) depends on.
/// </summary>
public class PartialWorkspaceRestoreObservationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sa_p2_" + Guid.NewGuid().ToString("N") + ".json");

    public PartialWorkspaceRestoreObservationTests(ITestOutputHelper output)
    {
        _output = output;
        File.WriteAllText(_path, "{}"); // InitializeWorkspaceAsync only continues when the file exists
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static WorkspaceSettings SavedWorkspace() => new()
    {
        IsLeftPanelVisible = false,
        LeftPanelWidth = 480,
        IsRightPanelVisible = false,
        RightPanelWidth = 520,
        IsTopPanelVisible = true,
        TopPanelHeight = 260,
        IsBottomPanelVisible = false,
        BottomPanelHeight = 300,
        SelectedTicker = "MSFT",
        SelectedTimeframe = "Weekly",
        ChartType = "Line",
        Indicators = new List<CoreIndicatorSettings>(),
        WatchlistProfiles = new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>(),
        TickerListVisibleColumns = new List<string> { "Symbol", "Price" },
        TickerListSortColumn = "Symbol",
    };

    private static Mock<IWorkspaceLayoutTarget> NewTarget()
    {
        var target = new Mock<IWorkspaceLayoutTarget>();
        target.SetupAllProperties();
        target.Setup(t => t.LeftPanelTabs).Returns(new ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.RightPanelTabs).Returns(new ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.TopPanelTabs).Returns(new ObservableCollection<WorkspaceViewItem>());
        target.Setup(t => t.BottomPanelTabs).Returns(new ObservableCollection<WorkspaceViewItem>());
        return target;
    }

    private static string Describe(IInvocation invocation)
    {
        string name = invocation.Method.Name;
        if (name.StartsWith("set_", StringComparison.Ordinal))
            return $"{name[4..]} = {invocation.Arguments[0] ?? "null"}";
        if (name.StartsWith("get_", StringComparison.Ordinal))
            return string.Empty;
        return name + "()";
    }

    /// <summary>The ordered target calls that change state (property writes and stage methods), reads omitted.</summary>
    private static List<string> WriteTrace(Mock<IWorkspaceLayoutTarget> target)
        => target.Invocations.Select(Describe).Where(s => s.Length > 0).ToList();

    private async Task<(List<string> Trace, WorkspaceLifecycleState FinalState, Mock<IWorkspaceLayoutTarget> Target)> RunAsync(string failingStage)
    {
        var store = new LayoutStateStore();
        var serialization = new Mock<IWorkspaceSerializationService>();
        serialization.Setup(s => s.LoadAsync(It.IsAny<string>())).ReturnsAsync(SavedWorkspace());
        var coordinator = new WorkspaceCoordinator(
            store,
            serialization.Object,
            new Mock<IWatchlistManager>().Object,
            Options.Create(new WorkspaceCoordinatorOptions { WorkspacePath = _path }),
            new SynchronousDispatcherService(),
            NullLogger<WorkspaceCoordinator>.Instance);

        var target = NewTarget();
        var boom = new InvalidOperationException("injected stage failure: " + failingStage);
        switch (failingStage)
        {
            case nameof(IWorkspaceLayoutTarget.ApplyPanelTabs):
                target.Setup(t => t.ApplyPanelTabs(It.IsAny<WorkspaceSettings>())).Throws(boom); break;
            case nameof(IWorkspaceLayoutTarget.RestoreDetachedTabs):
                target.Setup(t => t.RestoreDetachedTabs(It.IsAny<WorkspaceSettings>())).Throws(boom); break;
            case nameof(IWorkspaceLayoutTarget.RefreshTickerListNodes):
                target.Setup(t => t.RefreshTickerListNodes()).Throws(boom); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failingStage));
        }
        coordinator.Bind(target.Object);

        await coordinator.InitializeWorkspaceAsync();

        return (WriteTrace(target), store.LifecycleState, target);
    }

    [Theory]
    [InlineData(nameof(IWorkspaceLayoutTarget.ApplyPanelTabs))]
    [InlineData(nameof(IWorkspaceLayoutTarget.RestoreDetachedTabs))]
    [InlineData(nameof(IWorkspaceLayoutTarget.RefreshTickerListNodes))]
    public async Task FailureInOneStage_TraceOfWhatRanWhatWasSkippedAndWhatTheFallbackUndid(string failingStage)
    {
        var (trace, finalState, target) = await RunAsync(failingStage);

        _output.WriteLine($"Failing stage: {failingStage}; lifecycle state afterwards: {finalState}");
        for (int i = 0; i < trace.Count; i++) _output.WriteLine($"{i + 1,3}. {trace[i]}");

        // The failure does not escape and the app returns to Ready (so auto-save is enabled again afterwards).
        Assert.Equal(WorkspaceLifecycleState.Ready, finalState);

        // Stages after the failing one never ran.
        string[] stageOrder = { "ApplyPanelTabs()", "RestoreDetachedTabs()", "RefreshTickerListNodes()" };
        int failingIndex = Array.IndexOf(stageOrder, failingStage + "()");
        Assert.Contains(stageOrder[failingIndex], trace); // the failing call itself was made
        foreach (var later in stageOrder.Skip(failingIndex + 1))
            Assert.DoesNotContain(later, trace);
        Assert.DoesNotContain(trace, t => t.StartsWith("SetTimeframe", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, t => t.StartsWith("SetActiveColumns", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, t => t.StartsWith("ApplySortState", StringComparison.Ordinal));

        // The fallback is the default layout only: it writes panel geometry/tab indices and nothing else.
        int fallbackStart = trace.FindLastIndex(t => t.StartsWith("IsLeftPanelVisible = True", StringComparison.Ordinal));
        Assert.True(fallbackStart > 0, "the default-layout fallback must run after the saved values were applied");
        var afterFallbackStart = trace.Skip(fallbackStart).ToList();
        Assert.DoesNotContain(afterFallbackStart, t => t.StartsWith("SelectedTicker", StringComparison.Ordinal));
        Assert.DoesNotContain(afterFallbackStart, t => t.StartsWith("ApplyIndicators", StringComparison.Ordinal));
        Assert.DoesNotContain(afterFallbackStart, t => t.StartsWith("RestoreChartSettings", StringComparison.Ordinal));
        Assert.DoesNotContain(afterFallbackStart, t => t.StartsWith("ApplyPanelTabs", StringComparison.Ordinal));

        // What the user is left with: the ticker and the chart type of the saved workspace stay, the geometry is the default.
        Assert.Equal("MSFT", target.Object.SelectedTicker);
        Assert.True(target.Object.IsLeftPanelVisible);
        Assert.Equal(LayoutConstants.DefaultLeftWidth, target.Object.LeftPanelWidth);
    }
}
