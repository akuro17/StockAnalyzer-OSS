using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Services;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StockAnalyzer.Tests;

public class MainWindowViewModelCounterTests
{
    private static void ResetCounterState(int value, int initializedState)
    {
        var type = typeof(MainWindowViewModel);
        var counterField = type.GetField("_chartInstanceCounter", BindingFlags.Static | BindingFlags.NonPublic);
        var initializedField = type.GetField("_isCounterInitialized", BindingFlags.Static | BindingFlags.NonPublic);

        counterField?.SetValue(null, value);
        initializedField?.SetValue(null, initializedState);
    }

    [Fact]
    public void ThreadSafety_ConcurrentTokenGeneration_ShouldProduceUniqueIDs()
    {
        // Arrange
        ResetCounterState(0, 1); // Set initialized, start from 0
        var generatedIds = new ConcurrentBag<int>();
        int numThreads = 10;
        int numIterations = 100;

        // Act
        Parallel.For(0, numThreads, _ =>
        {
            for (int i = 0; i < numIterations; i++)
            {
                generatedIds.Add(MainWindowViewModel.GenerateNextChartId());
            }
        });

        // Assert
        Assert.Equal(numThreads * numIterations, generatedIds.Distinct().Count());
        Assert.All(generatedIds, id => Assert.True(id > 0));
    }

    [Fact]
    public void OverflowGuard_ShouldThrowException_WhenLimitsExceeded()
    {
        // Arrange
        ResetCounterState(int.MaxValue - 3, 1);

        // Act
        int firstId = MainWindowViewModel.GenerateNextChartId(); // Should yield int.MaxValue - 2

        // Assert
        Assert.Equal(int.MaxValue - 2, firstId);
        Assert.Throws<OverflowException>(() => MainWindowViewModel.GenerateNextChartId());
    }

    [Fact]
    public void DoubleInitialization_ShouldProtectAndMaintainFirstValue()
    {
        // Arrange
        ResetCounterState(0, 0); // Completely reset

        // Act
        MainWindowViewModel.InitializeChartInstanceCounter(100);
        MainWindowViewModel.InitializeChartInstanceCounter(200); // Should be ignored

        // Assert
        int nextId = MainWindowViewModel.GenerateNextChartId(); // Increment from 100 -> 101
        Assert.Equal(101, nextId);
    }

