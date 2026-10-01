using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>How <see cref="EquityCurveControl"/> turns the color style into geometry, and how it follows settings changes.</summary>
public class EquityCurveControlLineStyleTests
{
    private static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const decimal InitialCapital = 100m;

    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static BacktestEquityLineStyle Style(BacktestEquityColorMode mode) =>
        BacktestEquityLineStyle.FromSettings(new GlobalChartSettings { BacktestEquityColorMode = mode });

    private static EquityCurveControl CreateControl(
        ImmutableArray<EquityPoint> points,
        BacktestEquityLineStyle? style,
        ImmutableArray<bool>? flags = null) =>
        new()
        {
            Width = 400,
            Height = 240,
            ResultRevision = 1,
            EquityPoints = points,
            UnderwaterFlags = flags ?? EquityDrawdownClassifier.ComputeUnderwaterFlags(InitialCapital, points),
            LineStyle = style,
        };

    private static void Show(Window window)
    {
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static (EquityCurveControl Control, Window Window) Host(EquityCurveControl control)
    {
        var window = new Window { Width = 500, Height = 300, Content = control };
        Show(window);
        return (control, window);
    }

    // Drawn points (the initial capital 100 is not drawn): 110, 105, 108, 120, 119.
    private static readonly ImmutableArray<EquityPoint> Mixed = Points(110m, 105m, 108m, 120m, 119m);

    [AvaloniaFact]
    public void NoStyle_KeepsTheLegacySingleColorLine()
    {
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, style: null));
        try
        {
            Assert.False(control.RenderedIsSegmented);
            Assert.Equal((0, 0), control.RenderedSegmentFigureCounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SingleMode_DrawsOneLine()
    {
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, Style(BacktestEquityColorMode.Single)));
        try
        {
            Assert.False(control.RenderedIsSegmented);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PreviousBarMode_BuildsOneFigurePerRunOfSameDirection()
    {
        // segments between the drawn points 110,105,108,120,119: down, up, up, down -> up runs: [2,3] = 1 ; down runs: [1], [4] = 2
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, Style(BacktestEquityColorMode.PreviousBar)));
        try
        {
            Assert.True(control.RenderedIsSegmented);
            Assert.Equal((1, 2), control.RenderedSegmentFigureCounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DrawdownMode_BuildsOneFigurePerRunOfSameClass()
    {
        // E[0..6] = 100,110,105,103,110,115,114 -> segments (by end point): down, down, up, up, down => up runs 1, down runs 2.
        ImmutableArray<EquityPoint> points = Points(110m, 105m, 103m, 110m, 115m, 114m);
        (EquityCurveControl control, Window window) = Host(CreateControl(points, Style(BacktestEquityColorMode.Drawdown)));
        try
        {
            Assert.True(control.RenderedIsSegmented);
            Assert.Equal((1, 2), control.RenderedSegmentFigureCounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DrawdownMode_OnlyNewHighs_IsOneUpFigureAndNoDownFigure()
    {
        (EquityCurveControl control, Window window) = Host(CreateControl(Points(101m, 102m, 103m), Style(BacktestEquityColorMode.Drawdown)));
        try
        {
            Assert.Equal((1, 0), control.RenderedSegmentFigureCounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DrawdownMode_WithUnavailableFlags_DegradesToSingleColorWithoutThrowing()
    {
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, Style(BacktestEquityColorMode.Drawdown), flags: default(ImmutableArray<bool>)));
        try
        {
            Assert.False(control.RenderedIsSegmented);
            Assert.Equal((0, 0), control.RenderedSegmentFigureCounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DrawdownMode_WithMismatchedFlagLength_DegradesToSingleColor()
    {
        (EquityCurveControl control, Window window) = Host(
            CreateControl(Mixed, Style(BacktestEquityColorMode.Drawdown), flags: ImmutableArray.Create(false, true)));
        try
        {
            Assert.False(control.RenderedIsSegmented);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SinglePointResult_RendersAsADot_InEveryMode_WithoutThrowing()
    {
        foreach (BacktestEquityColorMode mode in Enum.GetValues<BacktestEquityColorMode>())
        {
            (EquityCurveControl control, Window window) = Host(CreateControl(Points(120m), Style(mode)));
            try
            {
                Assert.True(control.SnapshotBuildCount > 0);
                Assert.False(control.RenderedIsSegmented);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void SnapshotBuildCount_SameStyleValue_DoesNotRebuild_ChangedStyle_RebuildsOnce()
    {
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, Style(BacktestEquityColorMode.PreviousBar)));
        try
        {
            int builds = control.SnapshotBuildCount;

            control.LineStyle = Style(BacktestEquityColorMode.PreviousBar); // equal value, new instance
            Settle();
            Assert.Equal(builds, control.SnapshotBuildCount);

            control.LineStyle = Style(BacktestEquityColorMode.Single);
            Settle();
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
            Assert.False(control.RenderedIsSegmented);
        }
        finally { window.Close(); }
    }

    private sealed class ServicesScope : IDisposable
    {
        private static readonly PropertyInfo ServicesProperty = typeof(App).GetProperty(nameof(App.Services))!;
        private readonly object? _previous = ServicesProperty.GetValue(App.Current);

        public ServicesScope(IChartSettingsManager manager)
        {
            var services = new ServiceCollection();
            services.AddSingleton(manager);
            ServicesProperty.SetValue(App.Current, services.BuildServiceProvider());
        }

        public void Dispose() => ServicesProperty.SetValue(App.Current, _previous);
    }

    [AvaloniaFact]
    public void Attach_SubscribesAndReadsTheCurrentStyle_Detach_Unsubscribes()
    {
        var manager = new FakeChartSettingsManager();
        manager.Publish(new GlobalChartSettings { BacktestEquityColorMode = BacktestEquityColorMode.PreviousBar });
        using var scope = new ServicesScope(manager);
        var control = CreateControl(Mixed, style: null);
        var window = new Window { Width = 500, Height = 300, Content = control };
        try
        {
            Assert.Equal(0, manager.SubscriberCount);

            Show(window);

            Assert.Equal(1, manager.SubscriberCount);
            Assert.Equal(BacktestEquityLineStyle.FromSettings(manager.Current), control.LineStyle);
            Assert.True(control.RenderedIsSegmented);

            window.Content = null;
            Settle();

            Assert.Equal(0, manager.SubscriberCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsChange_UpdatesTheOpenControl_ButAnUnrelatedChangeDoesNotRebuild()
    {
        var manager = new FakeChartSettingsManager();
        using var scope = new ServicesScope(manager);
        (EquityCurveControl control, Window window) = Host(CreateControl(Mixed, style: null));
        try
        {
            Assert.Equal(BacktestEquityColorMode.Drawdown, control.LineStyle!.Mode);
            int builds = control.SnapshotBuildCount;

            manager.Publish(manager.Current with { TopMargin = manager.Current.TopMargin + 1f });
            Settle();
            Assert.Equal(builds, control.SnapshotBuildCount);

            manager.Publish(manager.Current with { BacktestEquityColorMode = BacktestEquityColorMode.Single });
            Settle();
            Assert.Equal(BacktestEquityColorMode.Single, control.LineStyle!.Mode);
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
            Assert.False(control.RenderedIsSegmented);

            manager.Publish(manager.Current with { BacktestEquityLineColor = "#FF112233" });
            Settle();
            Assert.Equal(builds + 2, control.SnapshotBuildCount);
        }
        finally { window.Close(); }
    }
}
