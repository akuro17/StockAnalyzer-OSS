using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>
/// The shared headless host of the equity chart tests: a 400x240 control in a window, real daily <see cref="EquityPoint"/> series, frame
/// settling and pointer helpers. One definition, so the viewport, hover and axis tests drive the control the same way.
/// </summary>
internal static class EquityChartTestHost
{
    public static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public const double ControlWidth = 400d;
    public const double ControlHeight = 240d;

    // The plot lies inside the BacktestUiConstants plot margins (the wide margin is the right one, holding the Y labels).
    public const double PlotLeft = BacktestUiConstants.PlotAxisMarginLeft;
    public const double PlotRight = ControlWidth - BacktestUiConstants.PlotAxisMarginRight;
    public const double PlotBottom = ControlHeight - BacktestUiConstants.PlotAxisMarginBottom;
    public static readonly Point InsidePlot = new(176d, 100d);
    public static readonly Point InAxisMargin = new(ControlWidth - (BacktestUiConstants.PlotAxisMarginRight / 2d), 100d);

    /// <summary>One point per day; the Equity wobbles on top of a rise so Y fits are never constant.</summary>
    public static ImmutableArray<EquityPoint> Daily(int count, int firstDayOffset = 0, decimal baseEquity = 1_000m) =>
        Enumerable.Range(0, count)
            .Select(i => new EquityPoint(i, Day0.AddDays(firstDayOffset + i), baseEquity + (i % 9) + i, (baseEquity / 2m) + i, 600m, 25m))
            .ToImmutableArray();

    public static (Window Window, EquityCurveControl Control) Show(ImmutableArray<EquityPoint> points, ImmutableArray<BacktestTradeRow> trades = default)
    {
        var control = new EquityCurveControl
        {
            Width = ControlWidth,
            Height = ControlHeight,
            ResultRevision = 1,
            EquityPoints = points,
            Trades = trades.IsDefault ? ImmutableArray<BacktestTradeRow>.Empty : trades,
        };
        var window = new Window { Width = 500, Height = 300, Content = control };
        window.Show();
        Settle();
        return (window, control);
    }

    // Pointer hit testing runs against the composed frame, which lags behind a layout change: render a few times so it has caught up.
    public static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static Point InWindow(Window window, Control control, Point controlPoint) =>
        control.TranslatePoint(controlPoint, window) ?? throw new InvalidOperationException("Control is not attached.");

    public static void MoveTo(Window window, Control control, Point controlPoint)
    {
        window.MouseMove(InWindow(window, control, controlPoint));
        Settle();
    }

    // A real pointer is over the control before it wheels; the headless device needs the same move to know its target.
    public static void Wheel(Window window, Control control, Point controlPoint, double deltaY)
    {
        Point point = InWindow(window, control, controlPoint);
        Visual? hit = window.GetVisualAt(point);
        Assert.Same(control, hit);
        window.MouseMove(point);
        window.MouseWheel(point, new Vector(0, deltaY));
    }
}
