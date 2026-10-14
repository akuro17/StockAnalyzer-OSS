using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// On a Point and Figure chart the Data tab follows the column under the pointer by its horizontal position alone, like the other charts that are not time
/// series: the column is reported whether the pointer is on its marks or above or below them. (Only the Data tab receives the crosshair message, so a
/// vertical condition could only make the tab fall back to the newest column.)
/// The chart publishes on the shared default messenger, so the test runs in the non-parallel collection for tests that use it.
/// </summary>
[Collection("MessengerSharedState")]
public class PointAndFigureHoverTests
{
    [AvaloniaFact]
    public void PointerAboveOrBelowAColumn_StillReportsThatColumn()
    {
        var vm = new ChartViewModel { Candles = SwingingCandles.Create(), EffectivePnfBoxSize = 1m, ChartType = ChartType.PointAndFigure };
        var chart = new ChartBaseControl { ViewModel = vm };
        var window = new Window { Width = 800, Height = 600, Content = chart };
        var recipient = new object();
        bool received = false;
        CoreCandleData? latest = null;
        WeakReferenceMessenger.Default.Register<object, CrosshairPositionChangedMessage>(recipient, (_, message) =>
        {
            if (message.Value.ChartSymbol == vm.Symbol)
            {
                received = true;
                latest = message.Value.HoveredCandle;
            }
        });
        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            HeadlessWindowInput.WaitUntilHittable(window, new Point(400, 300));

            var hovered = new List<CoreCandleData?>();
            foreach (double y in new[] { 10.0, 300.0, 590.0 })
            {
                received = false;
                window.MouseMove(new Point(400, y));

                Assert.True(received, $"no crosshair message for a pointer at y={y}");
                hovered.Add(latest);
            }

            Assert.All(hovered, candle => Assert.NotNull(candle));
            Assert.Single(hovered.Distinct());
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<CrosshairPositionChangedMessage>(recipient);
            window.MouseMove(new Point(-1, -1));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
