using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>The settings dialog snaps a fixed-period range only when the period was changed, in the same history step (settings expansion Run 5).</summary>
public sealed class RobustRangeSnapSettingsTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Bars(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i % 9);
            list.Add(new CoreCandleData(Start.AddDays(i), close, close + 1m, close - 1m, close, 1000L));
        }

        return list;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly List<CoreCandleData> Candles = Bars(120);
        public readonly ChartObjectManager Manager = new();
        public readonly DrawingEditCoordinator Coordinator;
        public readonly RobustRegressionTrendObject Robust;

        public Fixture(int firstBar, int lastBar, int period)
        {
            var key = new DrawingDocumentKey("7203", TimeframeType.Daily);
            Coordinator = new DrawingEditCoordinator(Manager, new DrawingHistoryService(key), new DrawingDocumentSession(key));
            Robust = new RobustRegressionTrendObject(new ChartPoint(Candles[firstBar].Timestamp, 150m), new ChartPoint(Candles[lastBar].Timestamp, 150m)) { Period = period };
            Coordinator.ExecuteEdit(DrawingOperationKind.Add, () => Manager.AddObject(Robust));
        }

        public DrawingSettingsDialog OpenDialog()
        {
            var dialog = new DrawingSettingsDialog(Robust, new DrawingSettingsPanelRegistry(), onApply: null, coordinator: Coordinator, candles: Candles);
            dialog.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            return dialog;
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Robust.Dispose();
        }
    }

    private static void Click(DrawingSettingsDialog dialog, string buttonName)
    {
        dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static RobustRegressionTrendObject Current(Fixture f) => f.Manager.Objects.OfType<RobustRegressionTrendObject>().Single();

    [AvaloniaFact]
    public void ChangingThePeriod_AndPressingOk_SnapsTheEnd_AndUndoRestoresPeriodAndAnchorsTogether()
    {
        using var f = new Fixture(10, 39, period: 30);
        DrawingSettingsDialog dialog = f.OpenDialog();
        try
        {
            f.Robust.Period = 12;   // what the generic settings view does when the user edits the field
            Click(dialog, "OkButton");

            Assert.Equal(12, f.Robust.Period);
            Assert.Equal(f.Candles[10].Timestamp, f.Robust.Points[0].Time);   // the start is fixed
            Assert.Equal(f.Candles[21].Timestamp, f.Robust.Points[1].Time);   // the end follows: start + 12 - 1
            Assert.Equal(2, f.Coordinator.UndoCount);

            Assert.True(f.Coordinator.Undo().IsSuccess);
            RobustRegressionTrendObject undone = Current(f);
            Assert.Equal(30, undone.Period);
            Assert.Equal(f.Candles[39].Timestamp, undone.Points[1].Time);
            Assert.Equal(1, f.Coordinator.UndoCount);

            Assert.True(f.Coordinator.Redo().IsSuccess);
            RobustRegressionTrendObject redone = Current(f);
            Assert.Equal(12, redone.Period);
            Assert.Equal(f.Candles[21].Timestamp, redone.Points[1].Time);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void OkWithoutAPeriodChange_NeverMovesAnAnchor_EvenForALegacyLongRange()
    {
        using var f = new Fixture(10, 70, period: 30);   // 61 bars, an older range
        DateTime t0 = f.Robust.Points[0].Time;
        DateTime t1 = f.Robust.Points[1].Time;
        DrawingSettingsDialog dialog = f.OpenDialog();
        try
        {
            f.Robust.Color = Colors.Orange;   // a colour change only
            Click(dialog, "OkButton");

            Assert.Equal(t0, f.Robust.Points[0].Time);
            Assert.Equal(t1, f.Robust.Points[1].Time);
            Assert.Equal(30, f.Robust.Period);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Apply_SnapsOnce_AndALaterOkDoesNotSnapAgain()
    {
        using var f = new Fixture(10, 39, period: 30);
        DrawingSettingsDialog dialog = f.OpenDialog();
        try
        {
            f.Robust.Period = 8;
            Click(dialog, "ApplyButton");
            Assert.Equal(f.Candles[17].Timestamp, f.Robust.Points[1].Time);

            // The user drags the end by hand afterwards (outside the dialog); OK without another period change must leave it alone.
            f.Robust.Points[1] = new ChartPoint(f.Candles[50].Timestamp, 150m);
            Click(dialog, "OkButton");

            Assert.Equal(f.Candles[50].Timestamp, f.Robust.Points[1].Time);
            Assert.Equal(8, f.Robust.Period);
        }
        finally
        {
            dialog.Close();
        }
    }
}
