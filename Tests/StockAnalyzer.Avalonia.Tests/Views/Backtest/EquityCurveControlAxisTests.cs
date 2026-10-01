using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Views.Backtest.EquityChartTestHost;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>Where the equity chart puts its axis labels: Y values in the right margin, X labels in month units like the main chart.</summary>
public class EquityCurveControlAxisTests
{
    private static EquityCurveControl ShowControl(int days, out Window window)
    {
        (window, EquityCurveControl control) = Show(Daily(days, baseEquity: 100_000m));
        return control;
    }

    [AvaloniaFact]
    public void YLabels_SitRightOfThePlot_AndNothingNumericIsLeftOfIt()
    {
        EquityCurveControl control = ShowControl(150, out Window window);
        try
        {
            double plotRight = ControlWidth - BacktestUiConstants.PlotAxisMarginRight;
            double plotBottom = ControlHeight - BacktestUiConstants.PlotAxisMarginBottom;
            var yLabels = control.RenderedAxisLabels.Where(l => l.Origin.Y < plotBottom).ToList();

            Assert.NotEmpty(yLabels);
            Assert.All(yLabels, l => Assert.Equal(plotRight + BacktestUiConstants.ChartLabelGap, l.Origin.X, precision: 6));
            Assert.All(yLabels, l => Assert.Matches(@"^-?\d+(\.\d+)?$", l.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void XLabels_AreMonthsLikeTheMainChart_NotDays()
    {
        EquityCurveControl control = ShowControl(150, out Window window);
        try
        {
            double plotBottom = ControlHeight - BacktestUiConstants.PlotAxisMarginBottom;
            var xLabels = control.RenderedAxisLabels.Where(l => l.Origin.Y >= plotBottom).ToList();

            Assert.True(xLabels.Count >= 2);
            Assert.All(xLabels, l => Assert.Matches(new Regex(@"^(\d{4}|[A-Z][a-z]{2})$"), l.Text));
            Assert.Contains(xLabels, l => l.Text is "Feb" or "Mar" or "Apr" or "May");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void XLabels_NeverReachIntoTheYLabelColumn()
    {
        foreach (int days in new[] { 40, 150, 400, 1500 })
        {
            EquityCurveControl control = ShowControl(days, out Window window);
            try
            {
                double plotRight = ControlWidth - BacktestUiConstants.PlotAxisMarginRight;
                double plotBottom = ControlHeight - BacktestUiConstants.PlotAxisMarginBottom;

                foreach ((string text, global::Avalonia.Point origin, double width) in control.RenderedAxisLabels.Where(l => l.Origin.Y >= plotBottom))
                {
                    Assert.True(origin.X >= 0d, $"{days} days: '{text}' starts left of the control");
                    Assert.True(origin.X + width <= plotRight + 1e-9, $"{days} days: '{text}' reaches into the Y label column");
                }
            }
            finally
            {
                window.Close();
            }
        }
    }
}