    [Fact]
    public void AutoSaveSuspensionScope_ShouldPauseAndResume_ReentrantAndExceptionSafe()
    {
        // Arrange
        var viewModel = (MainWindowViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        
        // Assert initial state
        var isApplyingField = typeof(MainWindowViewModel).GetProperty("_isApplyingSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var refCountField = typeof(MainWindowViewModel).GetField("_autoSavePauseRefCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        Assert.NotNull(isApplyingField);
        Assert.NotNull(refCountField);
        
        Assert.Equal(0, (int)refCountField.GetValue(viewModel)!);
        Assert.False((bool)isApplyingField.GetValue(viewModel)!);

        // Act & Assert: Single Scope
        using (viewModel.PauseAutoSave())
        {
            Assert.Equal(1, (int)refCountField.GetValue(viewModel)!);
            Assert.True((bool)isApplyingField.GetValue(viewModel)!);
        }

        // Assert after exit
        Assert.Equal(0, (int)refCountField.GetValue(viewModel)!);
        Assert.False((bool)isApplyingField.GetValue(viewModel)!);

        // Act & Assert: Nested Re-entrant Scopes
        using (viewModel.PauseAutoSave())
        {
            Assert.Equal(1, (int)refCountField.GetValue(viewModel)!);
            
            using (viewModel.PauseAutoSave())
            {
                Assert.Equal(2, (int)refCountField.GetValue(viewModel)!);
                Assert.True((bool)isApplyingField.GetValue(viewModel)!);
            }

            Assert.Equal(1, (int)refCountField.GetValue(viewModel)!);
            Assert.True((bool)isApplyingField.GetValue(viewModel)!);
        }

        Assert.Equal(0, (int)refCountField.GetValue(viewModel)!);
        Assert.False((bool)isApplyingField.GetValue(viewModel)!);

        // Act & Assert: Exception Safety
        try
        {
            using (viewModel.PauseAutoSave())
            {
                Assert.Equal(1, (int)refCountField.GetValue(viewModel)!);
                throw new InvalidOperationException("Simulated failure");
            }
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // Assert that state restored even after exception
        Assert.Equal(0, (int)refCountField.GetValue(viewModel)!);
        Assert.False((bool)isApplyingField.GetValue(viewModel)!);
    }

    [Fact]
    public void AddTab_And_ReorderTab_ShouldHandleMalformedAndOutOfBoundsParametersGracefully()
    {
        // Arrange
        var viewModel = (MainWindowViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        
        // Inject a null logger to prevent NullReferenceExceptions during logging
        var loggerField = typeof(MainWindowViewModel).GetField("_logger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(loggerField);
        loggerField.SetValue(viewModel, Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance);

        // Get methods
        var addTabMethod = typeof(MainWindowViewModel).GetMethod("AddTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var reorderTabMethod = typeof(MainWindowViewModel).GetMethod("ReorderTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(addTabMethod);
        Assert.NotNull(reorderTabMethod);

        // Act & Assert: Should not throw any exception under malformed formats
        
        // 1. Null/Empty
        addTabMethod.Invoke(viewModel, new object[] { null! });
        addTabMethod.Invoke(viewModel, new object[] { "" });
        addTabMethod.Invoke(viewModel, new object[] { "   " });

        // 2. Missing/Invalid colon
        addTabMethod.Invoke(viewModel, new object[] { "Left" });
        addTabMethod.Invoke(viewModel, new object[] { "Left:" });
        addTabMethod.Invoke(viewModel, new object[] { ":TabId" });

        // 3. Invalid region
        addTabMethod.Invoke(viewModel, new object[] { "InvalidRegion:TabId" });
        addTabMethod.Invoke(viewModel, new object[] { "99:TabId" }); // Numeric undefined enum validation

        // 4. Reorder tab malformed
        reorderTabMethod.Invoke(viewModel, new object[] { null! });
        reorderTabMethod.Invoke(viewModel, new object[] { "" });
        reorderTabMethod.Invoke(viewModel, new object[] { "Left" });
        reorderTabMethod.Invoke(viewModel, new object[] { "Left:1" });
        reorderTabMethod.Invoke(viewModel, new object[] { "Left:1:" });
        reorderTabMethod.Invoke(viewModel, new object[] { "InvalidRegion:1:2" });
        reorderTabMethod.Invoke(viewModel, new object[] { "99:1:2" }); // Numeric undefined enum validation
        reorderTabMethod.Invoke(viewModel, new object[] { "Left:abc:def" });

        // 5. Same index (from == to)
        reorderTabMethod.Invoke(viewModel, new object[] { "Left:1:1" });
    }
}

public class ChartViewModelComparisonTests
{
    [Fact]
    public void SymbolChanged_PreservesAllRegisteredComparisonTargets()
    {
        // Arrange
        var sut = new ChartViewModel();
        sut.ChartType = ChartType.RelativePerformance;
        sut.Symbol = "AAPL";
        sut.ComparisonTargets.Clear();
        sut.ComparisonTargets.Add("MSFT");
        sut.ComparisonTargets.Add("GOOG");

        // Act
        sut.Symbol = "MSFT";

        // Assert
        // All registered comparison targets should be completely preserved
        Assert.Equal(2, sut.ComparisonTargets.Count);
        Assert.Contains("MSFT", sut.ComparisonTargets);
        Assert.Contains("GOOG", sut.ComparisonTargets);
    }
}

public class MainWindowViewModelLayoutTests
{
    [Fact]
    public void HiddenPanel_WidthHeightSetters_DoNotThrowAndUpdateLastSize()
    {
        // Arrange
        var stateStore = new StockAnalyzer.Core.Models.UI.LayoutStateStore();
        var viewModel = (MainWindowViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        
        // Inject stateStore via _stateStore field
        var stateStoreField = typeof(MainWindowViewModel).GetField("_stateStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(stateStoreField);
        stateStoreField.SetValue(viewModel, stateStore);

        // Explicitly cast to interface
        var layoutTarget = (IWorkspaceLayoutTarget)viewModel;

        // Hide left panel
        layoutTarget.IsLeftPanelVisible = false;
        Assert.Equal(0.0, stateStore.LeftPanel.WidthOrHeight);
        Assert.False(stateStore.LeftPanel.IsVisible);

        // Act & Assert: Setting width on a hidden panel should update LastSize and NOT throw exceptions
        layoutTarget.LeftPanelWidth = 350.0;
        Assert.Equal(0.0, stateStore.LeftPanel.WidthOrHeight);
        Assert.Equal(350.0, stateStore.LeftPanel.LastSize);

        // Show left panel again -> should restore to LastSize (350.0)
        layoutTarget.IsLeftPanelVisible = true;
        Assert.Equal(350.0, stateStore.LeftPanel.WidthOrHeight);
    }
}