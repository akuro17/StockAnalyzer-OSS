using System;
using System.Collections.Immutable;
using System.Globalization;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 17 acceptance tests for the equity-chart coordinate-transform layer's boundary conditions
/// (spec §5.7). <see cref="EquityCurveLayout"/> has zero Avalonia/Skia dependency by design precisely
/// so these can run as plain unit tests.
/// </summary>
public class EquityCurveLayoutBoundaryTests
{
    [Fact]
    public void EmptyEquity_EmptyState_Displayed()
    {
        EquityCurveLayout layout = EquityCurveLayout.Build(ImmutableArray<EquityPoint>.Empty);

        Assert.Equal(EquityCurveDisplayState.Empty, layout.State);
        Assert.Empty(layout.Points);
    }

    [Fact]
    public void SinglePoint_Equity_DotRendered()
    {
        var point = new EquityPoint(0, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100_000m, 100_000m, 0m, 0m);

        EquityCurveLayout layout = EquityCurveLayout.Build(ImmutableArray.Create(point));

        Assert.Equal(EquityCurveDisplayState.SinglePoint, layout.State);
        Assert.Single(layout.Points);
        // A single point has zero Y-range, so spec §5.7's "Y range == 0 -> +/-1 currency-unit band"
        // rule applies: the domain is widened symmetrically around the one equity value.
        Assert.Equal(99_999m, layout.YMin);
        Assert.Equal(100_001m, layout.YMax);
        Assert.Equal("100001.0000", layout.YMaxLabel);
        Assert.Equal("100000.0000", layout.YMidLabel);
        Assert.Equal("99999.0000", layout.YMinLabel);
        Assert.Equal("2024-01-01 00:00", layout.ViewportStartLabel);
        Assert.Equal("2024-01-01 00:00", layout.ViewportEndLabel);
    }

    /// <summary>
    /// SA_ARCHITECTURE_RULES.md §6 Culture Invariance: <see cref="EquityCurveLayout.Build"/> must format
    /// its labels with <see cref="CultureInfo.InvariantCulture"/> so a comma-decimal OS culture (e.g.
    /// de-DE) cannot change the rendered decimal/time separators (same guard pattern as
    /// <c>IndicatorParameterViewModelTests.Validate_ShouldAcceptValidNumbers_InCommaCulture</c>).
    /// </summary>
    [Fact]
    public void Labels_UseInvariantCulture_RegardlessOfCurrentCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // Uses comma decimal
            var point = new EquityPoint(0, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100_000m, 100_000m, 0m, 0m);

            EquityCurveLayout layout = EquityCurveLayout.Build(ImmutableArray.Create(point));

            Assert.Equal("100001.0000", layout.YMaxLabel);
            Assert.Equal("99999.0000", layout.YMinLabel);
            Assert.Equal("2024-01-01 00:00", layout.ViewportStartLabel);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// Drawdown greater than 100% of the initial peak (equity goes negative, e.g. after a forced-
    /// liquidation shortfall) must still render as a normal line - not fall back to Unavailable or
    /// clamp the domain to zero - since spec §5.7's overflow fallback is reserved for a genuine decimal
    /// range overflow, not merely a negative value.
    /// </summary>
    [Fact]
    public void NegativeEquity_DDGreaterThan1_Rendered()
    {
        DateTime start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ImmutableArray<EquityPoint> points = ImmutableArray.Create(
            new EquityPoint(0, start, 100_000m, 100_000m, 0m, 0m),
            new EquityPoint(1, start.AddDays(1), 50_000m, 50_000m, 0m, 0m),
            new EquityPoint(2, start.AddDays(2), -20_000m, -20_000m, 0m, 0m));

        EquityCurveLayout layout = EquityCurveLayout.Build(points);

        Assert.Equal(EquityCurveDisplayState.Line, layout.State);
        Assert.Equal(3, layout.Points.Length);
        Assert.Equal(-20_000m, layout.YMin);
        Assert.Equal(100_000m, layout.YMax);

        decimal drawdownRatio = (layout.YMax - layout.YMin) / layout.YMax;
        Assert.True(drawdownRatio > 1m, $"Expected drawdown ratio > 1 (100%), got {drawdownRatio}.");

        Assert.Equal(0.0, layout.Points[2].YFraction, precision: 10);
        Assert.Equal(1.0, layout.Points[0].YFraction, precision: 10);
    }

    [Fact]
    public void NonUtcDuplicateOrReverseTimestamps_AreUnavailable()
    {
        DateTime utc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Local);

        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(ImmutableArray.Create(
                new EquityPoint(0, local, 1m, 1m, 0m, 0m))).State);
        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(ImmutableArray.Create(
                new EquityPoint(0, utc, 1m, 1m, 0m, 0m),
                new EquityPoint(1, utc, 2m, 2m, 0m, 0m))).State);
        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(ImmutableArray.Create(
                new EquityPoint(0, utc.AddDays(1), 1m, 1m, 0m, 0m),
                new EquityPoint(1, utc, 2m, 2m, 0m, 0m))).State);
    }

    [Fact]
    public void DecimalRangeOrConstantBandOverflow_IsUnavailable()
    {
        DateTime utc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(ImmutableArray.Create(
                new EquityPoint(0, utc, decimal.MinValue, decimal.MinValue, 0m, 0m),
                new EquityPoint(1, utc.AddDays(1), decimal.MaxValue, decimal.MaxValue, 0m, 0m))).State);
        Assert.Equal(
            EquityCurveDisplayState.Unavailable,
            EquityCurveLayout.Build(ImmutableArray.Create(
                new EquityPoint(0, utc, decimal.MaxValue, decimal.MaxValue, 0m, 0m))).State);
    }

    [Fact]
    public void NearMaximumRange_UsesOverflowSafeMidpoint()
    {
        DateTime utc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        EquityCurveLayout layout = EquityCurveLayout.Build(ImmutableArray.Create(
            new EquityPoint(0, utc, decimal.MaxValue - 2m, decimal.MaxValue - 2m, 0m, 0m),
            new EquityPoint(1, utc.AddDays(1), decimal.MaxValue, decimal.MaxValue, 0m, 0m)));

        Assert.Equal(EquityCurveDisplayState.Line, layout.State);
        Assert.Equal((decimal.MaxValue - 1m).ToString("F4", CultureInfo.InvariantCulture), layout.YMidLabel);
        Assert.Equal(0d, layout.Points[0].YFraction);
        Assert.Equal(1d, layout.Points[1].YFraction);
    }
}
