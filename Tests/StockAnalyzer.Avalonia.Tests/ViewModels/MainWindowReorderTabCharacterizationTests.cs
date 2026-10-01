using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the observable behavior of <c>MainWindowViewModel.ReorderTab</c> (panel tab reordering) against an
/// independent reference model, so that replacing its inline clamp rules by the shared
/// <c>TabMoveResolver</c> can be proven behavior-preserving.
/// </summary>
[Collection("MessengerSharedState")]
public class MainWindowReorderTabCharacterizationTests
{
    /// <summary>
    /// Reference model written from the documented rules (not from TabMoveResolver):
    /// equal indices -> nothing; source out of range -> nothing; distance limited to <paramref name="maxDistance"/>;
    /// target clamped to [0, count-1]; equal after clamping -> nothing. Returns the resulting target or null.
    /// </summary>
    private static int? ExpectedTarget(int count, int from, int to, int maxDistance)
    {
        if (from == to) return null;
        if (from < 0 || from >= count) return null;

        long target = to;
        if (Math.Abs((long)to - from) > maxDistance)
        {
            target = from + (long)maxDistance * (to > from ? 1 : -1);
        }

        if (target < 0) target = 0;
        if (target >= count) target = count - 1;

        return target == from ? null : (int)target;
    }

    private static List<string> ExpectedOrder(int count, int from, int target)
    {
        var order = Enumerable.Range(0, count).Select(i => $"T{i}").ToList();
        var moved = order[from];
        order.RemoveAt(from);
        order.Insert(target, moved);
        return order;
    }

    private static void AssertReorder(int count, int from, int to)
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;

        // A panel never holds more than MaxPanelTabs tabs (add and redock enforce it), so larger counts are unreachable.
        Assert.InRange(count, 0, h.Store.MaxPanelTabs);

        vm.LeftPanelTabs.Clear();
        for (int i = 0; i < count; i++) vm.LeftPanelTabs.Add(TestWorkspaceItems.Create($"T{i}"));
        int selectionBefore = vm.LeftSelectedTabIndex;
        h.Scheduler.Invocations.Clear();

        var expected = ExpectedTarget(count, from, to, h.Store.MaxTabReorderDistance);
        string label = $"count={count} from={from} to={to}";

        vm.ReorderTabCommand.Execute($"Left:{from}:{to}");

        if (expected is null)
        {
            Assert.True(
                vm.LeftPanelTabs.Select(t => t.Id).SequenceEqual(Enumerable.Range(0, count).Select(i => $"T{i}")),
                $"order must be unchanged ({label})");
            Assert.True(selectionBefore == vm.LeftSelectedTabIndex, $"selection must be unchanged ({label})");
            h.Scheduler.Verify(s => s.RequestSave(It.IsAny<LayoutChangeReason>()), Times.Never);
            return;
        }

        Assert.True(
            vm.LeftPanelTabs.Select(t => t.Id).SequenceEqual(ExpectedOrder(count, from, expected.Value)),
            $"order after move ({label})");
        Assert.True(expected.Value == vm.LeftSelectedTabIndex, $"selection follows the moved tab ({label})");

        // One explicit TabMoved save, plus one from the selected-index setter when the index actually changed.
        int expectedSaves = selectionBefore == expected.Value ? 1 : 2;
        h.Scheduler.Verify(s => s.RequestSave(LayoutChangeReason.TabMoved), Times.Exactly(expectedSaves));
    }

    [Fact]
    public void ReorderTab_SmallGrid_MatchesReferenceModel()
    {
        foreach (int count in new[] { 0, 1, 2, 5 })
        {
            for (int from = -1; from <= count; from++)
            {
                for (int to = -2; to <= count + 1; to++)
                {
                    AssertReorder(count, from, to);
                }
            }
        }
    }

    [Fact]
    public void ReorderTab_FullPanel_MatchesReferenceModel()
    {
        int count = new LayoutSettings().MaxPanelTabs;
        foreach (var (from, to) in new[] { (0, count - 1), (count - 1, 0), (3, 1000), (count - 2, -1000), (5, 5) })
        {
            AssertReorder(count, from, to);
        }
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ReorderTab_ExtremeTarget_ClampsToEdgeWithoutWrapping(int to)
    {
        // Documented equivalence edge: distance arithmetic must not overflow for extreme parsed indices.
        AssertReorder(5, 2, to);
    }

    [Theory]
    [InlineData("Right")]
    [InlineData("Top")]
    [InlineData("Bottom")]
    public void ReorderTab_OtherRegions_MoveTheirOwnCollectionAndSelection(string region)
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        var tabs = region switch
        {
            "Right" => vm.RightPanelTabs,
            "Top" => vm.TopPanelTabs,
            _ => vm.BottomPanelTabs
        };
        tabs.Clear();
        for (int i = 0; i < 4; i++) tabs.Add(TestWorkspaceItems.Create($"T{i}"));

        vm.ReorderTabCommand.Execute($"{region}:3:0");

        Assert.Equal(new[] { "T3", "T0", "T1", "T2" }, tabs.Select(t => t.Id).ToArray());
        int selected = region switch
        {
            "Right" => vm.RightSelectedTabIndex,
            "Top" => vm.TopSelectedTabIndex,
            _ => vm.BottomSelectedTabIndex
        };
        Assert.Equal(0, selected);
        Assert.Empty(vm.LeftPanelTabs.Where(t => t.Id == "T3"));
    }
}
