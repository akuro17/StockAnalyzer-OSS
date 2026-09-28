using System;
using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

public class EquityCurveControlCacheTests
{
    [AvaloniaFact]
    public void RepeatedRenderWithSameKey_DoesNotRebuildSnapshot()
    {
        DateTime utc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var control = new EquityCurveControl
        {
            Width = 400,
            Height = 240,
            ResultRevision = 1,
            EquityPoints = ImmutableArray.Create(
                new EquityPoint(0, utc, 100m, 100m, 0m, 0m),
                new EquityPoint(1, utc.AddDays(1), 110m, 110m, 0m, 0m)),
        };
        var window = new Window { Width = 500, Height = 300, Content = control };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            int initialBuilds = control.SnapshotBuildCount;
            Assert.True(initialBuilds > 0);

            control.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(initialBuilds, control.SnapshotBuildCount);

            control.ResultRevision++;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(initialBuilds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LocaleReload_RebuildsSnapshotWithoutResultChange()
    {
        var control = new EquityCurveControl { Width = 400, Height = 240 };
        var window = new Window { Width = 500, Height = 300, Content = control };
        string originalLanguage = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            int builds = control.SnapshotBuildCount;

            LocalizationManager.Instance.Initialize("ja");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(builds + 1, control.SnapshotBuildCount);

            control.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(builds + 1, control.SnapshotBuildCount);
        }
        finally
        {
            window.Close();
            LocalizationManager.Instance.Initialize(originalLanguage);
        }
    }
}
