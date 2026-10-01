using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Shared real-instance builders for the Results-tab metric presentation tests (no mocks): real generated reports and the real locale files.</summary>
internal static class BacktestReportTestFactory
{
    internal static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static BacktestTrade Trade(int id, decimal closedNet, int entryBar, int exitBar) => new(
        TradeId: id, Side: TradeSide.Long,
        EntryBar: entryBar, EntryTime: BaseUtc.AddDays(entryBar), EntryPrice: 100m,
        ExitBar: exitBar, ExitTime: BaseUtc.AddDays(exitBar), ExitPrice: 100m,
        Quantity: 1m,
        ClosedGross: closedNet, ClosedNet: closedNet,
        EntryFee: 0m, ExitFee: 0m,
        HoldingBars: exitBar - entryBar,
        IsForcedLiquidation: false);

    /// <summary>A real report generated from a run whose trades have the given net results (two bars each, back to back).</summary>
    internal static BacktestReport GenerateReport(params decimal[] closedNets)
    {
        const decimal initialCapital = 1000m;
        int barCount = Math.Max(2, closedNets.Length * 2 + 1);
        var points = ImmutableArray.CreateBuilder<EquityPoint>(barCount);
        var trades = ImmutableArray.CreateBuilder<BacktestTrade>(closedNets.Length);
        decimal equity = initialCapital;
        for (int bar = 0; bar < barCount; bar++)
        {
            int tradeIndex = (bar - 1) / 2;
            if (bar > 0 && bar % 2 == 0 && tradeIndex < closedNets.Length)
            {
                equity += closedNets[tradeIndex];
            }
            points.Add(new EquityPoint(bar, BaseUtc.AddDays(bar), equity, equity, 0m, 0m));
        }
        for (int i = 0; i < closedNets.Length; i++)
        {
            trades.Add(Trade(i + 1, closedNets[i], entryBar: i * 2, exitBar: i * 2 + 1));
        }

        var result = new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            trades.ToImmutable(),
            points.MoveToImmutable(),
            ImmutableArray<BacktestSignal>.Empty,
            new BacktestConfiguration
            {
                InitialCapital = initialCapital,
                SizingModel = PositionSizingModel.FixedQuantity,
                SizingParameter = 1m,
            },
            RunStatus.Completed,
            strategyName: "ExtendedMetricRows",
            reproducibilityHash: new byte[32],
            isInsufficientData: false);
        var options = new BacktestReportOptions(TimeFrame.D1, 0, BaseUtc, BaseUtc.AddDays(barCount + 1))
        {
            AnnualPeriods = 252,
        };
        return new BacktestReportGenerator().Generate(result, options);
    }

    /// <summary>Round-trips a generated report through JSON while replacing the given members (BacktestReport has init-only members).</summary>
    internal static BacktestReport WithMembers(BacktestReport report, IReadOnlyDictionary<string, JsonNode?> members)
    {
        JsonObject node = JsonNode.Parse(JsonSerializer.Serialize(report))!.AsObject();
        foreach ((string name, JsonNode? value) in members)
        {
            node[name] = value?.DeepClone();
        }
        return JsonSerializer.Deserialize<BacktestReport>(node.ToJsonString())!;
    }

    internal static JsonNode? Metric(decimal value, MetricUnit unit) =>
        JsonSerializer.SerializeToNode(MetricValue.Valid(value, unit));

    internal static FakeLocalizationService LoadLocale(string languageCode)
    {
        string path = Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{languageCode}.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var strings = document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
        return new FakeLocalizationService(strings);
    }

    internal static T WithCulture<T>(string cultureName, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
