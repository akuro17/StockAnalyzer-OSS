using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

[Collection("DrawingThemeContext State")]
public class FreehandChartInteractionTests
{
    [AvaloniaFact]
    public void RealChart_RepeatedMouseStrokes_CommitEachGesture()
    {
        var previousMode = DrawingThemeContext.DrawingToolContinuationMode;
        var candles = new List<CoreCandleData>();
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 40; i++)
            candles.Add(new CoreCandleData(time.AddDays(i), 100m, 110m, 90m, 105m, 1000));
        var vm = new ChartViewModel { Candles = candles, CurrentTool = DrawingTool.Freehand };
        vm.UpdateDrawingCoordinator();
        var chart = new ChartBaseControl { ViewModel = vm };
        var window = new Window { Width = 800, Height = 600, Content = chart };
        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            int expectedCount = 0;
            foreach (var mode in new[] { DrawingToolContinuationMode.ContinueDrawing, DrawingToolContinuationMode.ReturnToPointer })
            {
                DrawingThemeContext.SetDrawingToolContinuationModeForTesting(mode);
                for (int stroke = 0; stroke < 3; stroke++)
                {
                  Assert.Equal(DrawingTool.Freehand, vm.CurrentTool);
                  double x = 100 + stroke * 150;
                  window.MouseMove(new Point(x, 120));
                  window.MouseDown(new Point(x, 120), MouseButton.Left);
                  Assert.True(vm.DrawingEditCoordinator!.IsEditing,
                      $"Stroke {stroke + 1}: tool={vm.CurrentTool}, objects={vm.ObjectManager.Objects.Count}");
                  window.MouseMove(new Point(x + 20, 150));
                  AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                  window.MouseMove(new Point(x + 40, 130));
                  AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                  window.MouseUp(new Point(x + 60, 160), MouseButton.Left);
                  AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                  Assert.False(vm.DrawingEditCoordinator.IsEditing);
                  expectedCount++;
                  Assert.Equal(expectedCount, vm.ObjectManager.Objects.Count);
                  Assert.Equal(expectedCount, vm.DrawingEditCoordinator.UndoCount);
                  Assert.Equal(4, vm.ObjectManager.Objects[expectedCount - 1].Points.Count);
                  Assert.Equal(DrawingTool.Freehand, vm.CurrentTool);
              }
            }

            // --- Verification: Fixed Position (Dragging stroke does not move points or add Move history) ---
            var targetObj = (FreehandObject)vm.ObjectManager.Objects[0];
            var initialPoints = new List<ChartPoint>(targetObj.Points);
            int undoCountBefore = vm.DrawingEditCoordinator!.UndoCount;

            vm.CurrentTool = DrawingTool.Pointer;

            window.MouseMove(new Point(120, 150));
            window.MouseDown(new Point(120, 150), MouseButton.Left);
            window.MouseMove(new Point(200, 250));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.MouseUp(new Point(200, 250), MouseButton.Left);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(initialPoints.Count, targetObj.Points.Count);
            for (int i = 0; i < initialPoints.Count; i++)
            {
                Assert.Equal(initialPoints[i].Time, targetObj.Points[i].Time);
                Assert.Equal(initialPoints[i].Price, targetObj.Points[i].Price);
            }
            Assert.Equal(undoCountBefore, vm.DrawingEditCoordinator.UndoCount);

            // --- Verification: Shift-Click Deletion does not crash Skia and Undo restores safely ---
            int countBeforeDelete = vm.ObjectManager.Objects.Count;
            window.MouseMove(new Point(120, 150));
            window.MouseDown(new Point(120, 150), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(new Point(120, 150), MouseButton.Left, RawInputModifiers.Shift);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(countBeforeDelete - 1, vm.ObjectManager.Objects.Count);

            // Force render passes: must NOT throw AccessViolationException or ObjectDisposedException
            chart.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Undo deletion: object restored
            var undoRes = vm.DrawingEditCoordinator.Undo();
            Assert.True(undoRes.IsSuccess);
            Assert.Equal(countBeforeDelete, vm.ObjectManager.Objects.Count);

            // Force render pass on restored object: must render cleanly
            chart.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.MouseMove(new Point(-1, -1));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.DrawingEditCoordinator?.Dispose();
            DrawingThemeContext.SetDrawingToolContinuationModeForTesting(previousMode);
        }
    }
}
