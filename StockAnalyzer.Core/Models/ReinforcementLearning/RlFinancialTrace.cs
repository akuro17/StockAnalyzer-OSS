using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Evidence version only; numerical contracts and existing generations stay immutable.</summary>
public static class RlFinancialParityContract
{
    public const string SpecificationId = "SA-RL-FINANCIAL-PARITY-R03-v1";
    public const string FailureFile = "financial_failure.json";
    public const int ReplayLayers = 3;
    public static JsonSerializerOptions TraceJson { get; } = CreateTraceJson();
    private static JsonSerializerOptions CreateTraceJson()
    {
        var options = new JsonSerializerOptions(RlFiles.Json);
        options.Converters.Add(new DecimalStringConverter());
        return options;
    }
    private sealed class DecimalStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String ? RlEvaluation.Parse(reader.GetString()!) : reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
            => writer.WriteStringValue(RlEvaluation.DecimalText(value));
    }
}

/// <summary>Core-owned projection, never an alternative accountant. Money crosses Python as decimal text.</summary>
public sealed record RlFinancialAccount(int MarketAnchorBarIndex, DateTime MarketAnchor, int? TerminalEventBarIndex,
    int CompletedDecisions, int RemainingDecisions, EquityPoint EquityPoint, decimal Quantity, decimal EntryPrice,
    decimal UnrealizedPnl, decimal PeakEquity, decimal Drawdown, decimal MaximumDrawdown, int HeldBars,
    bool PendingEntry, bool PendingExit, bool Terminated, RlTerminalReason TerminalReason, RunStatus Status,
    decimal? ActiveStopPrice, decimal RetainedStopDistance, bool DeferredMarketExit, ImmutableArray<decimal> AccountFields)
{
    public static RlFinancialAccount From(RlSessionSnapshot s) => new(s.MarketAnchorBarIndex, s.MarketAnchor, s.TerminalEventBarIndex,
        s.CompletedDecisions, s.RemainingDecisions, s.EquityPoint, s.Quantity, s.EntryPrice, s.UnrealizedPnL, s.PeakEquity,
        s.Drawdown, s.MaximumDrawdown, s.HeldBars, s.PendingEntry, s.PendingExit, s.Terminated, s.TerminalReason, s.Status,
        s.ActiveStopPrice, s.RetainedStopDistance, s.DeferredMarketExit, s.AccountFields);
}

public sealed record RlFinancialOrder(BacktestOrder Order, string Reason);
public sealed record RlFinancialTrade(BacktestTrade Trade, decimal EntrySlippage, decimal ExitSlippage,
    decimal MfePrice, decimal MaePrice, string EntrySignal, string ExitReason);
public sealed record RlFinancialTrace(RlAction Action, RlActionOutcome Outcome, RejectedReason? RejectionReason,
    decimal Reward, bool Terminated, bool Truncated, RlFinancialAccount Account,
    ImmutableArray<RlFinancialOrder> Orders, ImmutableArray<BacktestFill> Fills, ImmutableArray<RlFinancialTrade> Trades)
{
    public static RlFinancialTrace Capture(RlStepResult step, BacktestResult result) => new(step.Action, step.Outcome,
        step.RejectionReason, step.Reward, step.Terminated, step.Truncated, RlFinancialAccount.From(step.Snapshot),
        result.Orders.Select(o => new RlFinancialOrder(o, o.Reason)).ToImmutableArray(), result.Fills,
        result.Trades.Select(t => new RlFinancialTrade(t, t.EntrySlippage, t.ExitSlippage, t.MfePrice, t.MaePrice,
            t.EntrySignal, t.ExitReason)).ToImmutableArray());
    public JsonElement ToWire() => JsonSerializer.SerializeToElement(this, RlFinancialParityContract.TraceJson);
}
