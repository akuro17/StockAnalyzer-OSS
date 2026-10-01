using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>
/// The equity hover texts are resolved from code, which the XAML-only <c>LocalizationKeyCoverageTests</c> does not see: every key
/// <see cref="EquityHoverText"/> actually asks for must exist in every shipped locale, or the chart would show a raw "[key]".
/// The keys are collected by running the text builders with a recording localizer, so the list cannot drift from the code.
/// </summary>
// Reads/changes the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class EquityHoverLocalizationTests
{
    private static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static HashSet<string> RequestedKeys()
    {
        var requested = new HashSet<string>();
        string Record(string key)
        {
            requested.Add(key);
            return key;
        }

        EquityHoverText.PointLines(new EquityPoint(0, Day0, 1m, 1m, 1m, 1m), Record);
        foreach (TradeSide side in new[] { TradeSide.Long, TradeSide.Short })
        {
            foreach (EquityMarkerKind kind in new[] { EquityMarkerKind.Entry, EquityMarkerKind.Exit })
            {
                var trade = new BacktestTradeRow
                {
                    TradeId = 1,
                    SideKind = side,
                    EntryTime = Day0,
                    EntryPrice = 1m,
                    ExitTime = Day0.AddDays(1),
                    ExitPrice = 2m,
                    Quantity = 1m,
                    ClosedNet = 1m,
                    PnLSemantic = BacktestMetricSemantic.Plus,
                    IsForcedLiquidation = true,
                };
                EquityHoverText.MarkerLines(trade, kind, isForcedLiquidation: true, Record);
            }
        }
        return requested;
    }

    [Fact]
    public void TheRecordedKeySet_IsNotEmpty_AndCoversTheCaptionsOfBothPanels()
    {
        HashSet<string> keys = RequestedKeys();

        Assert.Contains(EquityHoverText.DateKey, keys);
        Assert.Contains(EquityHoverText.ExitKey, keys);
        Assert.Contains("Btn_PositionShort", keys);
        Assert.Contains("Backtest_TradeList_ForcedLiquidation_Badge", keys);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryRequestedKey_ResolvesInTheLocale(string language)
    {
        string original = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            LocalizationManager.Instance.Initialize(language);

            foreach (string key in RequestedKeys().OrderBy(k => k, StringComparer.Ordinal))
            {
                string text = LocalizationManager.Instance.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: empty text for {key}");
                Assert.NotEqual($"[{key}]", text);
            }
        }
        finally
        {
            LocalizationManager.Instance.Initialize(original);
        }
    }
}
